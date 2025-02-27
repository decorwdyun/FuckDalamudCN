using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Unban;

internal sealed class UnbanController : IDisposable
{
    private readonly IDalamudPluginInterface _dalamudPluginInterface;
    private readonly ILogger<UnbanController> _logger;
    private readonly IFramework _framework;
    
    private readonly object _pluginManager = null!;
    private uint _tickCount = 0;
    private DateTime _nextCheckTime = DateTime.MinValue;
    
    public UnbanController(
        IDalamudPluginInterface pluginInterface,
        ILogger<UnbanController> logger,
        IFramework framework)
    {
        _logger = logger;
        _dalamudPluginInterface = pluginInterface;
        _framework = framework;
        
        var dalamudAssembly = _dalamudPluginInterface.GetType().Assembly;
        var dalamudService = dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: true);
        ArgumentNullException.ThrowIfNull(dalamudService);
        _pluginManager = dalamudService.MakeGenericType(dalamudAssembly.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true))
            .GetMethod("Get")
            .Invoke(null, BindingFlags.Default, null, [], null);
        
    }

    public void Start()
    {
        _logger.LogInformation("服务现在开始运行。");
        _framework.Update += Tick;
    }

    private void Tick(IFramework framework)
    {
        if (_nextCheckTime < DateTime.Now)
        {
            _nextCheckTime = DateTime.Now.AddSeconds(3);
            _tickCount++;
            if (_tickCount > 20)
            {
                _framework.Update -= Tick;
                _logger.LogInformation("服务现在已停止（应该不需要了）。");
                return;
            }
            ClearManifestBanned();
            LoadInstalledPlugins();
        }
    }
    
    private void ClearManifestBanned()
    {
        try
        {
            var bannedPlugins = _pluginManager.GetType().GetField("bannedPlugins", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_pluginManager);
            var elementType = bannedPlugins?.GetType().GetElementType();
            var bannedLength = (int)(bannedPlugins?.GetType().GetProperty("Length")?.GetValue(bannedPlugins) ?? 0);
            if (elementType != null)
            {
                var newBannedPlugins = Array.CreateInstance(elementType, bannedLength);
                _pluginManager.GetType().GetField("bannedPlugins", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(_pluginManager, newBannedPlugins);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "解除 Dalamud CN 插件封锁时发生错误。");
        }
    }

    private async void LoadInstalledPlugins()
    {
        try
        {
            var installedPluginsField = _pluginManager.GetType().GetField(
                "installedPluginsList",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            );
            var installedPlugins =
                (installedPluginsField?.GetValue(_pluginManager) as IEnumerable ??
                 throw new InvalidOperationException()).Cast<object>().ToList();

            foreach (var installedPlugin in installedPlugins)
            {
                Type pluginType = installedPlugin.GetType();
                var pluginName = (string)pluginType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(installedPlugin)!;
                var state = pluginType.GetProperty("State", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(installedPlugin)?.ToString();
                var isBanned =
                    (bool)(pluginType
                        .GetProperty("IsBanned", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(installedPlugin) ?? false);
                var isWantedByAnyProfile =
                    (bool)(pluginType.GetProperty("IsWantedByAnyProfile", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(installedPlugin) ?? false);

                ;
                if (isBanned)
                {
                    _logger.LogInformation(
                        $"检测到被封锁的插件: {pluginName}({pluginType.Name}) 当前状态: {state} 是否需要加载: {isWantedByAnyProfile}");
                    switch (pluginType.Name)
                    {
                        case "LocalDevPlugin":
                            pluginType.BaseType
                                ?.GetField("<IsBanned>k__BackingField",
                                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                ?.SetValue(installedPlugin, false);
                            break;
                        case "LocalPlugin":
                            pluginType.GetField("<IsBanned>k__BackingField",
                                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                                ?.SetValue(installedPlugin, false);
                            break;
                    }

                    if (isWantedByAnyProfile)
                    {
                        pluginType.GetProperty("State", BindingFlags.Public | BindingFlags.Instance)
                            ?.SetValue(installedPlugin, 0);
                        await (Task)pluginType.GetMethod("LoadAsync")?.Invoke(installedPlugin, [3, false])!;
                        _logger.LogInformation($"已尝试加载插件: {pluginName}({pluginType.Name})");
                    }
                }
            }

        }
        catch (Exception e)
        {
            _logger.LogError(e, "解除 Dalamud CN 插件封锁时发生错误。");
        }
    }
    
    
    public void Dispose()
    {
        _framework.Update -= Tick;
    }
}