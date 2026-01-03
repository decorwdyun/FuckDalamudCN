using System.Diagnostics;
using System.Net;
using System.Text;
using FastDalamudCN.Network.Abstractions;
using FastDalamudCN.Network.Proxy;
using FastDalamudCN.Utils;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

internal sealed class HttpDelegatingHandler : DelegatingHandler
{
    private const int MaxErrorCount = 10;
    private const string OfficialRepoPattern = "https://aonyx.ffxiv.wang/Plugin/PluginMaster";

    private readonly Configuration _configuration;
    private readonly DalamudVersionProvider _dalamudVersionProvider;
    private readonly GithubProxyProvider _proxyProvider;
    private readonly PluginLocalizationService _pluginLocalizationService;
    private readonly ILogger _logger;
    private readonly IHttpCacheService _httpCacheService;
    private readonly IRequestExecutor _requestExecutor;
    private readonly HijackedPluginRepositoryStore _pluginRepositoryStore;

    private int _errorCount;

    public HttpDelegatingHandler(
        ILogger<HttpDelegatingHandler> logger,
        DalamudVersionProvider dalamudVersionProvider,
        Configuration configuration,
        GithubProxyProvider proxyProvider,
        PluginLocalizationService pluginLocalizationService,
        IHttpCacheService httpCacheService,
        IRequestExecutor requestExecutor,
        HappyEyeballsCallback happyEyeballsCallback,
        HijackedPluginRepositoryStore pluginRepositoryStore)
    {
        _logger = logger;
        _dalamudVersionProvider = dalamudVersionProvider;
        _configuration = configuration;
        _proxyProvider = proxyProvider;
        _pluginLocalizationService = pluginLocalizationService;
        _httpCacheService = httpCacheService;
        _requestExecutor = requestExecutor;
        _pluginRepositoryStore = pluginRepositoryStore;

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
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        if (originalUri == null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (!IsValidUrl(originalUri) && request.Method == HttpMethod.Get)
        {
            // 小店喜欢显示公告？
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
        }

        PrepareRequestHeaders(request);

        var isPluginMaster = _pluginRepositoryStore.ContainsPluginMasterUrl(originalUri.ToString());

        if (request.Method == HttpMethod.Get && _configuration.EnablePluginManifestCache)
        {
            if (_httpCacheService.TryGetCachedResponse(request, originalUri, out var cachedResponse))
            {
                if (RequestFilter.IsGithub(originalUri))
                {
                    _proxyProvider.RecordSuccess();
                }

                _logger.LogTrace("从缓存中读取了 {OriginalUri}", originalUri);
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
                return cachedResponse!;
            }
        }

        try
        {
            HttpResponseMessage response;
            if (isPluginMaster)
            {
                response = await SendWithPluginMasterPolicyAsync(request, cancellationToken);
            }
            else
            {
                response = await _requestExecutor.ExecuteAsync(
                    request,
                    base.SendAsync,
                    cancellationToken);
            }

            return await HandleResponseAsync(response, request, originalUri, cancellationToken);
        }
        catch (Exception e)
        {
            return HandleRequestException(e, request);
        }
    }


    private async Task<HttpResponseMessage> HandleResponseAsync(HttpResponseMessage response,
        HttpRequestMessage request, Uri? originalUri, CancellationToken ct)
    {
        if (originalUri == null)
        {
            return response;
        }

        if (!response.IsSuccessStatusCode && !originalUri.ToString().EndsWith("png"))
        {
            LogFailure(originalUri, response.StatusCode);
            RecordFailure();
            return response;
        }

        _errorCount = 0;
        if (_configuration.EnableFastGithub && RequestFilter.IsGithub(originalUri))
        {
            _proxyProvider.RecordSuccess();
        }

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
        if (_errorCount >= MaxErrorCount)
        {
            _ = _proxyProvider.CheckProxiesAsync();
        }
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

    private async Task<HttpResponseMessage> SendWithPluginMasterPolicyAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var overallBudget = TimeSpan.FromSeconds(11);
        var perAttemptBudget = TimeSpan.FromSeconds(5);
        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var remaining = overallBudget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var timeoutThisAttempt = remaining <= perAttemptBudget ? remaining : perAttemptBudget;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutThisAttempt);

            var clonedRequest = CloneRequest(request);

            try
            {
                lastResponse?.Dispose();
                lastResponse = await _requestExecutor.ExecuteAsync(clonedRequest, base.SendAsync, cts.Token);

                if (lastResponse.IsSuccessStatusCode)
                {
                    return lastResponse;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
                lastResponse?.Dispose();
                lastResponse = null;

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        if (lastResponse != null)
        {
            return lastResponse;
        }

        if (lastException != null)
        {
            throw lastException;
        }

        throw new TimeoutException($"请求 {request.RequestUri} 超过 {overallBudget.TotalSeconds} 秒未完成");
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = request.Content,
            Version = request.Version
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return clone;
    }
}
