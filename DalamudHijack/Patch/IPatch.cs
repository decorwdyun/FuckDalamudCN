namespace DalamudHijack.Patch;

public interface IPatch
{
    void Apply(HarmonyLib.Harmony harmony);
    void Unpatch(HarmonyLib.Harmony harmony);
}