﻿using System.Net;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FuckDalamudCN.FastGithub;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal sealed class HappyHttpClientHijack : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ILogger<HappyHttpClientHijack> _logger;
    private readonly IFramework _framework;
    private readonly GithubProxyPool _proxyPool;
    private readonly Configuration _configuration;
    private readonly HappyEyeballsCallback _happyEyeballsCallback;

    private readonly object? _happyHttpClient;
    private readonly Assembly _dalamudAssembly;
    
    private string _dalamudAssemblyVersion;
    
    public HappyHttpClientHijack(
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
        var dalamudService = _dalamudAssembly.GetType("Dalamud.Service`1", throwOnError: true);
        ArgumentNullException.ThrowIfNull(_dalamudAssembly);
        
        _happyHttpClient = dalamudService?
            .MakeGenericType(_dalamudAssembly.GetType("Dalamud.Networking.Http.HappyHttpClient", throwOnError: true))
            .GetMethod("Get")
            ?.Invoke(null, BindingFlags.Default, null, [], null);

        var util = _dalamudAssembly.GetType("Dalamud.Utility.Util");
        var assemblyVersion = util?.GetProperty("AssemblyVersion", BindingFlags.Public | BindingFlags.Static);
        _dalamudAssemblyVersion = assemblyVersion?.GetValue(util) as string ?? _dalamudAssembly.GetName().Version?.ToString() ?? "Unknown";
        
        if (_happyHttpClient != null) Enable();
    }

    public void Enable()
    {
        if (_happyHttpClient == null) return;
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
            _proxyPool,
            _happyEyeballsCallback,
            false
            )
        );

        httpClient.SetValue(_happyHttpClient, newHttpClient);
        _logger.LogInformation($"已屏蔽数据上报, Dalamud/{_dalamudAssemblyVersion}, 随机机器码: {MachineCodeGenerator.Instance.MachineCode}");
    }
    

    public void Dispose()
    {
    }
}