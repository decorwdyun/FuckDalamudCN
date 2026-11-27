using System.Net;
using FastDalamudCN.Network.Abstractions;
using FastDalamudCN.Network.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Execution;

internal sealed class RaceConditionAwareRequestExecutor : IRequestExecutor
{
    private const int DefaultMaxConcurrentRequests = 3;
    private static readonly TimeSpan DefaultHedgeDelay = TimeSpan.FromMilliseconds(400);

    private readonly ILogger<RaceConditionAwareRequestExecutor> _logger;
    private readonly IProxySelector _proxySelector;
    private readonly Func<HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>> _trackResponse;
    private readonly HijackedPluginRepositoryStore _pluginRepositoryStore;
    private readonly int _maxConcurrentRequests;
    private readonly TimeSpan _hedgeDelay;

    [ActivatorUtilitiesConstructor]
    public RaceConditionAwareRequestExecutor(
        ILogger<RaceConditionAwareRequestExecutor> logger,
        IProxySelector proxySelector,
        ProxyStatistics statistics,
        HijackedPluginRepositoryStore pluginRepositoryStore)
        : this(
            logger,
            proxySelector,
            statistics.TrackResponseAsync,
            pluginRepositoryStore,
            DefaultMaxConcurrentRequests,
            DefaultHedgeDelay)
    {
    }

    internal RaceConditionAwareRequestExecutor(
        ILogger<RaceConditionAwareRequestExecutor> logger,
        IProxySelector proxySelector,
        Func<HttpResponseMessage, CancellationToken, Task<HttpResponseMessage>> trackResponse,
        HijackedPluginRepositoryStore pluginRepositoryStore,
        int maxConcurrentRequests = DefaultMaxConcurrentRequests,
        TimeSpan? hedgeDelay = null)
    {
        _logger = logger;
        _proxySelector = proxySelector;
        _trackResponse = trackResponse;
        _pluginRepositoryStore = pluginRepositoryStore;
        _maxConcurrentRequests = Math.Max(1, maxConcurrentRequests);
        _hedgeDelay = hedgeDelay ?? DefaultHedgeDelay;
    }

    public async Task<HttpResponseMessage> ExecuteAsync(
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        if (originalUri == null)
        {
            return await sendFunc(request, cancellationToken).ConfigureAwait(false);
        }

        var isPluginRepoUrl = _pluginRepositoryStore.ContainsPluginMasterUrl(originalUri.ToString());
        var shouldRace = isPluginRepoUrl && RequestFilter.IsGithub(originalUri);

        if (shouldRace)
        {
            return await ExecuteRaceAsync(request, originalUri, sendFunc, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteSingleWithFailoverAsync(request, originalUri, sendFunc, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> ExecuteSingleWithFailoverAsync(
        HttpRequestMessage request,
        Uri originalUri,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        var excludedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstProxyUri = _proxySelector.BuildProxyUri(originalUri, excludedPrefixes);
        if (firstProxyUri is null)
        {
            return await sendFunc(request, cancellationToken).ConfigureAwait(false);
        }

        var canRetry = CanRetryRequest(request);
        var maxAttempts = canRetry ? _maxConcurrentRequests : 1;
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;

        var currentProxyUri = firstProxyUri;
        for (var attempt = 0; attempt < maxAttempts && currentProxyUri is not null; attempt++)
        {
            var attemptRequest = canRetry
                ? request.CloneForSend(currentProxyUri)
                : UpdateRequestUri(request, currentProxyUri);

            try
            {
                var response = await sendFunc(attemptRequest, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    lastResponse?.Dispose();
                    _proxySelector.ReportProxySuccess(currentProxyUri);
                    return await _trackResponse(response, cancellationToken).ConfigureAwait(false);
                }

                if (!ShouldRetryProxyStatus(response.StatusCode) || !canRetry)
                {
                    lastResponse?.Dispose();
                    return response;
                }

                _proxySelector.ReportProxyFailure(currentProxyUri);
                lastResponse?.Dispose();
                lastResponse = response;
                _logger.LogDebug("代理返回 {StatusCode}，尝试切换备用代理: {ProxyUri}",
                    (int)response.StatusCode, currentProxyUri);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && canRetry)
            {
                _proxySelector.ReportProxyFailure(currentProxyUri);
                lastException = ex;
                _logger.LogDebug(ex, "代理请求失败，尝试切换备用代理: {ProxyUri}", currentProxyUri);
            }

            if (attempt + 1 < maxAttempts)
            {
                currentProxyUri = _proxySelector.BuildProxyUri(originalUri, excludedPrefixes);
            }
        }

        if (lastResponse is not null)
        {
            return lastResponse;
        }

        if (lastException is not null)
        {
            throw lastException;
        }

        return await sendFunc(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> ExecuteRaceAsync(
        HttpRequestMessage originalRequest,
        Uri originalUri,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        var proxyUris = _proxySelector.BuildMultipleProxyUris(originalUri, _maxConcurrentRequests);

        if (proxyUris.Count == 0)
        {
            _logger.LogWarning("没有可用的代理节点，回退到原始URL: {Uri}", originalUri);
            return await sendFunc(originalRequest, cancellationToken).ConfigureAwait(false);
        }

        if (proxyUris.Count == 1)
        {
            return await SendViaSingleProxyAsync(originalRequest, proxyUris[0], sendFunc, cancellationToken)
                .ConfigureAwait(false);
        }

        var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new List<Task<RaceAttemptResult>>(proxyUris.Count);
        var allTasks = new List<Task<RaceAttemptResult>>(proxyUris.Count);
        var errors = new List<(Uri ProxyUri, Exception Error)>(proxyUris.Count);

        HttpResponseMessage? winningResponse = null;
        var nextIndex = 0;

        void LaunchNext()
        {
            var proxyUri = proxyUris[nextIndex++];
            var task = ExecuteRaceAttemptAsync(originalRequest, proxyUri, sendFunc, raceCts.Token);
            inFlight.Add(task);
            allTasks.Add(task);
        }

        try
        {
            LaunchNext();

            while (inFlight.Count > 0 || nextIndex < proxyUris.Count)
            {
                if (inFlight.Count == 0)
                {
                    LaunchNext();
                    continue;
                }

                if (_hedgeDelay <= TimeSpan.Zero)
                {
                    while (nextIndex < proxyUris.Count)
                    {
                        LaunchNext();
                    }
                }

                var anyCompleted = Task.WhenAny(inFlight);
                Task<RaceAttemptResult> completedTask;

                if (nextIndex < proxyUris.Count)
                {
                    using var hedgeCts = CancellationTokenSource.CreateLinkedTokenSource(raceCts.Token);
                    var delayTask = Task.Delay(_hedgeDelay, hedgeCts.Token);
                    var finished = await Task.WhenAny(anyCompleted, delayTask).ConfigureAwait(false);

                    if (finished == delayTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        LaunchNext();
                        continue;
                    }

                    hedgeCts.Cancel();
                    completedTask = await anyCompleted.ConfigureAwait(false);
                }
                else
                {
                    completedTask = await anyCompleted.ConfigureAwait(false);
                }

                inFlight.Remove(completedTask);
                var result = await completedTask.ConfigureAwait(false);

                if (result.Response is { IsSuccessStatusCode: true })
                {
                    winningResponse = result.Response;
                    _proxySelector.ReportProxySuccess(result.ProxyUri);
                    break;
                }

                if (result.ShouldPenalizeProxy)
                {
                    _proxySelector.ReportProxyFailure(result.ProxyUri);
                }

                if (result.Error is not null)
                {
                    errors.Add((result.ProxyUri, result.Error));
                    _logger.LogTrace("代理失败: {ProxyUri}, 错误: {Error}", result.ProxyUri, result.Error.Message);
                }
            }
        }
        finally
        {
            try
            {
                raceCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _ = CleanupRaceAsync(allTasks, winningResponse, raceCts);
        }

        if (winningResponse is not null)
        {
            return await _trackResponse(winningResponse, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogWarning("所有代理都失败了 ({Count}), 原始URL: {Uri}", errors.Count, originalUri);
        foreach (var (proxyUri, error) in errors)
        {
            _logger.LogDebug("  - {ProxyUri}: {Error}", proxyUri, error.Message);
        }

        throw new HttpRequestException($"所有 {errors.Count} 个代理请求都失败了", errors.FirstOrDefault().Error);
    }

    private async Task<HttpResponseMessage> SendViaSingleProxyAsync(
        HttpRequestMessage request,
        Uri proxyUri,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        request.RequestUri = proxyUri;
        try
        {
            var response = await sendFunc(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _proxySelector.ReportProxySuccess(proxyUri);
                return await _trackResponse(response, cancellationToken).ConfigureAwait(false);
            }

            if (ShouldRetryProxyStatus(response.StatusCode))
            {
                _proxySelector.ReportProxyFailure(proxyUri);
            }

            return response;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _proxySelector.ReportProxyFailure(proxyUri);
            throw;
        }
    }

    private static async Task<RaceAttemptResult> ExecuteRaceAttemptAsync(
        HttpRequestMessage originalRequest,
        Uri proxyUri,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        var requestClone = originalRequest.CloneForSend(proxyUri);
        try
        {
            var response = await sendFunc(requestClone, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new RaceAttemptResult(response, proxyUri, Error: null, ShouldPenalizeProxy: false);
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            return new RaceAttemptResult(
                Response: null,
                ProxyUri: proxyUri,
                Error: new HttpRequestException($"Status: {statusCode}", null, statusCode),
                ShouldPenalizeProxy: ShouldRetryProxyStatus(statusCode));
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return new RaceAttemptResult(Response: null, ProxyUri: proxyUri, Error: ex, ShouldPenalizeProxy: false);
        }
        catch (Exception ex)
        {
            return new RaceAttemptResult(Response: null, ProxyUri: proxyUri, Error: ex, ShouldPenalizeProxy: true);
        }
    }

    private static async Task CleanupRaceAsync(
        List<Task<RaceAttemptResult>> tasks,
        HttpResponseMessage? winningResponse,
        CancellationTokenSource raceCts)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 落败任务异常已在 ExecuteRaceAttemptAsync 捕获，此处仅兜底防止未观察异常
        }

        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully &&
                task.Result.Response is { } response &&
                !ReferenceEquals(response, winningResponse))
            {
                response.Dispose();
            }
        }

        raceCts.Dispose();
    }

    private static HttpRequestMessage UpdateRequestUri(HttpRequestMessage request, Uri proxyUri)
    {
        request.RequestUri = proxyUri;
        return request;
    }

    private static bool CanRetryRequest(HttpRequestMessage request) =>
        request.Content is null || request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;

    private static bool ShouldRetryProxyStatus(HttpStatusCode statusCode) =>
        (int)statusCode >= 500 ||
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden;

    private readonly record struct RaceAttemptResult(
        HttpResponseMessage? Response,
        Uri ProxyUri,
        Exception? Error,
        bool ShouldPenalizeProxy);
}
