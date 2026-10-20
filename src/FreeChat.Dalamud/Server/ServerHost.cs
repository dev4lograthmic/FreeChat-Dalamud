using System.Net;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FreeChat.Protocol.V1;
using FreeChat.Server;
using Lumina.Excel.Sheets;

namespace FreeChat.Dalamud;

public sealed class ServerHost : IDisposable
{
    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private string? lastCharacterKey;

    public ServerHost(
        Configuration configuration,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.Approval = new PairingApprovalService();
    }

    public PairingApprovalService Approval { get; }
    public FreeChatServer? Server { get; private set; }
    public string? LastError { get; private set; }
    public bool IsRunning => this.Server is not null;

    public bool Start()
    {
        this.Stop();
        this.LastError = null;

        try
        {
            var directory = this.pluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var options = new ServerOptions
            {
                BindAddress = this.configuration.AllowLan ? IPAddress.Any : IPAddress.Loopback,
                Port = this.configuration.Port,
                ServerId = this.configuration.ServerId,
                ServerName = this.configuration.ServerName,
                CertificatePath = Path.Combine(directory, "freechat.pfx"),
                DeviceStorePath = Path.Combine(directory, "freechat-devices.json"),
                EnableJsonDebug = this.configuration.EnableJsonDebug,
                BacklogMessages = Math.Max(100, this.configuration.BacklogMessages),
                BacklogBytes = Math.Max(1, this.configuration.BacklogMiB) * 1024L * 1024,
            };

            var server = new FreeChatServer(options, new UnavailableChatSink(), this.Approval);
            server.StartAsync().GetAwaiter().GetResult();
            this.Server = server;
            this.lastCharacterKey = null;
            this.log.Information(
                "FreeChat server listening on {Address}:{Port}",
                options.BindAddress,
                server.Port);
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.log.Error(ex, "Could not start FreeChat server.");
            this.Server = null;
            return false;
        }
    }

    public bool Restart() => this.Start();

    public void UpdateCharacter(IPlayerCharacter? player)
    {
        var server = this.Server;
        if (server is null)
            return;

        if (player is null)
        {
            this.lastCharacterKey = null;
            server.UpdateCharacter(null, null);
            return;
        }

        var name = player.Name.TextValue;
        var worldId = player.HomeWorld.RowId;
        var worldName = player.HomeWorld.Value.Name.ExtractText();
        var key = $"{name}@{worldId}";
        if (key == this.lastCharacterKey)
            return;

        this.lastCharacterKey = key;
        server.UpdateCharacter(new CharacterRef
        {
            Name = name,
            WorldId = worldId,
            WorldName = worldName,
            ContentId = 0,
            IsPlayer = true,
            IsLocal = true,
        }, key);
    }

    public void Publish(ChatEvent chatEvent) => this.Server?.PublishChatEvent(chatEvent);

    public Task ResetSessionAsync(string reason) =>
        this.Server?.ResetSessionAsync(reason) ?? Task.CompletedTask;

    public void Stop()
    {
        var server = this.Server;
        this.Server = null;
        this.lastCharacterKey = null;
        if (server is null)
            return;

        try
        {
            server.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "Error while stopping FreeChat server.");
        }

        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public void Dispose() => this.Stop();
}
