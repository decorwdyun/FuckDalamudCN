using System.Net;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.FastGithub;

internal sealed class HttpDelegatingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly string _dalamudVersion;
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;

    private int _errorCount = 0;
    private const int MaxErrorCount = 10;

    private HappyEyeballsCallback SharedHappyEyeballsCallback { get; set; }
    public HttpDelegatingHandler(
        ILogger logger, 
        string dalamudVersion,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        DynamicHttpWindowsProxy.DynamicHttpWindowsProxy dynamicHttpWindowsProxy,
        HappyEyeballsCallback happyEyeballsCallback
    )
    {
        _logger = logger;
        _dalamudVersion = dalamudVersion;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        InnerHandler = new SocketsHttpHandler
        {
            Proxy = dynamicHttpWindowsProxy,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = happyEyeballsCallback.ConnectCallback,
        };
    }


    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        request.Headers.Add("User-Agent", $"Dalamud/{_dalamudVersion}");
        if (request.RequestUri?.Host == "aonyx.ffxiv.wang")
        {
            request.Headers.Add("X-Machine-Token", MachineCodeGenerator.Instance.MachineCode);
        }

        ReplaceRequestUri(request);
        
        var response = await base.SendAsync(request, cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _errorCount++;

            if (_errorCount >= MaxErrorCount)
            {
                _ = _githubProxyPool.CheckProxies();
            }
        }
        else
        {
            _errorCount = 0;
            if (_configuration.EnableFastGithub)
            {
                _githubProxyPool.IncreaseSuccessCount();
            }
        }
        
        return response;
    }

    private void ReplaceRequestUri(HttpRequestMessage request)
    {
        if (!_configuration.EnableFastGithub) return;
        var patterns = new[]
        {
            "https://raw.githubusercontent.com",
            "https://github.com",
            "https://gist.github.com",
        };
        
        foreach (var pattern in patterns)
        {
            if (request.RequestUri != null && request.RequestUri.ToString().StartsWith(pattern))
            {
                var fastestDomain = _githubProxyPool.GetFastestDomain();
                if (fastestDomain != null)
                {
                    var replacedUri = new Uri(fastestDomain + request.RequestUri);
                    // _logger.LogDebug($"Replacing {request.RequestUri} to {replacedUri}");
                    request.RequestUri = replacedUri;
                }
            }
        }
    }
}