using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FuckDalamudCN.FastGithub;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal sealed class PluginRepositoryHijack : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger<HappyHttpClientHijack> _logger;
    private readonly IFramework _framework;
    private readonly GithubProxyPool _proxyPool;
    private readonly Configuration _configuration;
    private readonly HappyEyeballsCallback _happyEyeballsCallback;
    private readonly Assembly _dalamudAssembly;
    private readonly object? _pluginManager;

    private readonly HttpClient _newHttpClient;
    private DateTime _nextCheckTime;

    public PluginRepositoryHijack(
        IDalamudPluginInterface pluginInterface,
        ILogger<HappyHttpClientHijack> logger,
        IFramework framework,
        GithubProxyPool proxyPool,
        Configuration configuration,
        HappyEyeballsCallback happyEyeballsCallback
    )
    {
        _pluginInterface = pluginInterface;
        _logger = logger;
        _framework = framework;
        _proxyPool = proxyPool;
        _configuration = configuration;
        _happyEyeballsCallback = happyEyeballsCallback;
        _dalamudAssembly = pluginInterface.GetType().Assembly;

        var util = _dalamudAssembly.GetType("Dalamud.Utility.Util");
        var assemblyVersion = util?.GetProperty("AssemblyVersion", BindingFlags.Public | BindingFlags.Static);
        var dalamudAssemblyVersion = assemblyVersion?.GetValue(util) as string ??
                                     _dalamudAssembly.GetName().Version?.ToString() ?? "Unknown";

        _newHttpClient = new HttpClient(new HttpDelegatingHandler(_logger,
            dalamudAssemblyVersion,
            _configuration,
            _proxyPool,
            _happyEyeballsCallback,
            true))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var dalamudService = _dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: true);
        ArgumentNullException.ThrowIfNull(_dalamudAssembly);
        _pluginManager = dalamudService!
            .MakeGenericType(_dalamudAssembly!.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true))
            .GetMethod("Get")
            .Invoke(null, BindingFlags.Default, null, [], null);

        if (_pluginManager != null) Start();
    }

    private void Start()
    {
        _framework.Update += Tick;
    }

    private void Tick(IFramework framework)
    {
        if (_nextCheckTime < DateTime.Now)
        {
            _nextCheckTime = DateTime.Now.AddSeconds(10);
            TryHijackPluginRepository();
        }
    }

    public void TryHijackPluginRepository()
    {
        if (!_configuration.EnableFastGithub) return;
    
        if (_pluginManager is null)
        {
            _logger.LogError("插件管理器未找到，无法启用插件仓库加速。");
            return;
        }

        try
        {
            var thirdReposField = _pluginManager.GetType().GetProperty(
                "Repos",
                BindingFlags.Instance | BindingFlags.Public
            );

            var thirdRepos =
                (thirdReposField?.GetValue(_pluginManager) as IList ??
                 throw new InvalidOperationException()).Cast<object>().ToList();
            foreach (var thirdRepo in thirdRepos)
            {
                var pluginMasterUrlProperty = thirdRepo.GetType().GetProperty("PluginMasterUrl", BindingFlags.Instance | BindingFlags.Public);
                if (pluginMasterUrlProperty == null) continue;
                var pluginMasterUrl = pluginMasterUrlProperty.GetValue(thirdRepo) as string;
                
                var httpClientField = thirdRepo.GetType()
                    .GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
                if (httpClientField == null) continue;

                var currentHttpClient = httpClientField.GetValue(thirdRepo) as HttpClient;
                var replaced = ReferenceEquals(currentHttpClient, _newHttpClient);

                if (!replaced)
                {
                    var patterns = new[]
                    {
                        "https://raw.githubusercontent.com",
                        "https://github.com",
                        "https://gist.github.com",
                    };
                    if (patterns.Any(p => pluginMasterUrl!.StartsWith(p)))
                    {
                        httpClientField.SetValue(thirdRepo, _newHttpClient);
                    };
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "启用插件仓库加速失败。");
            return;
        }
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
    }
}