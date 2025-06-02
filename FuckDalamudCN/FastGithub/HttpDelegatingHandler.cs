using System.Net;
using Microsoft.Extensions.Logging;
using Polly;

namespace FuckDalamudCN.FastGithub;

internal sealed class HttpDelegatingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly string _dalamudVersion;
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;

    private int _errorCount;
    private const int MaxErrorCount = 10;
    
    private static string OfficialRepoPattern => "https://aonyx.ffxiv.wang/Plugin/PluginMaster";
    
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
            ConnectTimeout = TimeSpan.FromSeconds(10),
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
        var originalUri = request.RequestUri;

        var retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(500 * retryAttempt),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning("Retry {RetryCount} for request {RequestUri} after {TimeSpan}",
                        retryCount, request.RequestUri, timeSpan);
                    ReplaceRequestUri(request, originalUri);
                });

                try
                {
                    ReplaceRequestUri(request, originalUri);
                    var response = await retryPolicy.ExecuteAsync(() => base.SendAsync(request, cancellationToken));
        
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
                catch (Exception e)
                {
                    _errorCount++;

                    if (_errorCount >= MaxErrorCount)
                    {
                        _ = _githubProxyPool.CheckProxies();
                    }
    
                    if (request.RequestUri != null &&
                        request.RequestUri.ToString().StartsWith(OfficialRepoPattern))
                    {
                        var fakeResponse = new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
                            RequestMessage = request
                        };
                        _logger.LogWarning(e, "Failed to send HTTP request to official repo, returning fake response: {RequestUri}", request.RequestUri);
                        return fakeResponse;
                    }
                    _logger.LogError(e, "Failed to send HTTP request: {RequestUri}", request.RequestUri);
                    throw;
                }
    }

    private void ReplaceRequestUri(HttpRequestMessage request, Uri? originalUri)
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
            if (originalUri != null && originalUri.ToString().StartsWith(pattern))
            {
                var fastestDomain = _githubProxyPool.GetFastestDomain();
                if (fastestDomain != null)
                {
                    var replacedUri = new Uri(fastestDomain + originalUri);
                    _logger.LogDebug($"Replacing {originalUri} to {replacedUri}");
                    request.RequestUri = replacedUri;
                }
            }
        }
    }
}