using Dalamud.Extensions.MicrosoftLogging;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FuckDalamudCN.Controllers;
using FuckDalamudCN.Network;
using FuckDalamudCN.Utils;
using FuckDalamudCN.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN;

// ReSharper disable once InconsistentNaming
public sealed class FuckDalamudCN : IDalamudPlugin
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ServiceProvider? _serviceProvider;

    public FuckDalamudCN(IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IPluginLog pluginLog,
        IFramework framework,
        INotificationManager notificationManager,
        IChatGui chatGui,
        ICommandManager commandManager
    )
    {
        _pluginInterface = pluginInterface;
        if ((uint)clientState.ClientLanguage != 4)
        {
            throw new InvalidOperationException("This plugin is not compatible with your client.");
        }
#if !DEBUG
        bool RepoCheck()
        {
            var sourceRepository = _pluginInterface.SourceRepository;
            return sourceRepository == "https://gp.xuolu.com/love.json" || sourceRepository.Contains("decorwdyun/DalamudPlugins", StringComparison.OrdinalIgnoreCase);
        }
        if ((_pluginInterface.IsDev || !RepoCheck()))
        {
            notificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification()
            {
                Type = Dalamud.Interface.ImGuiNotification.NotificationType.Error,
                Title = "加载验证",
                Content = "由于本地加载或安装来源仓库非 decorwdyun 个人仓库，插件禁止加载。",
            });
            return;
        }
#endif
        try
        {
            ServiceCollection serviceCollection = new();
            serviceCollection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace)
                .ClearProviders()
                .AddDalamudLogger(pluginLog, t => t[(t.LastIndexOf('.') + 1)..]));
            serviceCollection.AddSingleton<IDalamudPlugin>(this);
            serviceCollection.AddSingleton(pluginInterface);
            serviceCollection.AddSingleton(framework);
            serviceCollection.AddSingleton(clientState);
            serviceCollection.AddSingleton(chatGui);
            serviceCollection.AddSingleton(commandManager);
            serviceCollection.AddSingleton((Configuration?)pluginInterface.GetPluginConfig() ?? new Configuration());
            serviceCollection.AddSingleton(new WindowSystem(nameof(FuckDalamudCN)));
            serviceCollection.AddSingleton<DalamudInitializer>();
            serviceCollection.AddSingleton<DalamudVersionProvider>();
            serviceCollection.AddSingleton<CommandHandler>();

            serviceCollection.AddSingleton<PluginLocalizationService>();
            serviceCollection.AddSingleton<HappyEyeballsCallback>();

            serviceCollection.AddSingleton<GithubProxyPool>();
            serviceCollection.AddSingleton<IHttpCacheService, HttpCacheService>();
            serviceCollection.AddSingleton<HttpDelegatingHandler>();
            serviceCollection.AddSingleton<HappyHttpClientHijack>();
            serviceCollection.AddSingleton<PluginRepositoryHijack>();
            
            serviceCollection.AddSingleton<ConfigWindow>();

            _serviceProvider = serviceCollection.BuildServiceProvider();
            _serviceProvider.GetRequiredService<CommandHandler>();
            try
            {
                _serviceProvider.GetRequiredService<DalamudInitializer>();
                _serviceProvider.GetRequiredService<HappyHttpClientHijack>();
                _serviceProvider.GetRequiredService<PluginRepositoryHijack>();
            }
            catch (Exception e)
            {
                pluginLog.Error(e.ToString());
            }
        }
        catch (Exception e)
        {
            pluginLog.Error(e, "Failed to initialize plugin..");
            throw;
        }
    }

    public void Dispose()
    {
#if !DEBUG
        bool RepoCheck()
        {
            var sourceRepository = _pluginInterface.SourceRepository;
            return sourceRepository == "https://gp.xuolu.com/love.json" || sourceRepository.Contains("decorwdyun/DalamudPlugins", StringComparison.OrdinalIgnoreCase);
        }
        if (_pluginInterface.IsDev || !RepoCheck())
        {
            return;
        }
#endif
        _serviceProvider?.Dispose();
    }
}