using System.Net;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FuckDalamudCN.FastGithub;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal sealed class FastGithubController : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger<FastGithubController> _logger;
    private readonly IFramework _framework;
    private readonly GithubProxyPool _proxyPool;
    private readonly Configuration _configuration;
    
    private readonly object _happyHttpClient = null!;
    private readonly Assembly _dalamudAssembly;
    
    private string _dalamudAssemblyVersion;
    
    public FastGithubController(
        IDalamudPluginInterface pluginInterface,
        ILogger<FastGithubController> logger,
        IFramework framework,
        GithubProxyPool proxyPool,
        Configuration configuration
        )
    {
        _pluginInterface = pluginInterface;
        _logger = logger;
        _framework = framework;
        _proxyPool = proxyPool;
        _configuration = configuration;
        _dalamudAssembly = pluginInterface.GetType().Assembly;
        var dalamudService = _dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: true);
        ArgumentNullException.ThrowIfNull(_dalamudAssembly);
        
        _happyHttpClient = dalamudService?
            .MakeGenericType(_dalamudAssembly.GetType("Dalamud.Networking.Http.HappyHttpClient", throwOnError: true))
            .GetMethod("Get")
            ?.Invoke(null, BindingFlags.Default, null, [], null);

        var util = _dalamudAssembly.GetType("Dalamud.Utility.Util");
        var assemblyVersion = util?.GetProperty("AssemblyVersion", BindingFlags.Public | BindingFlags.Static);
        _dalamudAssemblyVersion = assemblyVersion?.GetValue(util) as string ?? _dalamudAssembly.GetName().Version?.ToString() ?? "Unknown";
    
        if (_configuration.EnableFastGithub && _happyHttpClient != null) Enable();
    }

    public void Enable()
    {
        var httpClient = _happyHttpClient.GetType()
            .GetField("<SharedHttpClient>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (httpClient is null)
        {
            _logger.LogError("Failed to get SharedHttpClient");
            return;
        };
      
        var newHttpClient = new HttpClient(new HttpDelegatingHandler(
            _logger, 
            _dalamudAssemblyVersion,
            _configuration,
            _proxyPool));

        httpClient.SetValue(_happyHttpClient, newHttpClient);
        _logger.LogInformation($"Github 加速已开启, Dalamud/{_dalamudAssemblyVersion}, 随机机器码: {MachineCodeGenerator.Instance.MachineCode}");
    }
    

    public void Dispose()
    {
    }
}