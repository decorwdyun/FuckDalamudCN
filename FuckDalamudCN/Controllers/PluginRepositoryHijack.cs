using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FuckDalamudCN.FastGithub;
using FuckDalamudCN.Utils;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal sealed class PluginRepositoryHijack : IDisposable
{
    private readonly Configuration _configuration;
    private readonly IFramework _framework;
    private readonly ILogger<HappyHttpClientHijack> _logger;

    private readonly HttpClient _newHttpClient;
    private readonly object? _pluginManager;
    private DateTime _nextCheckTime;

    public PluginRepositoryHijack(
        IDalamudPluginInterface pluginInterface,
        ILogger<HappyHttpClientHijack> logger,
        IFramework framework,
        GithubProxyPool proxyPool,
        Configuration configuration,
        DalamudVersionProvider dalamudVersionProvider,
        HappyEyeballsCallback happyEyeballsCallback
    )
    {
        _logger = logger;
        _framework = framework;
        _configuration = configuration;
        var dalamudAssembly = pluginInterface.GetType().Assembly;

        _newHttpClient = new HttpClient(new HttpDelegatingHandler(_logger,
            dalamudVersionProvider,
            _configuration,
            proxyPool,
            happyEyeballsCallback,
            true))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        var dalamudService = dalamudAssembly.GetType("Dalamud.Service`1", true);
        ArgumentNullException.ThrowIfNull(dalamudAssembly);
        _pluginManager = dalamudService!
            .MakeGenericType(dalamudAssembly!.GetType("Dalamud.Plugin.Internal.PluginManager", true))
            .GetMethod("Get")
            .Invoke(null, BindingFlags.Default, null, [], null);

        if (_pluginManager != null) Start();
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
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
                var pluginMasterUrlProperty = thirdRepo.GetType()
                    .GetProperty("PluginMasterUrl", BindingFlags.Instance | BindingFlags.Public);
                if (pluginMasterUrlProperty == null) continue;
                var pluginMasterUrl = pluginMasterUrlProperty.GetValue(thirdRepo) as string;

                var httpClientField = thirdRepo.GetType()
                    .GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
                if (httpClientField == null) continue;

                var currentHttpClient = httpClientField.GetValue(thirdRepo) as HttpClient;
                var replaced = ReferenceEquals(currentHttpClient, _newHttpClient);

                if (!replaced)
                {
                    httpClientField.SetValue(thirdRepo, _newHttpClient);
                    _logger.LogTrace($"已接管 {pluginMasterUrl}");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "启用插件仓库加速失败。");
        }
    }
}