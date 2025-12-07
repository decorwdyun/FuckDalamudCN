using System.Reflection;
using Dalamud.Plugin;
using FuckDalamudCN.FastGithub;
using FuckDalamudCN.Utils;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal sealed class HappyHttpClientHijack
{
    private readonly Configuration _configuration;
    private readonly DalamudVersionProvider _dalamudVersionProvider;
    private readonly HappyEyeballsCallback _happyEyeballsCallback;

    private readonly object? _happyHttpClient;
    private readonly ILogger<HappyHttpClientHijack> _logger;
    private readonly GithubProxyPool _proxyPool;
    private readonly DalamudBranchDetector _dalamudBranchDetector;


    public HappyHttpClientHijack(
        IDalamudPluginInterface pluginInterface,
        ILogger<HappyHttpClientHijack> logger,
        DalamudVersionProvider dalamudVersionProvider,
        GithubProxyPool proxyPool,
        DalamudBranchDetector dalamudBranchDetector,
        Configuration configuration,
        HappyEyeballsCallback happyEyeballsCallback
    )
    {
        _logger = logger;
        _dalamudVersionProvider = dalamudVersionProvider;
        _proxyPool = proxyPool;
        _dalamudBranchDetector = dalamudBranchDetector;
        _configuration = configuration;
        _happyEyeballsCallback = happyEyeballsCallback;
        var dalamudAssembly = pluginInterface.GetType().Assembly;
        var dalamudService = dalamudAssembly.GetType("Dalamud.Service`1", true);
        ArgumentNullException.ThrowIfNull(dalamudAssembly);

        _happyHttpClient = dalamudService?
            .MakeGenericType(dalamudAssembly.GetType("Dalamud.Networking.Http.HappyHttpClient", true))
            .GetMethod("Get")
            ?.Invoke(null, BindingFlags.Default, null, [], null);

        if (_happyHttpClient != null) Enable();
    }
    

    private void Enable()
    {
        if (_happyHttpClient == null) return;
        var httpClient = _happyHttpClient.GetType()
            .GetField("<SharedHttpClient>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (httpClient is null)
        {
            _logger.LogError("Failed to get SharedHttpClient");
            return;
        }

        ;

        var newHttpClient = new HttpClient(new HttpDelegatingHandler(
                _logger,
                _dalamudVersionProvider,
                _configuration,
                _proxyPool,
                _happyEyeballsCallback,
                false
            )
        );

        httpClient.SetValue(_happyHttpClient, newHttpClient);
        _logger.LogInformation(
            $"已屏蔽数据上报, Dalamud/{_dalamudVersionProvider.DalamudAssemblyVersion}, 随机机器码: {MachineCodeGenerator.Instance.MachineCode}");
    }
}