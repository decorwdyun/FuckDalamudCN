using System.Reflection;
using HarmonyLib;

namespace DalamudHijack.Patch;

public class EventTrackingPatch : IPatch
{
    private MethodInfo? _patchedMethod;

    public void Apply(Harmony harmony)
    {
        var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Dalamud");
        if (dalamudAssembly == null) return;

        var eventTrackingType = dalamudAssembly.GetType("Dalamud.Support.EventTracking");
        if (eventTrackingType == null) return;

        _patchedMethod = eventTrackingType.GetMethod("SendMeasurement", BindingFlags.Public | BindingFlags.Static);
        var prefix = typeof(EventTrackingPatch).GetMethod(nameof(SendMeasurementPrefix), BindingFlags.Static | BindingFlags.Public);
        if (_patchedMethod != null && prefix != null)
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

    public static bool SendMeasurementPrefix(ulong contentId, uint actorId, uint homeWorldId)
    {
        DalamudService.PluginLog.Info($"SendMeasurement called with ContentId: {contentId}, ActorId: {actorId}, HomeWorldId: {homeWorldId}");
        return false;
    }
}