using Dalamud.Configuration;

namespace FuckDalamudCN;

internal sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool EnableFastGithub { get; set; } = false;
}