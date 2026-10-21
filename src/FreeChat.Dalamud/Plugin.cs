using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FreeChat.Dalamud.Chat;
using FreeChat.Dalamud.Localization;
using FreeChat.Dalamud.Logging;
using FreeChat.Dalamud.Send;
using FreeChat.Dalamud.Social;
using FreeChat.Dalamud.UI;

namespace FreeChat.Dalamud;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/freechat";

    [PluginService] internal static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProviderService { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("FreeChat");
    private readonly MainWindow mainWindow;
    private bool disposed;

    public Plugin()
    {
        this.Configuration = Interface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialise(this);
        this.UiLog = new UiLog();
        this.Localizer = new Localizer(this.Configuration.Language);

        this.ChatSendService = new ChatSendService(
            this.Configuration,
            this.UiLog,
            Framework,
            Log);
        this.Social = new GameSocialData(
            this.Configuration,
            ClientState,
            PlayerState,
            DataManager,
            Framework,
            GameInteropProvider,
            Log);
        this.ServerHost = new ServerHost(
            this.Configuration,
            Interface,
            Log,
            this.UiLog,
            this.ChatSendService,
            this.Social);
        this.ServerHost.Start();

        var factory = new ChatEventFactory(
            new ChatEnricher(DataManager),
            this.Social.NameFor);
        this.ChatCapture = new ChatCapture(
            ChatGui,
            this.ServerHost,
            factory,
            this.ChatSendService,
            () => this.LocalPlayerName,
            Log);

        this.mainWindow = new MainWindow(this);
        this.windowSystem.AddWindow(this.mainWindow);
        Interface.UiBuilder.Draw += this.windowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi += this.OpenMainWindow;
        Interface.UiBuilder.OpenMainUi += this.OpenMainWindow;

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open the FreeChat server window.",
        });

        ClientState.Login += this.OnLogin;
        ClientState.Logout += this.OnLogout;
        Framework.Update += this.OnFrameworkUpdate;

        if (ClientState.IsLoggedIn && PlayerState.IsLoaded)
            this.ServerHost.UpdateCharacter(ObjectTable.LocalPlayer);

        Log.Information("FreeChat loaded.");
        this.UiLog.Add(UiLogLevel.Info, "FreeChat loaded.");
    }

    public Configuration Configuration { get; }
    public Localizer Localizer { get; }
    public UiLog UiLog { get; }
    public ITextureProvider TextureProvider => TextureProviderService;
    public ServerHost ServerHost { get; }
    public GameSocialData Social { get; }
    public ChatSendService ChatSendService { get; }
    public ChatCapture ChatCapture { get; }
    public string? LocalPlayerName => ObjectTable.LocalPlayer?.Name.TextValue;

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        ClientState.Login -= this.OnLogin;
        ClientState.Logout -= this.OnLogout;
        Framework.Update -= this.OnFrameworkUpdate;
        CommandManager.RemoveHandler(CommandName);
        Interface.UiBuilder.Draw -= this.windowSystem.Draw;
        Interface.UiBuilder.OpenConfigUi -= this.OpenMainWindow;
        Interface.UiBuilder.OpenMainUi -= this.OpenMainWindow;

        this.ChatCapture.Dispose();
        this.ChatSendService.Dispose();
        this.ServerHost.Dispose();
        this.Social.Dispose();
        this.windowSystem.RemoveAllWindows();
        this.mainWindow.Dispose();
    }

    private void OnCommand(string command, string args) => this.OpenMainWindow();

    private void OpenMainWindow() => this.mainWindow.IsOpen = true;

    private void OnLogin() => this.ServerHost.UpdateCharacter(ObjectTable.LocalPlayer);

    private void OnLogout(int type, int code)
    {
        this.Social.Clear();
        _ = this.ServerHost.ResetSessionAsync("logout");
        this.ServerHost.UpdateCharacter(null);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.Social.Tick();
        if (ClientState.IsLoggedIn && PlayerState.IsLoaded)
            this.ServerHost.UpdateCharacter(ObjectTable.LocalPlayer);
    }
}
