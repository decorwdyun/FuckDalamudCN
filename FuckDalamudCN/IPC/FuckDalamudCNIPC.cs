using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FuckDalamudCN.Windows;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.IPC;

public class FuckDalamudCNIPC : IDisposable
{
    private readonly ILogger<FuckDalamudCNIPC> _logger;
    private readonly ICallGateProvider<Version> _version;

    public FuckDalamudCNIPC(
        IDalamudPluginInterface pluginInterface,
        ILogger<FuckDalamudCNIPC> logger
    )
    {
        _logger = logger;

        _version = pluginInterface.GetIpcProvider<Version>("FuckDalamudCN.Version");
        _version.RegisterFunc(() => typeof(FuckDalamudCN).Assembly.GetName().Version!);
    }

    public void Dispose()
    {
        _version.UnregisterFunc();
    }
}