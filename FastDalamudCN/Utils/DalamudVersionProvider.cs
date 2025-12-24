using System.Reflection;
using Dalamud.Plugin;

namespace FastDalamudCN.Utils;

public class DalamudVersionProvider
{
    public DalamudVersionProvider(IDalamudPluginInterface pluginInterface)
    {
        var dalamudAssembly = pluginInterface.GetType().Assembly;
        var util = dalamudAssembly.GetType("Dalamud.Utility.Util");
        var assemblyVersion = util?.GetProperty("AssemblyVersion", BindingFlags.Public | BindingFlags.Static);
        DalamudAssemblyVersion = assemblyVersion?.GetValue(util) as string ??
                                 dalamudAssembly.GetName().Version?.ToString() ?? "Unknown";
    }

    public string DalamudAssemblyVersion { get; private set; }
}