using Dalamud.Extensions.MicrosoftLogging;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FuckDalamudCN.Unban;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN;

// ReSharper disable once InconsistentNaming
public sealed class FuckDalamudCN: IDalamudPlugin
{
    private readonly ServiceProvider? _serviceProvider;
    
    public FuckDalamudCN(IDalamudPluginInterface pluginInterface,
        IPluginLog pluginLog,
        IFramework framework
    )
    {
        try
        {
            ServiceCollection serviceCollection = new();
            serviceCollection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace)
                .ClearProviders()
                .AddDalamudLogger(pluginLog, t => t[(t.LastIndexOf('.') + 1)..]));
            serviceCollection.AddSingleton<IDalamudPlugin>(this);
            serviceCollection.AddSingleton(pluginInterface);
            serviceCollection.AddSingleton(framework);
            
            serviceCollection.AddSingleton<UnbanController>();
            _serviceProvider = serviceCollection.BuildServiceProvider();
            
            _serviceProvider.GetRequiredService<UnbanController>().Start();
        }
        catch (Exception e)
        {
            pluginLog.Error(e, "Failed to initialize plugin.");
            throw;
        }
    }


    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}