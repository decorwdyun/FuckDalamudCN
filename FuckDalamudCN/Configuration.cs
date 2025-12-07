using Dalamud.Configuration;

namespace FuckDalamudCN;

internal sealed class Configuration : IPluginConfiguration
{
    public bool EnableFastGithub { get; set; } = false;
    public int Version { get; set; } = 1;
}