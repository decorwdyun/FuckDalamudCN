using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal sealed class DoNotTrackController: IDisposable
{
    private readonly IDalamudPluginInterface _dalamudPluginInterface;
    private readonly ILogger<DoNotTrackController> _logger;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;

    private readonly object? _chatHandlers;
    
    public DoNotTrackController(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        ILogger<DoNotTrackController> logger,
        IFramework framework)
    {
        _logger = logger;
        _dalamudPluginInterface = pluginInterface;
        _framework = framework;
        _clientState = clientState;

        var dalamudAssembly = _dalamudPluginInterface.GetType().Assembly;
        var dalamudService = dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: true);
        ArgumentNullException.ThrowIfNull(dalamudService);
        _chatHandlers = dalamudService.MakeGenericType(dalamudAssembly.GetType("Dalamud.Game.ChatHandlers", throwOnError: true))
            .GetMethod("Get")
            .Invoke(null, BindingFlags.Default, null, [], null);
        
        _clientState.Logout += OnLogout;
    }

    private void OnLogout(int type, int code)
    {
        _framework.RunOnTick(StopSendMeasurement, TimeSpan.FromSeconds(5));
    }

    public void Enable()
    {
        _framework.RunOnTick(StopSendMeasurement);
    }

    private void StopSendMeasurement()
    {
        _chatHandlers?.GetType().GetField("hasSendMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(_chatHandlers, true);
        _logger.LogInformation("已禁止 Dalamud CN 收集隐私数据。");
    }

    public void Dispose()
    {
        _clientState.Logout -= OnLogout;
    }
}