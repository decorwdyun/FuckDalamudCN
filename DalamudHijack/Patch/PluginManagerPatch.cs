using System.Reflection;
using DalamudHijack.External;
using HarmonyLib;

namespace DalamudHijack.Patch;

public class PluginManagerPatch() : IPatch
{
    private MethodInfo? _patchedMethod;

    public void Apply(Harmony harmony)
    {
        var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Dalamud");
        if (dalamudAssembly == null) return;

        var pluginRepositoryType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.Types.PluginRepository");
        if (pluginRepositoryType == null) return;

        _patchedMethod =
            pluginRepositoryType.GetMethod("ReloadPluginMasterAsync", BindingFlags.Public | BindingFlags.Instance);
        var prefix = typeof(PluginManagerPatch).GetMethod(nameof(ReloadPluginMasterAsyncPrefix),
            BindingFlags.Public | BindingFlags.Static);
        if (_patchedMethod == null || prefix == null)
        {
            DalamudService.PluginLog.Warning("ReloadPluginMasterAsync Hook Failed.");
            return;
        }

        harmony.Patch(_patchedMethod, new HarmonyMethod(prefix));
    }

    public static bool ReloadPluginMasterAsyncPrefix()
    {
        FuckDalamudCNIPC.HijackPluginRepository();
        return true;
    }

    public void Unpatch(Harmony harmony)
    {
        if (_patchedMethod != null)
        {
            harmony.Unpatch(_patchedMethod, HarmonyPatchType.Prefix, harmony.Id);
        }
    }
}