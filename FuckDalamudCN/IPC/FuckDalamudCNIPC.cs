using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FuckDalamudCN.Controllers;
using FuckDalamudCN.Windows;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.IPC;

internal class FuckDalamudCNIPC : IDisposable
{
    private readonly ILogger<FuckDalamudCNIPC> _logger;
    private readonly ICallGateProvider<Version> _version;
    private readonly ICallGateProvider<bool> _hijackPluginRepository;

    public FuckDalamudCNIPC(
        IDalamudPluginInterface pluginInterface,
        ILogger<FuckDalamudCNIPC> logger,
        PluginRepositoryHijack repositoryHijack
    )
    {
        _logger = logger;

        _version = pluginInterface.GetIpcProvider<Version>("FuckDalamudCN.Version");
        _version.RegisterFunc(() => typeof(FuckDalamudCN).Assembly.GetName().Version!);

        _hijackPluginRepository = pluginInterface.GetIpcProvider<bool>("FuckDalamudCN.HijackPluginRepository");
        _hijackPluginRepository.RegisterAction(repositoryHijack.TryHijackPluginRepository);

    }

    public void Dispose()
    {
        _version.UnregisterFunc();
        _hijackPluginRepository.UnregisterAction();
    }
}