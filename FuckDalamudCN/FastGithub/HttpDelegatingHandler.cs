using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Polly;

namespace FuckDalamudCN.FastGithub;

internal sealed class HttpDelegatingHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly string _dalamudVersion;
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly MemoryCache _responseCache = new(new MemoryCacheOptions());

    private int _errorCount;
    private const int MaxErrorCount = 10;

    private static string OfficialRepoPattern => "https://aonyx.ffxiv.wang/Plugin/PluginMaster";

    public HttpDelegatingHandler(
        ILogger logger,
        string dalamudVersion,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        HappyEyeballsCallback happyEyeballsCallback
    )
    {
        _logger = logger;
        _dalamudVersion = dalamudVersion;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        InnerHandler = new SocketsHttpHandler
        {
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = happyEyeballsCallback.ConnectCallback,
        };
    }

    private class CachedHttpResponse
    {
        private HttpStatusCode StatusCode { get; }
        private IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }
        private IReadOnlyDictionary<string, IEnumerable<string>> ContentHeaders { get; }
        private byte[] Content { get; }

        public CachedHttpResponse(HttpResponseMessage response, byte[] content)
        {
            StatusCode = response.StatusCode;
            Content = content;
            Headers = response.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            ContentHeaders = response.Content.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        public HttpResponseMessage ToHttpResponseMessage(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new ByteArrayContent(Content),
                RequestMessage = request,
            };

            foreach (var header in Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var header in ContentHeaders)
            {
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        }
    }
    
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Add("User-Agent", $"Dalamud/{_dalamudVersion}");
        if (request.RequestUri?.Host == "aonyx.ffxiv.wang")
        {
            request.Headers.Add("X-Machine-Token", MachineCodeGenerator.Instance.MachineCode);
        }

        var originalUri = request.RequestUri;

        if (request.Method == HttpMethod.Get && originalUri != null && ShouldHandle(originalUri) && !originalUri.ToString().EndsWith("zip"))
        {
            if (_responseCache.TryGetValue(originalUri.ToString(), out CachedHttpResponse? cachedItem))
            {
                if (cachedItem != null)
                {
                    if (_configuration.EnableFastGithub)
                    {
                        _githubProxyPool.IncreaseSuccessCount();
                    }
                    return cachedItem.ToHttpResponseMessage(request);
                }
            }
        }
        
        var retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(2000),
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
                if (request.Method == HttpMethod.Get && originalUri != null && ShouldHandle(originalUri) && !originalUri.ToString().EndsWith("zip"))
                {
                    var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var cacheEntry = new CachedHttpResponse(response, contentBytes);
                    _responseCache.Set(originalUri.ToString(), cacheEntry, TimeSpan.FromMinutes(10));
                    return cacheEntry.ToHttpResponseMessage(request);
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
                _logger.LogWarning(e,
                    "Failed to send HTTP request to official repo, returning fake response: {RequestUri}",
                    request.RequestUri);
                return fakeResponse;
            }

            _logger.LogError(e, "Failed to send HTTP request: {RequestUri}", request.RequestUri);
            throw;
        }
    }

    private bool ShouldHandle(Uri? originalUri)
    {
        if (originalUri == null) return false;

        var patterns = new[]
        {
            "https://raw.githubusercontent.com",
            "https://github.com",
            "https://gist.github.com",
        };

        return patterns.Any(pattern => originalUri.ToString().StartsWith(pattern));
    }
    
    private void ReplaceRequestUri(HttpRequestMessage request, Uri? originalUri)
    {
        if (!_configuration.EnableFastGithub) return;
        if (!ShouldHandle(originalUri)) return;
        
        var fastestDomain = _githubProxyPool.GetFastestDomain();
        if (fastestDomain != null)
        {
            var replacedUri = new Uri(fastestDomain + originalUri);
            // _logger.LogDebug($"Replacing {originalUri} to {replacedUri}");
            request.RequestUri = replacedUri;
        }
    }
}