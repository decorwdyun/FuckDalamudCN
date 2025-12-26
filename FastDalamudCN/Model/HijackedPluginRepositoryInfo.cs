namespace FastDalamudCN.Model;

public sealed class HijackedPluginRepositoryInfo(string pluginMasterUrl, bool isThirdParty)
{
    public string PluginMasterUrl { get; } = pluginMasterUrl;

    public bool IsThirdParty { get; } = isThirdParty;
}
