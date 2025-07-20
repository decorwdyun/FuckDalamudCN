using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace DalamudHijack.External;

public class FuckDalamudCNIPC
{
    private static ICallGateSubscriber<Version>? _version;
    private static ICallGateSubscriber<string, bool> _showIncompatiblePluginWarningWindow;

    public static void Initialize()
    {
        _version = DalamudService.PluginInterface.GetIpcSubscriber<Version>("FuckDalamudCN.Version");
        _showIncompatiblePluginWarningWindow = DalamudService.PluginInterface.GetIpcSubscriber<string,bool>("FuckDalamudCN.ShowIncompatiblePluginWarningWindow");
    }

    public static Version Version()
    {
        return _version!.InvokeFunc();
    }
    
    public static bool ShowIncompatiblePluginWarningWindow(string pluginName)
    {
        return _showIncompatiblePluginWarningWindow.InvokeFunc(pluginName);
    }
}