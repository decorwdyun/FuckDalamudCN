using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace DalamudHijack.External;

public class FuckDalamudCNIPC
{
    private static ICallGateSubscriber<Version>? _version;
    private static ICallGateSubscriber<bool>? _hijackPluginRepository;

    public static void Initialize()
    {
        _version = DalamudService.PluginInterface.GetIpcSubscriber<Version>("FuckDalamudCN.Version");
        _hijackPluginRepository = DalamudService.PluginInterface.GetIpcSubscriber<bool>("FuckDalamudCN.HijackPluginRepository");
    }

    public static Version Version()
    {
        return _version!.InvokeFunc();
    }

    public static void HijackPluginRepository()
    {
        _hijackPluginRepository?.InvokeAction();
    }
}