using DalamudHijack.Patch;
using HarmonyLib;

namespace DalamudHijack;

public class PatchManager(string id)
{
    private readonly Harmony _harmony = new(id);
    private readonly List<IPatch> _patches = new();

    public void RegisterPatch(IPatch patch)
    {
        _patches.Add(patch);
    }

    public void ApplyAll()
    {
        foreach (var patch in _patches)
        {
            try
            {
                patch.Apply(_harmony);
                DalamudService.PluginLog.Info($"Applied patch: {patch}");
            }
            catch (Exception ex)
            {
                DalamudService.PluginLog.Info($"Failed to apply patch {patch}: {ex.Message} {ex.StackTrace}.");
            }
        }
    }

    public void UnpatchAll()
    {
        foreach (var patch in _patches)
        {
            try
            {
                patch.Unpatch(_harmony);
                DalamudService.PluginLog.Info($"Unpatched: {patch}");
            }
            catch (Exception ex)
            {
                DalamudService.PluginLog.Info($"Failed to unpatch {patch}: {ex.Message}");
            }
        }

        try
        {
            _harmony.UnpatchAll(id);
        }
        catch (Exception e)
        {
            DalamudService.PluginLog.Error($"Failed to unpatch all: {e.Message} {e.StackTrace}");
        }
    }
}