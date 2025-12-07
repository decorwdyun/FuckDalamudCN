using Microsoft.Extensions.Caching.Memory;

namespace FuckDalamudCN.FastGithub;

internal static class HttpResponseCacheExtensions
{
    public static bool ShouldCache(this HttpRequestMessage request, Uri uri)
    {
        return request.Method == HttpMethod.Get && !uri.ToString().EndsWith("zip");
    }

    public static CachedHttpResponse? TryGetCachedResponse(this IMemoryCache cache, HttpRequestMessage request,
        Uri originalUri, bool enableFastGithub, GithubProxyPool proxyPool)
    {
        if (!request.ShouldCache(originalUri)) return null;

        if (cache.TryGetValue(originalUri.ToString(), out CachedHttpResponse? cachedItem) && cachedItem != null)
        {
            if (enableFastGithub) proxyPool.IncreaseSuccessCount();
            return cachedItem;
        }

        return null;
    }
}