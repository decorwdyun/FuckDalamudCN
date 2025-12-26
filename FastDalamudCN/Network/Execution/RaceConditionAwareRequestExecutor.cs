using FastDalamudCN.Network;
using FastDalamudCN.Network.Abstractions;
using FastDalamudCN.Network.Proxy;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Execution;

internal sealed class RaceConditionAwareRequestExecutor(
    ILogger<RaceConditionAwareRequestExecutor> logger,
    IProxySelector proxySelector,
    GithubProxyProvider proxyProvider,
    HijackedPluginRepositoryStore pluginRepositoryStore,
    int maxConcurrentRequests = 3)
    : IRequestExecutor
{
    public async Task<HttpResponseMessage> ExecuteAsync(
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        var originalUri = request.RequestUri;
        if (originalUri == null)
        {
            return await sendFunc(request, cancellationToken);
        }

        var isPluginRepoUrl = pluginRepositoryStore.ContainsPluginMasterUrl(originalUri.ToString());
        var shouldRace = isPluginRepoUrl && RequestFilter.IsGithub(originalUri);

        if (shouldRace)
        {
            return await ExecuteRaceAsync(request, originalUri, sendFunc, cancellationToken);
        }

        var proxyUri = proxySelector.BuildProxyUri(originalUri, null);
        if (proxyUri != null)
        {
            request.RequestUri = proxyUri;
        }

        return await sendFunc(request, cancellationToken);
    }
    
    private async Task<HttpResponseMessage> ExecuteRaceAsync(
        HttpRequestMessage originalRequest,
        Uri originalUri,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken)
    {
        var proxyUris = proxySelector.BuildMultipleProxyUris(originalUri, maxConcurrentRequests, null);

        if (proxyUris.Count == 0)
        {
            logger.LogWarning("没有可用的代理节点，回退到原始URL: {Uri}", originalUri);
            return await sendFunc(originalRequest, cancellationToken);
        }

        if (proxyUris.Count == 1)
        {
            originalRequest.RequestUri = proxyUris[0];
            return await sendFunc(originalRequest, cancellationToken);
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new List<Task<(HttpResponseMessage? Response, Uri ProxyUri, Exception? Error)>>();

        foreach (var proxyUri in proxyUris)
        {
            var requestClone = CloneRequest(originalRequest);
            requestClone.RequestUri = proxyUri;

            var task = Task.Run(async () =>
            {
                try
                {
                    var response = await sendFunc(requestClone, raceCts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return (Response: response, ProxyUri: proxyUri, Error: (Exception?)null);
                    }
                    return (Response: (HttpResponseMessage?)null, ProxyUri: proxyUri, Error: new HttpRequestException($"Status: {response.StatusCode}"));
                }
                catch (Exception ex)
                {
                    return (Response: (HttpResponseMessage?)null, ProxyUri: proxyUri, Error: ex);
                }
            }, raceCts.Token);

            tasks.Add(task);
        }
        
        HttpResponseMessage? successResponse = null;
        var completedTasks = 0;
        var errors = new List<(Uri ProxyUri, Exception Error)>();

        while (tasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(tasks);
            tasks.Remove(completedTask);
            completedTasks++;

            try
            {
                var (response, proxyUri, error) = await completedTask;

                if (response != null && response.IsSuccessStatusCode)
                {
                    successResponse = response;
                    raceCts.Cancel();
                    break;
                }

                if (error != null)
                {
                    errors.Add((proxyUri, error));
                    logger.LogTrace("代理失败: {ProxyUri}, 错误: {Error}", proxyUri, error.Message);
                }
            }
            catch (Exception ex)
            {
                logger.LogTrace("竞速任务异常: {Error}", ex.Message);
            }
        }
        
        if (successResponse != null)
        {
            proxyProvider.RecordSuccess();
            return successResponse;
        }
        
        logger.LogWarning("所有代理都失败了 ({Count}), 原始URL: {Uri}", errors.Count, originalUri);
        foreach (var (proxyUri, error) in errors)
        {
            logger.LogDebug("  - {ProxyUri}: {Error}", proxyUri, error.Message);
        }

        throw new HttpRequestException($"所有 {errors.Count} 个代理请求都失败了", errors.FirstOrDefault().Error);
    }

    /// <summary>
    /// 克隆HTTP请求
    /// </summary>
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

        foreach (var property in request.Options)
        {
            clone.Options.TryAdd(property.Key, property.Value);
        }

        return clone;
    }
}
