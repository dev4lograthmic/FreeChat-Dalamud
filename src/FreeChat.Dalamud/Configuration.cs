using Dalamud.Configuration;

namespace FreeChat.Dalamud;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    private Plugin? plugin;

    public int Version { get; set; } = 1;
    public int Port { get; set; } = 14777;
    public bool AllowLan { get; set; }
    public bool EnableJsonDebug { get; set; }
    public string ServerName { get; set; } = "FreeChat";
    public string ServerId { get; set; } = Guid.NewGuid().ToString("N");
    public int BacklogMessages { get; set; } = 5000;
    public int BacklogMiB { get; set; } = 32;

    internal void Initialise(Plugin owner) => this.plugin = owner;

    public void Save() => Plugin.Interface.SavePluginConfig(this);
}
