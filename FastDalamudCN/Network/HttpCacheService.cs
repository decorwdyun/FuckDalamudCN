using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

public interface IHttpCacheService
{
    bool TryGetCachedResponse(HttpRequestMessage request, Uri originalUri,
        [NotNullWhen(true)] out HttpResponseMessage? cachedResponse);
    Task<HttpResponseMessage> CacheResponseAsync(HttpResponseMessage response, HttpRequestMessage request,
        Uri originalUri, TimeSpan duration, CancellationToken ct);
    void ClearCache();
}

public class HttpCacheService(ILogger<HttpCacheService> logger) : IHttpCacheService, IDisposable
{
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());

    public bool TryGetCachedResponse(HttpRequestMessage request, Uri originalUri,
        [NotNullWhen(true)] out HttpResponseMessage? cachedResponse)
    {
        if (CanCache(request, originalUri) &&
            _memoryCache.TryGetValue(originalUri.ToString(), out CachedHttpResponse? entry) &&
            entry is not null && entry.TryUse())
        {
            cachedResponse = entry.ToHttpResponseMessage(request);
            return true;
        }

        // 耗尽的条目由后续写入替换或自然过期，避免按键删除并发写入的新条目。
        cachedResponse = null;
        return false;
    }

    public async Task<HttpResponseMessage> CacheResponseAsync(HttpResponseMessage response, HttpRequestMessage request,
        Uri originalUri, TimeSpan duration, CancellationToken ct)
    {
        if (!CanCache(request, originalUri))
        {
            return response;
        }

        HttpResponseMessage cachedResponse;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var content = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var entry = new CachedHttpResponse(response, content);
            _memoryCache.Set(originalUri.ToString(), entry, duration);
            logger.LogTrace("已缓存响应: {Uri}, 有效期: {Duration}", originalUri, duration);
            cachedResponse = entry.ToHttpResponseMessage(request);
        }
        catch (Exception)
        {
            return response;
        }

        response.Dispose();
        return cachedResponse;
    }

    public void ClearCache() => _memoryCache.Clear();

    public void Dispose() => _memoryCache.Dispose();

    private static bool CanCache(HttpRequestMessage request, Uri originalUri)
    {
        return request.Method == HttpMethod.Get && !originalUri.ToString().EndsWith("zip");
    }
}
