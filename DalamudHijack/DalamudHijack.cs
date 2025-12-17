using System.Reflection;
using Dalamud.Plugin;
using DalamudHijack.External;
using DalamudHijack.Patch;

namespace DalamudHijack;

public class DalamudHijack
{
    private static PatchManager? _patchManager;

    // ReSharper disable once UnusedMember.Global
    public static void Initialize(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<DalamudService>();
        FuckDalamudCNIPC.Initialize();
        DalamudService.PluginLog.Info("Initialized.");
        // DalamudService.PluginLog.Info($"Hello From DalamudHijack v{Assembly.GetExecutingAssembly().GetName().Version} by {pluginInterface.InternalName}({FuckDalamudCNIPC.Version()})!");
        if (_patchManager != null)
        {
            DalamudService.PluginLog.Info("Already initialized...");
            return;
        }

        _patchManager = new PatchManager("com.decorwdyun.DalamudHijack");
        _patchManager.RegisterPatch(new TestPatch());
        _patchManager.RegisterPatch(new EventTrackingPatch());
        _patchManager.RegisterPatch(new DeviceUtilsPatch());
        _patchManager.RegisterPatch(new PluginManagerPatch());
        _patchManager.ApplyAll();
    }

    public static void Unpatch()
    {
        if (_patchManager == null)
        {
            DalamudService.PluginLog.Info("Not initialized, nothing to unpatch.");
            return;
        }

        _patchManager.UnpatchAll();
        _patchManager = null;
        DalamudService.PluginLog.Info("Unpatched all.");
    }
}