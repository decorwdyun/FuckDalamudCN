using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FastDalamudCN.Network;
using FastDalamudCN.Utils;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Controllers;

internal sealed class PluginRepositoryHijack : IDisposable
{
    private readonly IFramework _framework;
    private readonly ILogger<HappyHttpClientHijack> _logger;

    private readonly HttpClient _newHttpClient;
    private readonly object? _pluginManager;
    private DateTime _nextCheckTime;

    private readonly Dictionary<object, HttpClient> _originalClients = new();

    public PluginRepositoryHijack(
        IDalamudPluginInterface pluginInterface,
        ILogger<HappyHttpClientHijack> logger,
        IFramework framework,
        Configuration configuration,
        HttpDelegatingHandler httpDelegatingHandler
    )
    {
        _logger = logger;
        _framework = framework;
        var dalamudAssembly = pluginInterface.GetType().Assembly;

        _newHttpClient = new HttpClient(httpDelegatingHandler)
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
        RestoreOriginalHttpClients();
    }

    private void Start()
    {
        _framework.Update += Tick;
    }

    private void Tick(IFramework framework)
    {
        if (_nextCheckTime < DateTime.Now)
        {
            _nextCheckTime = DateTime.Now.AddSeconds(1);
            TryHijackPluginRepository();
        }
    }

    private void TryHijackPluginRepository()
    {
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
                
                if (ReferenceEquals(currentHttpClient, _newHttpClient)) continue;

                if (currentHttpClient != null)
                {
                    _originalClients.TryAdd(thirdRepo, currentHttpClient);
                }

                httpClientField.SetValue(thirdRepo, _newHttpClient);
                _logger.LogTrace($"已接管 {pluginMasterUrl}");
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "启用插件仓库加速失败。");
        }
    }

    private void RestoreOriginalHttpClients()
    {
        if (_originalClients.Count == 0) return;

        try
        {
            foreach (var (repo, originalClient) in _originalClients)
            {
                try
                {
                    var httpClientField = repo.GetType()
                        .GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
                    
                    if (httpClientField != null)
                    {
                        httpClientField.SetValue(repo, originalClient);
                    }
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, "还原单个仓库 HttpClient 失败");
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "还原插件仓库 HttpClient 过程中发生错误");
        }
        finally
        {
            _originalClients.Clear();
            _logger.LogTrace("已还原原始 HttpClient");
        }
    }
}