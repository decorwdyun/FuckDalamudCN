using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using FuckDalamudCN.Windows;

namespace FuckDalamudCN;

internal sealed class DalamudInitializer : IDisposable
{
    private readonly ConfigWindow _configWindow;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly WindowSystem _windowSystem;

    public DalamudInitializer(
        IDalamudPluginInterface pluginInterface,
        WindowSystem windowSystem,
        ConfigWindow configWindow
    )
    {
        _pluginInterface = pluginInterface;
        _windowSystem = windowSystem;
        _configWindow = configWindow;
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