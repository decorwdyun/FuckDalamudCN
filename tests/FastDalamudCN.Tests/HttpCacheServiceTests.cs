using System.Net;
using FastDalamudCN.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDalamudCN.Tests;

public class HttpCacheServiceTests
{
    private static readonly Uri RepositoryUri = new("https://repo.example/manifest.json");
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task CacheUsesOriginalUriAndReturnsIndependentResponses()
    {
        using var cache = new HttpCacheService(NullLogger<HttpCacheService>.Instance);
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "https://proxy.example/manifest.json");
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        using var upstream = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StreamContent(stream) };
        upstream.Headers.Add("X-Repository", "main");
        upstream.Content.Headers.ContentType = new("application/json");

        using (var first = await cache.CacheResponseAsync(upstream, proxyRequest, RepositoryUri, Duration, default))
        {
            Assert.Same(proxyRequest, first.RequestMessage);
            Assert.Equal(new byte[] { 1, 2, 3 }, await first.Content.ReadAsByteArrayAsync());
        }
        Assert.False(stream.CanRead);
        Assert.False(cache.TryGetCachedResponse(proxyRequest, proxyRequest.RequestUri!, out _));
        using var post = new HttpRequestMessage(HttpMethod.Post, RepositoryUri);
        Assert.False(cache.TryGetCachedResponse(post, RepositoryUri, out _));

        using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryUri);
        for (var hit = 0; hit < 4; hit++)
        {
            Assert.True(cache.TryGetCachedResponse(request, RepositoryUri, out var response));
            using (response)
            {
                Assert.Same(request, response.RequestMessage);
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                Assert.Equal("main", Assert.Single(response.Headers.GetValues("X-Repository")));
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal(new byte[] { 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
            }
        }
        Assert.False(cache.TryGetCachedResponse(request, RepositoryUri, out _));
    }

    [Fact]
    public async Task ConcurrentReadersShareFourHits()
    {
        using var cache = new HttpCacheService(NullLogger<HttpCacheService>.Instance);
        using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryUri);
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK);
        using var first = await cache.CacheResponseAsync(upstream, request, RepositoryUri, Duration, default);

        var hits = 0;
        Parallel.For(0, 32, _ =>
        {
            if (cache.TryGetCachedResponse(request, RepositoryUri, out var response))
            {
                response.Dispose();
                Interlocked.Increment(ref hits);
            }
        });

        Assert.Equal(4, hits);
    }

    [Fact]
    public async Task ClearCacheAllowsImmediateRefill()
    {
        using var cache = new HttpCacheService(NullLogger<HttpCacheService>.Instance);
        using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryUri);
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK);
        using var first = await cache.CacheResponseAsync(upstream, request, RepositoryUri, Duration, default);
        cache.ClearCache();
        Assert.False(cache.TryGetCachedResponse(request, RepositoryUri, out _));

        using var replacement = new HttpResponseMessage(HttpStatusCode.Created);
        using var second = await cache.CacheResponseAsync(replacement, request, RepositoryUri, Duration, default);
        Assert.True(cache.TryGetCachedResponse(request, RepositoryUri, out var response));
        using (response)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("POST", "https://repo.example/manifest.json")]
    [InlineData("GET", "https://repo.example/plugin.zip")]
    public async Task IneligibleRequestsBypassCache(string method, string url)
    {
        using var cache = new HttpCacheService(NullLogger<HttpCacheService>.Instance);
        var uri = new Uri(url);
        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK);

        Assert.Same(upstream, await cache.CacheResponseAsync(upstream, request, uri, Duration, default));
        using var get = new HttpRequestMessage(HttpMethod.Get, uri);
        Assert.False(cache.TryGetCachedResponse(get, uri, out _));
    }

    [Fact]
    public async Task ReadFailureReturnsOriginalResponse()
    {
        using var cache = new HttpCacheService(NullLogger<HttpCacheService>.Instance);
        using var request = new HttpRequestMessage(HttpMethod.Get, RepositoryUri);
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent() };

        Assert.Same(upstream, await cache.CacheResponseAsync(upstream, request, RepositoryUri, Duration, default));
        Assert.False(cache.TryGetCachedResponse(request, RepositoryUri, out _));
    }

    private sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("Response stream interrupted"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
