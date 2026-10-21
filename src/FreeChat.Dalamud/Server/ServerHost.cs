using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FreeChat.Dalamud.Logging;
using FreeChat.Protocol.V1;
using FreeChat.Server;
using Lumina.Excel.Sheets;

namespace FreeChat.Dalamud;

public sealed class ServerHost : IDisposable
{
    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly UiLog uiLog;
    private readonly IChatSink chatSink;
    private readonly ISocialDataProvider? social;
    private string? lastCharacterKey;
    private string? advertisedHost;

    public ServerHost(
        Configuration configuration,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        UiLog uiLog,
        IChatSink chatSink,
        ISocialDataProvider? social = null)
    {
        this.configuration = configuration;
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.uiLog = uiLog;
        this.chatSink = chatSink;
        this.social = social;
        this.Approval = new PairingApprovalService();
    }

    public PairingApprovalService Approval { get; }
    public FreeChatServer? Server { get; private set; }
    public string? LastError { get; private set; }
    public bool CertificateError { get; private set; }
    public bool IsRunning => this.Server is not null;

    public bool Start()
    {
        this.Stop();
        this.LastError = null;
        this.CertificateError = false;
        this.advertisedHost = null;

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
                MaxClients = Math.Clamp(this.configuration.MaxClients, 1, 128),
                SendRateLimitPerSecond = Math.Clamp(this.configuration.SendRateLimitPerSecond, 1, 100),
                SendRateLimitBurst = Math.Clamp(this.configuration.SendRateLimitBurst, 1, 200),
            };

            var server = new FreeChatServer(options, this.chatSink, this.Approval, social: this.social);
            server.StartAsync().GetAwaiter().GetResult();
            this.Server = server;
            this.lastCharacterKey = null;
            this.log.Information(
                "FreeChat server listening on {Address}:{Port}",
                options.BindAddress,
                server.Port);
            this.uiLog.Add(
                UiLogLevel.Info,
                $"Server listening on {options.BindAddress}:{server.Port}");
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.CertificateError = ex is CryptographicException;
            this.log.Error(ex, "Could not start FreeChat server.");
            this.uiLog.Add(UiLogLevel.Error, $"Server start failed: {ex.Message}");
            this.Server = null;
            return false;
        }
    }

    public bool ResetCertificate()
    {
        this.Stop();
        this.LastError = null;
        this.CertificateError = false;

        try
        {
            var directory = this.pluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var certificatePath = Path.Combine(directory, "freechat.pfx");
            if (File.Exists(certificatePath))
            {
                var quarantine = certificatePath
                                 + ".corrupt-"
                                 + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff");
                File.Move(certificatePath, quarantine, overwrite: false);
                this.uiLog.Add(UiLogLevel.Warning, $"Certificate quarantined: {quarantine}");
            }

            if (this.Start())
            {
                this.uiLog.Add(UiLogLevel.Info, "A new TLS certificate was generated.");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.CertificateError = ex is CryptographicException;
            this.log.Error(ex, "Could not reset the FreeChat certificate.");
            this.uiLog.Add(UiLogLevel.Error, $"Certificate reset failed: {ex.Message}");
            this.Server = null;
            return false;
        }
    }

    public bool Restart() => this.Start();

    public string AdvertisedHost
    {
        get
        {
            if (!this.configuration.AllowLan)
                return IPAddress.Loopback.ToString();

            return this.advertisedHost ??= ResolveAdvertisedHost();
        }
    }

    private static string ResolveAdvertisedHost()
    {
        try
        {
            var address = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(x =>
                    x.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(x));
            if (address is not null)
                return address.ToString();
        }
        catch
        {
            // Fall back to loopback when the LAN address cannot be resolved.
        }

        return IPAddress.Loopback.ToString();
    }

    public string? PairingLink
    {
        get
        {
            var server = this.Server;
            if (server is null)
                return null;

            var fingerprint = Convert.ToHexString(server.Fingerprint).ToLowerInvariant();
            return "freechat://pair"
                   + $"?v=1&host={Uri.EscapeDataString(this.AdvertisedHost)}"
                   + $"&port={server.Port}"
                   + $"&sid={Uri.EscapeDataString(server.ServerId)}"
                   + $"&fp={fingerprint}"
                   + $"&code={server.Pairing.CurrentCode}";
        }
    }

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
            this.uiLog.Add(UiLogLevel.Warning, $"Server stop failed: {ex.Message}");
        }

        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        this.uiLog.Add(UiLogLevel.Info, "Server stopped.");
    }

    public void Dispose() => this.Stop();
}
