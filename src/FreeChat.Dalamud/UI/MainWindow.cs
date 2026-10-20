using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FreeChat.Dalamud.Logging;
using FreeChat.Protocol.V1;
using FreeChat.Server;

namespace FreeChat.Dalamud.UI;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly (ChatChannel Channel, string Key)[] SendChannels =
    [
        (ChatChannel.Unspecified, "channel.raw"),
        (ChatChannel.Say, "channel.say"),
        (ChatChannel.Party, "channel.party"),
        (ChatChannel.Shout, "channel.shout"),
        (ChatChannel.Yell, "channel.yell"),
        (ChatChannel.Alliance, "channel.alliance"),
        (ChatChannel.FreeCompany, "channel.freeCompany"),
        (ChatChannel.Linkshell1, "channel.linkshell1"),
        (ChatChannel.Linkshell2, "channel.linkshell2"),
        (ChatChannel.Linkshell3, "channel.linkshell3"),
        (ChatChannel.Linkshell4, "channel.linkshell4"),
        (ChatChannel.Linkshell5, "channel.linkshell5"),
        (ChatChannel.Linkshell6, "channel.linkshell6"),
        (ChatChannel.Linkshell7, "channel.linkshell7"),
        (ChatChannel.Linkshell8, "channel.linkshell8"),
        (ChatChannel.CrossLinkshell1, "channel.crossLinkshell1"),
        (ChatChannel.CrossLinkshell2, "channel.crossLinkshell2"),
        (ChatChannel.CrossLinkshell3, "channel.crossLinkshell3"),
        (ChatChannel.CrossLinkshell4, "channel.crossLinkshell4"),
        (ChatChannel.CrossLinkshell5, "channel.crossLinkshell5"),
        (ChatChannel.CrossLinkshell6, "channel.crossLinkshell6"),
        (ChatChannel.CrossLinkshell7, "channel.crossLinkshell7"),
        (ChatChannel.CrossLinkshell8, "channel.crossLinkshell8"),
        (ChatChannel.NoviceNetwork, "channel.novice"),
        (ChatChannel.PvpTeam, "channel.pvp"),
        (ChatChannel.Tell, "channel.tell"),
    ];

    private readonly Plugin plugin;
    private readonly Localization.Localizer l10n;

    private string testText = "/echo FreeChat test";
    private string targetName = string.Empty;
    private string targetWorld = string.Empty;
    private string blockedText;
    private string logFilter = string.Empty;
    private string? settingsMessage;
    private string? qrText;
    private QrCodeMatrix? qrCode;
    private int sendChannelIndex;
    private bool showDebug = true;
    private bool showInfo = true;
    private bool showWarning = true;
    private bool showError = true;

    public MainWindow(Plugin plugin)
        : base("FreeChat##FreeChatMain")
    {
        this.plugin = plugin;
        this.l10n = plugin.Localizer;
        plugin.Configuration.BlockedCommands ??= new List<string>(CommandPolicy.DefaultBlocked);
        this.blockedText = string.Join(Environment.NewLine, plugin.Configuration.BlockedCommands);
        this.Size = new Vector2(700, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.IsOpen = true;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        ImGui.TextUnformatted(this.l10n["app.title"]);
        ImGui.SameLine();
        ImGui.TextDisabled(this.l10n["app.subtitle"]);
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##freechat-tabs"))
            return;

        if (ImGui.BeginTabItem(this.l10n["tab.status"]))
        {
            this.DrawStatus();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(this.l10n["tab.pairing"]))
        {
            this.DrawPairing();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(this.l10n["tab.devices"]))
        {
            this.DrawDevices();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(this.l10n["tab.settings"]))
        {
            this.DrawSettings();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem(this.l10n["tab.logs"]))
        {
            this.DrawLogs();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawStatus()
    {
        var host = this.plugin.ServerHost;
        var server = host.Server;

        if (server is null)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.35f, 1f), this.l10n["status.stopped"]);
            if (!string.IsNullOrEmpty(host.LastError))
                ImGui.TextWrapped(this.l10n.Get("status.error", host.LastError));
            if (ImGui.Button(this.l10n["status.start"]))
                host.Start();
            return;
        }

        ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.45f, 1f), this.l10n["status.running"]);
        ImGui.SameLine();
        if (ImGui.Button(this.l10n["status.stop"]))
            host.Stop();
        ImGui.SameLine();
        if (ImGui.Button(this.l10n["status.restart"]))
            host.Restart();

        ImGui.TextUnformatted(this.l10n.Get(
            "status.address",
            $"{server.Options.BindAddress}:{server.Port}"));

        var character = server.Character;
        var characterText = character is { Name.Length: > 0 }
            ? $"{character.Name}@{character.WorldName}"
            : this.l10n["status.notLoggedIn"];
        ImGui.TextUnformatted(this.l10n.Get("status.character", characterText));

        var stats = this.plugin.ChatCapture.Statistics;
        ImGui.TextUnformatted(this.l10n.Get(
            "status.captured",
            stats.PreHandle,
            stats.PostHandle,
            stats.Structured));
        ImGui.TextDisabled(stats.LastEventAt is { } last
            ? this.l10n.Get("status.lastEvent", last.ToLocalTime().ToString("HH:mm:ss"))
            : this.l10n.Get("status.lastEvent", this.l10n["common.none"]));

        ImGui.Separator();
        if (ImGui.CollapsingHeader(this.l10n["status.connections"], ImGuiTreeNodeFlags.DefaultOpen))
            this.DrawSessions(server);

        ImGui.Separator();
        this.DrawTestSend();
    }

    private void DrawSessions(FreeChatServer server)
    {
        var sessions = server.Sessions;
        if (sessions.Count == 0)
        {
            ImGui.TextDisabled(this.l10n["status.noConnections"]);
            return;
        }

        if (!ImGui.BeginTable(
                "##freechat-sessions",
                5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn(this.l10n["status.sessionDevice"]);
        ImGui.TableSetupColumn(this.l10n["status.sessionPlatform"]);
        ImGui.TableSetupColumn(this.l10n["status.sessionRemote"]);
        ImGui.TableSetupColumn(this.l10n["status.sessionState"]);
        ImGui.TableSetupColumn(this.l10n["status.sessionActions"]);
        ImGui.TableHeadersRow();

        foreach (var session in sessions)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(session.DeviceName ?? session.DeviceId ?? this.l10n["status.sessionPending"]);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(session.Platform ?? "-");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(session.RemoteAddress ?? "-");
            ImGui.TableSetColumnIndex(3);
            ImGui.TextUnformatted(!session.Authenticated
                ? this.l10n["status.sessionConnected"]
                : session.Subscribed
                    ? this.l10n["status.sessionSubscribed"]
                    : this.l10n["status.sessionAuthenticated"]);
            ImGui.TableSetColumnIndex(4);
            ImGui.PushID(session.SessionId);
            if (ImGui.Button(this.l10n["common.disconnect"]))
                server.DisconnectSession(session.SessionId);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawTestSend()
    {
        ImGui.TextUnformatted(this.l10n["send.title"]);

        var current = SendChannels[this.sendChannelIndex];
        if (ImGui.BeginCombo(this.l10n["send.channel"], this.l10n[current.Key]))
        {
            for (var i = 0; i < SendChannels.Length; i++)
            {
                if (ImGui.Selectable(this.l10n[SendChannels[i].Key], i == this.sendChannelIndex))
                    this.sendChannelIndex = i;
            }

            ImGui.EndCombo();
        }

        if (current.Channel == ChatChannel.Tell)
        {
            ImGui.InputText(this.l10n["send.targetName"], ref this.targetName, 64);
            ImGui.InputText(this.l10n["send.targetWorld"], ref this.targetWorld, 64);
        }

        ImGui.InputText(this.l10n["send.text"], ref this.testText, 500);
        if (ImGui.Button(this.l10n["send.send"]) && !string.IsNullOrWhiteSpace(this.testText))
        {
            _ = this.plugin.ChatSendService.SendTestAsync(
                this.testText,
                current.Channel,
                this.targetName,
                this.targetWorld);
        }

        var result = this.plugin.ChatSendService.LastResult;
        var status = result is null
            ? this.l10n["common.none"]
            : this.l10n[$"send.status.{result.Status}"]
              + (string.IsNullOrEmpty(result.Message) ? string.Empty : $": {result.Message}");
        ImGui.TextWrapped(this.l10n.Get("send.lastStatus", status));
        ImGui.TextWrapped(this.l10n.Get(
            "send.lastEcho",
            this.plugin.ChatSendService.LastEcho ?? this.l10n["common.none"]));
    }

    private void DrawPairing()
    {
        var host = this.plugin.ServerHost;
        var server = host.Server;
        if (server is null)
        {
            ImGui.TextDisabled(this.l10n["pairing.unavailable"]);
            return;
        }

        ImGui.TextUnformatted(this.l10n["pairing.code"]);
        ImGui.SameLine();
        ImGui.TextUnformatted(server.Pairing.CurrentCode);
        ImGui.SameLine();
        if (ImGui.Button(this.l10n["pairing.copyCode"]))
            ImGui.SetClipboardText(server.Pairing.CurrentCode);
        ImGui.SameLine();
        if (ImGui.Button(this.l10n["pairing.newCode"]))
            server.Pairing.GenerateCode();

        ImGui.TextDisabled(this.l10n.Get(
            "pairing.expires",
            server.Pairing.CodeExpiresAt.ToLocalTime().ToString("HH:mm:ss")));

        var fingerprint = TlsCertificate.Hex(server.Fingerprint);
        ImGui.TextUnformatted(this.l10n["pairing.fingerprint"]);
        ImGui.TextWrapped(fingerprint);
        ImGui.SameLine();
        if (ImGui.Button(this.l10n["common.copy"] + "##fingerprint"))
            ImGui.SetClipboardText(fingerprint);

        var link = host.PairingLink;
        if (!string.IsNullOrEmpty(link))
        {
            ImGui.TextUnformatted(this.l10n["pairing.deepLink"]);
            ImGui.InputText("##freechat-pairing-link", ref link, 2048, ImGuiInputTextFlags.ReadOnly);
            if (ImGui.Button(this.l10n["pairing.copyLink"]))
                ImGui.SetClipboardText(link);
            ImGui.TextUnformatted(this.l10n["pairing.qr"]);
            this.DrawQrCode(link);
        }

        ImGui.TextDisabled(this.l10n["pairing.note"]);

        ImGui.Separator();
        this.DrawPendingApprovals();
    }

    private void DrawQrCode(string link)
    {
        if (!string.Equals(this.qrText, link, StringComparison.Ordinal))
        {
            this.qrText = link;
            try
            {
                this.qrCode = QrCodeMatrix.Create(link);
            }
            catch (Exception ex)
            {
                this.qrCode = null;
                this.plugin.UiLog.Add(UiLogLevel.Warning, $"QR generation failed: {ex.Message}");
            }
        }

        var qr = this.qrCode;
        if (qr is null)
            return;

        const float moduleSize = 4f;
        const int quietZone = 2;
        var size = (qr.Size + (quietZone * 2)) * moduleSize;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            origin,
            origin + new Vector2(size, size),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));

        for (var y = 0; y < qr.Size; y++)
        {
            for (var x = 0; x < qr.Size; x++)
            {
                if (!qr.Modules[y, x])
                    continue;

                var topLeft = origin + new Vector2(
                    (x + quietZone) * moduleSize,
                    (y + quietZone) * moduleSize);
                drawList.AddRectFilled(
                    topLeft,
                    topLeft + new Vector2(moduleSize, moduleSize),
                    ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f)));
            }
        }

        ImGui.Dummy(new Vector2(size, size));
    }

    private void DrawPendingApprovals()
    {
        var pending = this.plugin.ServerHost.Approval.Pending;
        ImGui.TextUnformatted(this.l10n["pairing.pending"]);
        if (pending.Count == 0)
        {
            ImGui.TextDisabled(this.l10n["pairing.noPending"]);
            return;
        }

        foreach (var approval in pending)
        {
            ImGui.PushID((int)approval.Id);
            ImGui.TextWrapped($"{approval.Request.DeviceName} ({approval.Request.Platform})");
            ImGui.TextWrapped($"Fingerprint: {approval.Fingerprint}");
            if (ImGui.Button(this.l10n["common.accept"] + "##approval"))
                this.plugin.ServerHost.Approval.Resolve(approval.Id, true);
            ImGui.SameLine();
            if (ImGui.Button(this.l10n["common.deny"] + "##approval"))
                this.plugin.ServerHost.Approval.Resolve(approval.Id, false);
            ImGui.PopID();
        }
    }

    private void DrawDevices()
    {
        ImGui.TextUnformatted(this.l10n["devices.title"]);
        var server = this.plugin.ServerHost.Server;
        if (server is null)
        {
            ImGui.TextDisabled(this.l10n["pairing.unavailable"]);
            return;
        }

        var devices = server.Devices.List();
        if (devices.Count == 0)
        {
            ImGui.TextDisabled(this.l10n["devices.none"]);
            return;
        }

        if (!ImGui.BeginTable(
                "##freechat-devices",
                4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn(this.l10n["devices.name"]);
        ImGui.TableSetupColumn(this.l10n["devices.platform"]);
        ImGui.TableSetupColumn(this.l10n["devices.lastSeen"]);
        ImGui.TableSetupColumn(this.l10n["devices.actions"]);
        ImGui.TableHeadersRow();

        foreach (var device in devices)
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
            if (ImGui.Button(this.l10n["common.revoke"]))
                server.Devices.Revoke(device.DeviceId);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawSettings()
    {
        var config = this.plugin.Configuration;
        var host = this.plugin.ServerHost;

        ImGui.TextUnformatted(this.l10n["settings.title"]);

        var language = config.Language;
        if (ImGui.BeginCombo(this.l10n["settings.language"], language))
        {
            foreach (var code in Localization.Localizer.AvailableLanguages)
            {
                if (!ImGui.Selectable(code, string.Equals(code, language, StringComparison.OrdinalIgnoreCase)))
                    continue;

                config.Language = code;
                this.l10n.SetLanguage(code);
                config.Save();
            }

            ImGui.EndCombo();
        }

        var port = config.Port;
        if (ImGui.InputInt(this.l10n["settings.port"], ref port, 1, 100))
            config.Port = Math.Clamp(port, 1, 65535);

        var lan = config.AllowLan;
        if (ImGui.Checkbox(this.l10n["settings.allowLan"], ref lan))
            config.AllowLan = lan;

        var json = config.EnableJsonDebug;
        if (ImGui.Checkbox(this.l10n["settings.json"], ref json))
            config.EnableJsonDebug = json;

        var backlogMessages = config.BacklogMessages;
        if (ImGui.InputInt(this.l10n["settings.backlogMessages"], ref backlogMessages, 100, 1000))
            config.BacklogMessages = Math.Max(100, backlogMessages);

        var backlogMiB = config.BacklogMiB;
        if (ImGui.InputInt(this.l10n["settings.backlogMiB"], ref backlogMiB, 1, 8))
            config.BacklogMiB = Math.Max(1, backlogMiB);

        var maxClients = config.MaxClients;
        if (ImGui.InputInt(this.l10n["settings.maxClients"], ref maxClients, 1, 8))
            config.MaxClients = Math.Clamp(maxClients, 1, 128);

        var rate = config.SendRateLimitPerSecond;
        if (ImGui.InputInt(this.l10n["settings.rate"], ref rate, 1, 4))
            config.SendRateLimitPerSecond = Math.Clamp(rate, 1, 100);

        var burst = config.SendRateLimitBurst;
        if (ImGui.InputInt(this.l10n["settings.burst"], ref burst, 1, 4))
            config.SendRateLimitBurst = Math.Clamp(burst, 1, 200);

        var interval = config.SendIntervalMs;
        if (ImGui.InputInt(this.l10n["settings.sendInterval"], ref interval, 50, 100))
            config.SendIntervalMs = Math.Clamp(interval, 0, 5000);

        ImGui.TextUnformatted(this.l10n["settings.blocked"]);
        ImGui.InputTextMultiline("##freechat-blocked", ref this.blockedText, 4096, new Vector2(-1, 96));
        ImGui.TextDisabled(this.l10n["settings.blockedHint"]);

        if (ImGui.Button(this.l10n["settings.saveRestart"]))
        {
            this.SaveSettings(config, host);
            this.settingsMessage = this.l10n["settings.saved"];
        }

        if (!string.IsNullOrEmpty(this.settingsMessage))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(this.settingsMessage);
        }

        ImGui.TextDisabled(this.l10n["settings.restartHint"]);
    }

    private void SaveSettings(Configuration config, ServerHost host)
    {
        config.Port = Math.Clamp(config.Port, 1, 65535);
        config.BacklogMessages = Math.Max(100, config.BacklogMessages);
        config.BacklogMiB = Math.Max(1, config.BacklogMiB);
        config.MaxClients = Math.Clamp(config.MaxClients, 1, 128);
        config.SendRateLimitPerSecond = Math.Clamp(config.SendRateLimitPerSecond, 1, 100);
        config.SendRateLimitBurst = Math.Clamp(config.SendRateLimitBurst, 1, 200);
        config.SendIntervalMs = Math.Clamp(config.SendIntervalMs, 0, 5000);

        var commands = this.blockedText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        config.BlockedCommands = commands.Count > 0
            ? commands
            : new List<string>(CommandPolicy.DefaultBlocked);

        config.Save();
        host.Restart();
        this.plugin.UiLog.Add(UiLogLevel.Info, "Settings saved; server restarted.");
    }

    private void DrawLogs()
    {
        ImGui.TextUnformatted(this.l10n["logs.title"]);
        ImGui.Checkbox(this.l10n["logs.level.debug"], ref this.showDebug);
        ImGui.SameLine();
        ImGui.Checkbox(this.l10n["logs.level.info"], ref this.showInfo);
        ImGui.SameLine();
        ImGui.Checkbox(this.l10n["logs.level.warning"], ref this.showWarning);
        ImGui.SameLine();
        ImGui.Checkbox(this.l10n["logs.level.error"], ref this.showError);

        ImGui.InputText(this.l10n["logs.filter"], ref this.logFilter, 128);
        if (ImGui.Button(this.l10n["logs.copy"]))
            ImGui.SetClipboardText(this.BuildLogText());
        ImGui.SameLine();
        if (ImGui.Button(this.l10n["logs.clear"]))
            this.plugin.UiLog.Clear();

        if (!ImGui.BeginChild("##freechat-log", new Vector2(0, 0), true))
            return;

        var entries = this.plugin.UiLog.Snapshot();
        var shown = 0;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (!this.MatchesLogFilter(entry))
                continue;

            shown++;
            ImGui.TextUnformatted(
                $"{entry.At:HH:mm:ss} [{this.l10n[LogLevelKey(entry.Level)]}] {entry.Message}");
        }

        if (shown == 0)
            ImGui.TextDisabled(this.l10n["logs.empty"]);

        ImGui.EndChild();
    }

    private string BuildLogText()
    {
        var lines = new List<string>();
        foreach (var entry in this.plugin.UiLog.Snapshot())
        {
            if (this.MatchesLogFilter(entry))
                lines.Add($"{entry.At:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private bool MatchesLogFilter(UiLogEntry entry)
    {
        if (entry.Level switch
            {
                UiLogLevel.Debug => !this.showDebug,
                UiLogLevel.Info => !this.showInfo,
                UiLogLevel.Warning => !this.showWarning,
                UiLogLevel.Error => !this.showError,
                _ => false,
            })
            return false;

        return string.IsNullOrEmpty(this.logFilter)
               || entry.Message.Contains(this.logFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string LogLevelKey(UiLogLevel level) => level switch
    {
        UiLogLevel.Debug => "logs.level.debug",
        UiLogLevel.Info => "logs.level.info",
        UiLogLevel.Warning => "logs.level.warning",
        UiLogLevel.Error => "logs.level.error",
        _ => "logs.level.info",
    };
}
