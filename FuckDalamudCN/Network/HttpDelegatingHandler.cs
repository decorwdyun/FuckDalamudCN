using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FuckDalamudCN.Utils;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace FuckDalamudCN.Network;

internal sealed partial class HttpDelegatingHandler : DelegatingHandler
{
    private const int MaxErrorCount = 10;
    private const string OfficialRepoPattern = "https://aonyx.ffxiv.wang/Plugin/PluginMaster";

    private const string ContextKeyUsedProxies = "UsedProxies";
    private const string ContextKeyOriginalUri = "OriginalUri";
    private const string ContextKeyRequest = "Request";

    private readonly Configuration _configuration;
    private readonly DalamudVersionProvider _dalamudVersionProvider;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly PluginLocalizationService _pluginLocalizationService;
    private readonly ILogger _logger;
    private readonly IHttpCacheService _httpCacheService;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    private readonly AsyncTimeoutPolicy<HttpResponseMessage> _timeoutPolicy;

    private int _errorCount;

    public HttpDelegatingHandler(
        ILogger<HttpDelegatingHandler> logger,
        DalamudVersionProvider dalamudVersionProvider,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        PluginLocalizationService pluginLocalizationService,
        IHttpCacheService httpCacheService,
        HappyEyeballsCallback happyEyeballsCallback)
    {
        _logger = logger;
        _dalamudVersionProvider = dalamudVersionProvider;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _pluginLocalizationService = pluginLocalizationService;
        _httpCacheService = httpCacheService;

        InnerHandler = new SocketsHttpHandler
        {
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 50,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            Expect100ContinueTimeout = TimeSpan.Zero,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = happyEyeballsCallback.ConnectCallback
        };

        _timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(15));
        _retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync([
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200)
            ], OnRetry);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        if (originalUri == null) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!IsValidUrl(originalUri) && request.Method == HttpMethod.Get)
            // 小店喜欢显示公告？
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"), 
                RequestMessage = request
            };

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

        if (request.Method == HttpMethod.Get && _configuration.EnablePluginManifestCache)
        {
            if (_httpCacheService.TryGetCachedResponse(request, originalUri, out var cachedResponse))
            {
                if (RequestFilter.IsGithub(originalUri))
                    _githubProxyPool.IncreaseSuccessCount();
                _logger.LogTrace($"从缓存中读取了 {originalUri}");
                await Task.Delay(TimeSpan.FromMilliseconds(120));
                return cachedResponse!;
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

    private void TryReplaceUri(HttpRequestMessage request, Uri? originalUri, Context context)
    {
        if (originalUri == null) return;
        if (!_configuration.EnableFastGithub || !RequestFilter.IsGithub(originalUri)) return;

        var usedProxies = (HashSet<string>)context[ContextKeyUsedProxies];
        var normalizedUri = NormalizeGithubRawUri(originalUri);
        var fastestDomain = _githubProxyPool.GetFastestDomain(originalUri.ToString(), usedProxies);

        if (fastestDomain != null)
        {
            usedProxies.Add(fastestDomain);

            var replacedUri = new Uri(fastestDomain + normalizedUri);
            if (request.RequestUri != replacedUri)
            {
                request.RequestUri = replacedUri;
                _logger.LogTrace("重定向: {originalUri} -> {RequestUri}", originalUri, request.RequestUri);
            }
        }
    }

    private Uri? NormalizeGithubRawUri(Uri uri)
    {
        if (uri.Host != "raw.githubusercontent.com") return uri;

        var path = uri.AbsolutePath;
        var regex = RefHead();
        var match = regex.Match(path);

        if (match.Success)
        {
            var normalizedPath =
                $"/{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}{match.Groups[4].Value}";
            var builder = new UriBuilder(uri)
            {
                Path = normalizedPath,
                Query = uri.Query
            };
            return builder.Uri;
        }

        return uri;
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
        if (_configuration.EnableFastGithub && RequestFilter.IsGithub(originalUri))
            _githubProxyPool.IncreaseSuccessCount();


        await _pluginLocalizationService.TranslatePluginDescriptionsAsync(response, originalUri, ct);

        if (_configuration.EnablePluginManifestCache && request.Method == HttpMethod.Get &&
            request.ShouldCache(originalUri))
        {
            var duration = RequestFilter.IsGithub(originalUri) ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);
            return await _httpCacheService.CacheResponseAsync(response, request, originalUri, duration, ct);
        }

        return response;
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

    private static bool IsValidUrl(Uri uri)
    {
        var host = uri.Host;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || host.Contains('.');
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

    [GeneratedRegex(@"^/([^/]+)/([^/]+)/refs/heads/([^/]+)(.*)$")]
    private static partial Regex RefHead();
}