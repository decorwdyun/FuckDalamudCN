using System.Net;
using System.Text;
using FuckDalamudCN.Utils;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace FuckDalamudCN.FastGithub;

internal sealed class HttpDelegatingHandler : DelegatingHandler
{
    private const int MaxErrorCount = 10;
    private const string OfficialRepoPattern = "https://aonyx.ffxiv.wang/Plugin/PluginMaster";

    private const string ContextKeyUsedProxies = "UsedProxies";
    private const string ContextKeyOriginalUri = "OriginalUri";
    private const string ContextKeyRequest = "Request";
    private readonly Configuration _configuration;
    private readonly DalamudVersionProvider _dalamudVersionProvider;
    private readonly bool _enableCaching;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly ILogger _logger;
    private readonly MemoryCache _responseCache = new(new MemoryCacheOptions());
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    private readonly AsyncTimeoutPolicy<HttpResponseMessage> _timeoutPolicy;

    private int _errorCount;

    public HttpDelegatingHandler(
        ILogger logger,
        DalamudVersionProvider dalamudVersionProvider,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        HappyEyeballsCallback happyEyeballsCallback,
        bool enableCaching)
    {
        _logger = logger;
        _dalamudVersionProvider = dalamudVersionProvider;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _enableCaching = enableCaching;

        InnerHandler = new SocketsHttpHandler
        {
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 10,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = happyEyeballsCallback.ConnectCallback
        };

        _timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(15));
        _retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(3, _ => TimeSpan.FromMilliseconds(500), OnRetry);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        if (originalUri == null) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        PrepareRequestHeaders(request);

        if (RequestFilter.HandleAnalyticsAndPrivacy(request, _logger) is { } blockedResponse)
        {
            var requestBody = await ReadBodySafeAsync(request.Content);
            var responseBody = await ReadBodySafeAsync(blockedResponse.Content);

            _logger.LogInformation(
                "【隐私保护】禁止了一个发往 {RequestUri} 的请求。\n原始请求Body: {RequestBody}\n伪造的响应: {ResponseBody}",
                request.RequestUri,
                requestBody,
                responseBody
            );

            return blockedResponse;
        }

        if (_enableCaching && _configuration.EnableFastGithub)
        {
            var cachedItem = _responseCache.TryGetCachedResponse(request, originalUri);
            if (cachedItem != null)
            {
                var response = cachedItem.ToHttpResponseMessage(request);
                if (RequestFilter.IsGithub(originalUri))
                    _githubProxyPool.IncreaseSuccessCount();
                await Task.Delay(120, cancellationToken).ConfigureAwait(false); // xixi 加点延迟
                return response;
            }
        }

        var context = new Context
        {
            { ContextKeyOriginalUri, originalUri },
            { ContextKeyRequest, request },
            { ContextKeyUsedProxies, new HashSet<string>() }
        };

        try
        {
            TryReplaceUri(request, originalUri, context);

            var pipeline = Policy.WrapAsync(_timeoutPolicy, _retryPolicy);
            var response = await pipeline
                .ExecuteAsync(async _ => await base.SendAsync(request, cancellationToken), context)
                .ConfigureAwait(false);

            return await HandleResponseAsync(response, request, originalUri, cancellationToken);
        }
        catch (Exception e)
        {
            return HandleRequestException(e, request);
        }
    }

    private void OnRetry(DelegateResult<HttpResponseMessage> result, TimeSpan timeSpan, int retryCount, Context context)
    {
        var request = (HttpRequestMessage)context[ContextKeyRequest];
        var originalUri = (Uri?)context[ContextKeyOriginalUri];

        if (originalUri != null) TryReplaceUri(request, originalUri, context);

        _logger.LogWarning("在 {timeSpan} 后进行第 {RetryCount} 次重试，当前请求URL：{RequestUri}", timeSpan, retryCount,
            request.RequestUri);
    }

    private bool TryReplaceUri(HttpRequestMessage request, Uri? originalUri, Context context)
    {
        if (originalUri == null) return false;
        if (!_configuration.EnableFastGithub || !RequestFilter.IsGithub(originalUri)) return false;

        var usedProxies = (HashSet<string>)context[ContextKeyUsedProxies];

        var fastestDomain = _githubProxyPool.GetFastestDomain(originalUri.ToString(), usedProxies);

        if (fastestDomain != null)
        {
            usedProxies.Add(fastestDomain);

            var replacedUri = new Uri(fastestDomain + originalUri);
            if (request.RequestUri != replacedUri)
            {
                request.RequestUri = replacedUri;
                _logger.LogTrace("FastGithub 重定向: {originalUri} -> {RequestUri}", originalUri, request.RequestUri);
                return true;
            }
        }

        return false;
    }

    private async Task<HttpResponseMessage> HandleResponseAsync(HttpResponseMessage response,
        HttpRequestMessage request, Uri? originalUri, CancellationToken ct)
    {
        if (originalUri == null) return response;
        if (!response.IsSuccessStatusCode && !originalUri.ToString().EndsWith("png"))
        {
            LogFailure(originalUri, response.StatusCode);
            RecordFailure();
            return response;
        }

        _errorCount = 0;
        if (_configuration.EnableFastGithub) _githubProxyPool.IncreaseSuccessCount();

        if (_enableCaching && request.ShouldCache(originalUri))
        {
            var contentBytes = await ReadWithTimeoutAsync(response.Content, TimeSpan.FromSeconds(10), ct);
            var cacheEntry = new CachedHttpResponse(response, contentBytes);
            var duration = RequestFilter.IsGithub(originalUri) ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(5);

            _responseCache.Set(originalUri.ToString(), cacheEntry, duration);
            return cacheEntry.ToHttpResponseMessage(request);
        }

        return response;
    }

    private static async Task<byte[]> ReadWithTimeoutAsync(HttpContent content, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await content.ReadAsByteArrayAsync(cts.Token);
    }

    private void RecordFailure()
    {
        _errorCount++;
        if (_errorCount >= MaxErrorCount) _ = _githubProxyPool.CheckProxies();
    }

    private void LogFailure(Uri uri, HttpStatusCode code)
    {
        _logger.LogWarning("警告: 无法访问: {RequestUri} 状态码：{StatusCode}", uri, (int)code);
        _logger.LogWarning("如果这是你添加的错误/失效的库链，请在卫月仓库删除此条。此错误非插件崩溃。");
    }

    private HttpResponseMessage HandleRequestException(Exception e, HttpRequestMessage request)
    {
        RecordFailure();

        if (request.RequestUri?.ToString().StartsWith(OfficialRepoPattern) == true)
        {
            _logger.LogWarning(e, "官方库请求失败，返回伪造响应: {RequestUri}", request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }

        _logger.LogWarning(e, "错误: 无法访问: {RequestUri}. 若能看到此错误，通常为网络问题。", request.RequestUri);
        throw e;
    }

    private void PrepareRequestHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent",
            $"Dalamud/{_dalamudVersionProvider.DalamudAssemblyVersion}");
        if (request.RequestUri?.Host == "aonyx.ffxiv.wang")
            request.Headers.TryAddWithoutValidation("X-Machine-Token", MachineCodeGenerator.Instance.MachineCode);
    }
    
    private static async Task<string> ReadBodySafeAsync(HttpContent? content, int maxLength = 4096)
    {
        if (content == null)
            return string.Empty;

        string body;
        try
        {
            body = await content.ReadAsStringAsync();
        }
        catch
        {
            return "<读取Body失败>";
        }

        if (string.IsNullOrEmpty(body))
            return "<空Body>";

        if (body.Length > maxLength)
            return body.Substring(0, maxLength) + $"...（已截断，原始长度 {body.Length}）";

        return body;
    }
}