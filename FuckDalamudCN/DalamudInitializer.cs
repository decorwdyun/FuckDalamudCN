using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using FuckDalamudCN.Windows;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN;

internal sealed class DalamudInitializer : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly WindowSystem _windowSystem;
    private readonly ConfigWindow _configWindow;
    private readonly ILogger<DalamudInitializer> _logger;
    
    public DalamudInitializer(
        IDalamudPluginInterface pluginInterface,
        WindowSystem windowSystem,
        ConfigWindow configWindow,
        ILogger<DalamudInitializer> logger
        )
    {
        _pluginInterface = pluginInterface;
        _windowSystem = windowSystem;
        _configWindow = configWindow;
        _logger = logger;
        _windowSystem.AddWindow(configWindow);
        
        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += _configWindow.Toggle;
        _pluginInterface.UiBuilder.OpenConfigUi += _configWindow.Toggle;
    }

    public void Dispose()
    {
        _pluginInterface.UiBuilder.OpenConfigUi -= _configWindow.Toggle;
        _pluginInterface.UiBuilder.OpenMainUi -= _configWindow.Toggle;
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _windowSystem.RemoveAllWindows();
    }
}