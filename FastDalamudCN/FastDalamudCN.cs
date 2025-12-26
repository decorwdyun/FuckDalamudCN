using Dalamud.Extensions.MicrosoftLogging;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FastDalamudCN.Controllers;
using FastDalamudCN.Network;
using FastDalamudCN.Network.Abstractions;
using FastDalamudCN.Network.Execution;
using FastDalamudCN.Network.Proxy;
using FastDalamudCN.Utils;
using FastDalamudCN.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN;

// ReSharper disable once InconsistentNaming
public sealed class FastDalamudCN : IDalamudPlugin
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ServiceProvider? _serviceProvider;

    public FastDalamudCN(IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IPluginLog pluginLog,
        IFramework framework,
        ICommandManager commandManager
    )
    {
        _pluginInterface = pluginInterface;

        if (!_pluginInterface.Manifest.Author.Equals("decorwdyun", StringComparison.InvariantCultureIgnoreCase))
        {
            throw new InvalidOperationException($"Plugin author tampered!");
        }

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
            serviceCollection.AddSingleton(commandManager);
            serviceCollection.AddSingleton((Configuration?)pluginInterface.GetPluginConfig() ?? new Configuration());
            serviceCollection.AddSingleton(new WindowSystem(nameof(FastDalamudCN)));
            serviceCollection.AddSingleton<DalamudInitializer>();
            serviceCollection.AddSingleton<DalamudVersionProvider>();
            serviceCollection.AddSingleton<CommandHandler>();

            serviceCollection.AddSingleton<PluginLocalizationService>();
            serviceCollection.AddSingleton<DnsResolver>();
            serviceCollection.AddSingleton<HappyEyeballsCallback>();
            
            serviceCollection.AddSingleton<GithubProxyProvider>();
            serviceCollection.AddSingleton<IProxySelector, GithubProxySelector>();
            serviceCollection.AddSingleton<IHttpCacheService, HttpCacheService>();
            serviceCollection.AddSingleton<HijackedPluginRepositoryStore>();
            
            serviceCollection.AddSingleton<RaceConditionAwareRequestExecutor>();
            serviceCollection.AddSingleton<IRequestExecutor>(sp => sp.GetRequiredService<RaceConditionAwareRequestExecutor>());
            
            serviceCollection.AddSingleton<HttpDelegatingHandler>();
            
            serviceCollection.AddSingleton<PluginRepositoryHijack>();
            serviceCollection.AddSingleton<HappyHttpClientHijack>();

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
        _serviceProvider?.Dispose();
    }
}
