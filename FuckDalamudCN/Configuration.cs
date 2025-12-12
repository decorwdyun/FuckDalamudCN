using Dalamud.Configuration;

namespace FuckDalamudCN;

public sealed class Configuration : IPluginConfiguration
{
    public bool EnableFastGithub { get; set; } = false;
    public bool EnableMainRepoPluginLocalization { get; set; } = true;
    public bool EnablePluginManifestCache { get; set; } = true;
    public int Version { get; set; } = 1;
}