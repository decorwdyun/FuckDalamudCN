using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

public interface IHttpCacheService
{
    bool TryGetCachedResponse(HttpRequestMessage request, Uri originalUri, out HttpResponseMessage? cachedResponse);
    Task<HttpResponseMessage> CacheResponseAsync(HttpResponseMessage response, HttpRequestMessage request, Uri originalUri, TimeSpan duration, CancellationToken ct);
    void ClearCache();
}

public class HttpCacheService(ILogger<HttpCacheService> logger) : IHttpCacheService, IDisposable
{
    private MemoryCache _memoryCache = new(new MemoryCacheOptions());

    public bool TryGetCachedResponse(HttpRequestMessage request, Uri originalUri, out HttpResponseMessage? cachedResponse)
    {
        cachedResponse = null;
        var cachedItem = _memoryCache.TryGetCachedResponse(request, originalUri);
        
        if (cachedItem != null)
        {
            cachedResponse = cachedItem.ToHttpResponseMessage(request);
            return true;
        }

        return false;
    }

    public async Task<HttpResponseMessage> CacheResponseAsync(HttpResponseMessage response, HttpRequestMessage request, Uri originalUri, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var contentBytes = await ReadWithTimeoutAsync(response.Content, TimeSpan.FromSeconds(10), ct);
            
            var cacheEntry = new CachedHttpResponse(response, contentBytes);
            
            _memoryCache.Set(originalUri.ToString(), cacheEntry, duration);
            
            logger.LogTrace("已缓存响应: {Uri}, 有效期: {Duration}", originalUri, duration);

            return cacheEntry.ToHttpResponseMessage(request);
        }
        catch (Exception)
        {
            return response; 
        }
    }

    public void ClearCache()
    {
        var oldCache = _memoryCache;
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        oldCache.Dispose();
    }

    private static async Task<byte[]> ReadWithTimeoutAsync(HttpContent content, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await content.ReadAsByteArrayAsync(cts.Token);
    }

    public void Dispose()
    {
        _memoryCache?.Dispose();
    }
}