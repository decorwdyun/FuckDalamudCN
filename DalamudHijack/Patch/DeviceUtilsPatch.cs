using System.Reflection;
using HarmonyLib;

namespace DalamudHijack.Patch;

public class DeviceUtilsPatch : IPatch
{
    private MethodInfo? _patchedMethod;

    public void Apply(Harmony harmony)
    {
        var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Dalamud");
        if (dalamudAssembly == null) return;

        var utilsType = dalamudAssembly.GetType("Dalamud.Utility.DeviceUtils");
        if (utilsType == null) return;

        _patchedMethod = utilsType.GetMethod("GetDeviceId", BindingFlags.Public | BindingFlags.Static);
        if (_patchedMethod == null) return;

        var prefix =
            typeof(DeviceUtilsPatch).GetMethod(nameof(GetDeviceIdPostfix),
                BindingFlags.Static | BindingFlags.Public);
        if (_patchedMethod != null && prefix != null)
        {
            harmony.Patch(_patchedMethod, postfix:new HarmonyMethod(prefix));
        }
    }

    public void Unpatch(Harmony harmony)
    {
        if (_patchedMethod != null)
        {
            harmony.Unpatch(_patchedMethod, HarmonyPatchType.Prefix, harmony.Id);
        }
    }
    
    public static void GetDeviceIdPostfix(ref string __result)
    {
        string RandomHex(int len)
        {
            var rng = new Random();
            var bytes = new byte[len / 2];
            rng.NextBytes(bytes);
            return Convert.ToHexString(bytes).ToUpper();
        }
        // DalamudHijack.LogMessage($"DeviceUtilsPatch.GetDeviceIdPostfix called, old: {__result}, new: {newId}");
        __result = $"{RandomHex(32)}:{RandomHex(32)}:{RandomHex(32)}";;
    }
}