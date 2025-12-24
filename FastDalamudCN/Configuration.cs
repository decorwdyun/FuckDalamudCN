using Dalamud.Configuration;

namespace FastDalamudCN;

public sealed class Configuration : IPluginConfiguration
{
    public bool EnableFastGithub { get; set; } = true;
    public bool EnableMainRepoPluginLocalization { get; set; } = true;
    public bool EnablePluginManifestCache { get; set; } = true;
    public int Version { get; set; } = 1;
}