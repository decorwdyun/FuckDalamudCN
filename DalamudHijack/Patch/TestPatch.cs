using System.Reflection;
using HarmonyLib;

namespace DalamudHijack.Patch;

public class TestPatch : IPatch
{

    private MethodInfo? _patchedMethod;

    public void Apply(Harmony harmony)
    {
        var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Dalamud");
        if (dalamudAssembly == null) return;
        
        var interfaceType = dalamudAssembly.GetType("Dalamud.Interface.Internal.DalamudInterface");
        if (interfaceType == null) return;
        
        _patchedMethod = interfaceType.GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.Public);
        if (_patchedMethod == null) return;
        
        var prefix = typeof(TestPatch).GetMethod(nameof(OpenSettingsPrefix), BindingFlags.Static | BindingFlags.Public);
        if (prefix != null)
        {
            harmony.Patch(_patchedMethod, new HarmonyMethod(prefix));
        }
    }

    public void Unpatch(Harmony harmony)
    {
        if (_patchedMethod != null)
        {
            harmony.Unpatch(_patchedMethod, HarmonyPatchType.Prefix, harmony.Id);
        }
    }

    public static bool OpenSettingsPrefix()
    {
        DalamudService.PluginLog.Info(
            "TestPatch OpenSettingsPrefix called. This is a test patch to check if the patching system works correctly.");
        return true;
    }
}