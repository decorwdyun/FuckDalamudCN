using System.Reflection;
using Dalamud.Plugin;
using FastDalamudCN.Network;
using FastDalamudCN.Utils;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Controllers;

internal sealed class HappyHttpClientHijack : IDisposable
{
    private readonly DalamudVersionProvider _dalamudVersionProvider;
    private readonly HttpDelegatingHandler _httpDelegatingHandler;

    private readonly object? _happyHttpClient;
    private readonly ILogger<HappyHttpClientHijack> _logger;

    private HttpClient? _originalHttpClient;
    private FieldInfo? _sharedHttpClientField;

    public HappyHttpClientHijack(
        IDalamudPluginInterface pluginInterface,
        ILogger<HappyHttpClientHijack> logger,
        DalamudVersionProvider dalamudVersionProvider,
        HttpDelegatingHandler httpDelegatingHandler
    )
    {
        _logger = logger;
        _dalamudVersionProvider = dalamudVersionProvider;
        _httpDelegatingHandler = httpDelegatingHandler;
        var dalamudAssembly = pluginInterface.GetType().Assembly;
        var dalamudService = dalamudAssembly.GetType("Dalamud.Service`1", true);
        ArgumentNullException.ThrowIfNull(dalamudAssembly);

        _happyHttpClient = dalamudService?
#pragma warning disable CS8604 // 引用类型参数可能为 null。
            .MakeGenericType(dalamudAssembly.GetType("Dalamud.Networking.Http.HappyHttpClient", true))
#pragma warning restore CS8604 // 引用类型参数可能为 null。
            .GetMethod("Get")
            ?.Invoke(null, BindingFlags.Default, null, [], null);

        if (_happyHttpClient != null) Enable();
    }

    public void Dispose()
    {
        if (_happyHttpClient != null && _sharedHttpClientField != null && _originalHttpClient != null)
        {
            try
            {
                _sharedHttpClientField.SetValue(_happyHttpClient, _originalHttpClient);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "还原 HappyHttpClient 失败");
            }
        }
    }

    private void Enable()
    {
        if (_happyHttpClient == null) return;
        
        _sharedHttpClientField = _happyHttpClient.GetType()
            .GetField("<SharedHttpClient>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            
        if (_sharedHttpClientField is null)
        {
            _logger.LogError("Failed to get SharedHttpClient field");
            return;
        }

        _originalHttpClient = _sharedHttpClientField.GetValue(_happyHttpClient) as HttpClient;

        var newHttpClient = new HttpClient(_httpDelegatingHandler);

        _sharedHttpClientField.SetValue(_happyHttpClient, newHttpClient);
    }
}