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
    private readonly bool _enableCaching;
    private readonly MemoryCache _responseCache = new(new MemoryCacheOptions());

    private int _errorCount;
    private const int MaxErrorCount = 10;

    private static string OfficialRepoPattern => "https://aonyx.ffxiv.wang/Plugin/PluginMaster";

    public HttpDelegatingHandler(
        ILogger logger,
        string dalamudVersion,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        HappyEyeballsCallback happyEyeballsCallback,
        bool enableCaching
    )
    {
        _logger = logger;
        _dalamudVersion = dalamudVersion;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _enableCaching = enableCaching;
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
        PrepareRequest(request);
        if (HandleAnalyticsRequest(request) is { } analyticsResponse)
        {
            _logger.LogDebug("Handled analytics request: {RequestUri}", request.RequestUri);
            return analyticsResponse;
        }

        var originalUri = request.RequestUri;

        if (_enableCaching && TryGetCachedResponseAsync(request, originalUri) is { } cachedResponse)
        {
            return cachedResponse;
        }

        try
        {
            var response = await ExecuteRequestWithRetryAsync(request, originalUri, cancellationToken);
            return await HandleSuccessfulResponseAsync(response, request, originalUri, cancellationToken);
        }
        catch (Exception e)
        {
            return HandleRequestException(e, request);
        }
    }

    private HttpResponseMessage? TryGetCachedResponseAsync(HttpRequestMessage request, Uri? originalUri)
    {
        if (request.Method == HttpMethod.Get && originalUri != null && ShouldHandle(originalUri) &&
            !originalUri.ToString().EndsWith("zip"))
        {
            if (_responseCache.TryGetValue(originalUri.ToString(), out CachedHttpResponse? cachedItem) &&
                cachedItem != null)
            {
                if (_configuration.EnableFastGithub)
                {
                    _githubProxyPool.IncreaseSuccessCount();
                }

                return cachedItem.ToHttpResponseMessage(request);
            }
        }

        return null;
    }

    private Task<HttpResponseMessage> ExecuteRequestWithRetryAsync(HttpRequestMessage request, Uri? originalUri,
        CancellationToken cancellationToken)
    {
        if (originalUri == null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(3, _ => TimeSpan.FromSeconds(1), (exception, timeSpan, retryCount, context) =>
            {
                ReplaceRequestUri(request, originalUri);
                _logger.LogWarning("在 {timeSpan} 后进行第 {RetryCount} 次重试，请求URL：{RequestUri}", timeSpan, retryCount,
                    request.RequestUri);
            });

        if (_configuration.EnableFastGithub && ReplaceRequestUri(request, originalUri))
        {
            _logger.LogDebug("FuckDalamudCN 已接管请求: {Method} {originalUri} 至 {RequestUri}", request.Method, originalUri,
                request.RequestUri);
        }

        return retryPolicy.ExecuteAsync(() => base.SendAsync(request, cancellationToken));
    }

    private async Task<HttpResponseMessage> HandleSuccessfulResponseAsync(HttpResponseMessage response,
        HttpRequestMessage request, Uri? originalUri, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode && originalUri != null && !originalUri.ToString().EndsWith("png"))
        {
            _logger.LogWarning("警告: 无法访问: {RequestUri} 状态码：{StatusCode}", originalUri, (int)response.StatusCode);
            _logger.LogWarning($"如果这是你添加的错误/失效的库链，请在你的卫月仓库删除这一条");
            _logger.LogWarning("这并不是插件报错/崩溃，应该不会导致任何后果，不要截图到处问问问。");
            HandleFailedRequest();
            return response;
        }

        _errorCount = 0;
        if (_configuration.EnableFastGithub)
        {
            _githubProxyPool.IncreaseSuccessCount();
        }

        if (_enableCaching &&
            request.Method == HttpMethod.Get
            && originalUri != null
            && ShouldHandle(originalUri) &&
            !originalUri.ToString().EndsWith("zip"))
        {
            var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var cacheEntry = new CachedHttpResponse(response, contentBytes);
            _responseCache.Set(originalUri.ToString(), cacheEntry, TimeSpan.FromMinutes(20));
            return cacheEntry.ToHttpResponseMessage(request);
        }

        return response;
    }

    private HttpResponseMessage HandleRequestException(Exception e, HttpRequestMessage request)
    {
        HandleFailedRequest();
        
        if (request.RequestUri != null && request.RequestUri.ToString().StartsWith(OfficialRepoPattern))
        {
            var fakeResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            _logger.LogWarning(e, "Failed to send HTTP request to official repo, returning fake response: {RequestUri}",
                request.RequestUri);
            return fakeResponse;
        }

        _logger.LogWarning("================================");
        _logger.LogWarning($"如果你能看到这个错误，一般是你自己的网络问题，或者这个网址真的访问不了，你可以复制 {request.RequestUri} 试试浏览器能不能打开。");
        _logger.LogWarning("这并不是插件报错/崩溃，不会导致任何后果，仅作为提示而已，不要截图到处问问问。");
        _logger.LogWarning(e, "错误: 无法访问: {RequestUri}", request.RequestUri);
        _logger.LogWarning("================================");
        throw e;
    }

    private void HandleFailedRequest()
    {
        _errorCount++;
        if (_errorCount >= MaxErrorCount)
        {
            _ = _githubProxyPool.CheckProxies();
        }
    }

    private void PrepareRequest(HttpRequestMessage request)
    {
        request.Headers.Add("User-Agent", $"Dalamud/{_dalamudVersion}");
        if (request.RequestUri?.Host == "aonyx.ffxiv.wang")
        {
            request.Headers.Add("X-Machine-Token", MachineCodeGenerator.Instance.MachineCode);
        }
    }

    private HttpResponseMessage? HandleAnalyticsRequest(HttpRequestMessage request)
    {
        if (request.RequestUri?.ToString() == "https://api.bilibili.com/x/web-interface/zone")
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
            };
        }

        if (request.RequestUri?.ToString() == "https://aonyx.ffxiv.wang/Dalamud/ToS?tosHash=true")
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
            };
        }

        if (request.RequestUri?.Host == "aonyx.ffxiv.wang" &&
            request.RequestUri.ToString().EndsWith("/Dalamud/Analytics/Start"))
        {
            _logger.LogDebug("Blocked analytics request to {RequestUri}", request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            };
        }

        return null;
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

    private bool ReplaceRequestUri(HttpRequestMessage request, Uri originalUri)
    {
        if (!_configuration.EnableFastGithub) return false;
        if (!ShouldHandle(originalUri)) return false;

        var fastestDomain = _githubProxyPool.GetFastestDomain(originalUri.ToString());
        if (fastestDomain != null)
        {
            var replacedUri = new Uri(fastestDomain + originalUri);
            request.RequestUri = replacedUri;
            return true;
        }
        return false;
    }
}