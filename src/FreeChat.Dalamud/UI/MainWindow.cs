using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FreeChat.Server;

namespace FreeChat.Dalamud.UI;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string testText = "/echo FreeChat test";

    public MainWindow(Plugin plugin)
        : base("FreeChat##FreeChatMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;
        this.Size = new Vector2(560, 480);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var config = this.plugin.Configuration;
        var host = this.plugin.ServerHost;
        var server = host.Server;

        ImGui.TextUnformatted("FreeChat game-side server");
        ImGui.Separator();

        if (server is null)
        {
            ImGui.TextWrapped(host.LastError ?? "Server is not running.");
            if (ImGui.Button("Start server"))
                host.Start();
            return;
        }

        ImGui.TextUnformatted($"Listening: {server.Options.BindAddress}:{server.Port}");
        ImGui.TextUnformatted($"Character: {server.Character?.Name ?? "(not logged in)"}");

        var stats = this.plugin.ChatCapture.Statistics;
        ImGui.TextUnformatted(
            $"Captured: pre={stats.PreHandle} post={stats.PostHandle} structured={stats.Structured}");
        ImGui.TextDisabled(
            stats.LastEventAt is { } last
                ? $"Last event: {last.ToLocalTime():HH:mm:ss}"
                : "Last event: none");

        var fingerprint = TlsCertificate.Hex(server.Fingerprint);
        ImGui.TextUnformatted("Fingerprint:");
        ImGui.SameLine();
        ImGui.TextWrapped(fingerprint);
        ImGui.SameLine();
        if (ImGui.Button("Copy##fingerprint"))
            ImGui.SetClipboardText(fingerprint);

        ImGui.TextUnformatted("Pairing code:");
        ImGui.SameLine();
        ImGui.TextUnformatted(server.Pairing.CurrentCode);
        ImGui.SameLine();
        if (ImGui.Button("Copy##pairing"))
            ImGui.SetClipboardText(server.Pairing.CurrentCode);

        ImGui.SameLine();
        if (ImGui.Button("New code##pairing"))
            server.Pairing.GenerateCode();

        this.DrawPendingApprovals();
        this.DrawDevices(server);
        this.DrawSettings(config, host);

        ImGui.Separator();
        ImGui.TextUnformatted("Test send");
        ImGui.InputText("##freechat-test-send", ref this.testText, 500);
        if (ImGui.Button("Send") && !string.IsNullOrWhiteSpace(this.testText))
            _ = this.plugin.ChatSendService.SendTestAsync(this.testText);

        ImGui.TextWrapped($"Last send: {this.plugin.ChatSendService.LastStatus ?? "(none)"}");
        ImGui.TextWrapped($"Last echo: {this.plugin.ChatSendService.LastEcho ?? "(none)"}");
    }

    private void DrawPendingApprovals()
    {
        var pending = this.plugin.ServerHost.Approval.Pending;
        if (pending.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted("Pending pairing requests");
        foreach (var approval in pending)
        {
            ImGui.PushID((int)approval.Id);
            ImGui.TextWrapped(
                $"{approval.Request.DeviceName} ({approval.Request.Platform})");
            ImGui.TextWrapped($"Fingerprint: {approval.Fingerprint}");
            if (ImGui.Button("Accept##approval"))
                this.plugin.ServerHost.Approval.Resolve(approval.Id, true);
            ImGui.SameLine();
            if (ImGui.Button("Deny##approval"))
                this.plugin.ServerHost.Approval.Resolve(approval.Id, false);
            ImGui.PopID();
        }
    }

    private void DrawDevices(FreeChatServer server)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Trusted devices");

        if (ImGui.BeginTable("##freechat-devices", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Platform");
            ImGui.TableSetupColumn("Last seen");
            ImGui.TableSetupColumn("Actions");
            ImGui.TableHeadersRow();

            foreach (var device in server.Devices.List())
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(device.Name);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(device.Platform);
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(device.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                ImGui.TableSetColumnIndex(3);
                ImGui.PushID(device.DeviceId);
                if (ImGui.Button("Revoke"))
                    server.Devices.Revoke(device.DeviceId);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawSettings(Configuration config, ServerHost host)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Settings");

        var port = config.Port;
        if (ImGui.InputInt("Port", ref port, 1, 100))
            config.Port = Math.Clamp(port, 1, 65535);

        var lan = config.AllowLan;
        if (ImGui.Checkbox("Allow LAN connections", ref lan))
            config.AllowLan = lan;

        var json = config.EnableJsonDebug;
        if (ImGui.Checkbox("Enable JSON debug transport", ref json))
            config.EnableJsonDebug = json;

        if (ImGui.Button("Save and restart"))
        {
            config.Save();
            host.Restart();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Changes apply after Save and restart.");
    }
}
