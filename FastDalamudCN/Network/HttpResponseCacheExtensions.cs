using Microsoft.Extensions.Caching.Memory;

namespace FastDalamudCN.Network;

internal static class HttpResponseCacheExtensions
{
    public static bool ShouldCache(this HttpRequestMessage request, Uri uri)
    {
        return request.Method == HttpMethod.Get && !uri.ToString().EndsWith("zip");
    }

    public static CachedHttpResponse? TryGetCachedResponse(this IMemoryCache cache, HttpRequestMessage request,
        Uri originalUri)
    {
        if (!request.ShouldCache(originalUri)) return null;

        if (cache.TryGetValue(originalUri.ToString(), out CachedHttpResponse? cachedItem) && cachedItem != null)
        {
            if (cachedItem.TryUse())
            {
                return cachedItem;
            }
            
            cache.Remove(originalUri.ToString());
        }

        return null;
    }
}