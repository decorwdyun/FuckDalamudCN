using System.Net;
using System.Security.Cryptography;
using FastDalamudCN.Network;
using FastDalamudCN.Network.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDalamudCN.Tests;

public class GithubProxyProviderTests
{
    private const string FastProxy = "https://fast.example/";
    private const string PeerProxy = "https://peer.example/";
    private const string SlowProxy = "https://slow.example/";
    private static readonly byte[] ProbePayload = "probe-ok"u8.ToArray();
    private static readonly string ProbeSha256 = Convert.ToHexStringLower(SHA256.HashData(ProbePayload));

    [Fact]
    public async Task IncrementalProbingPublishesFastNodeBeforeSlowNodeTimesOut()
    {
        var fastCompletedCount = 0;
        var fastFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            if (request.RequestUri!.ToString().StartsWith(FastProxy, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref fastCompletedCount) == 2)
                {
                    fastFinished.TrySetResult();
                }

                return CreateProbeResponse();
            }

            slowStarted.TrySetResult();
            await slowRelease.Task.WaitAsync(ct);
            return CreateProbeResponse();
        });

        using var provider = new GithubProxyProvider(
            NullLogger<GithubProxyProvider>.Instance,
            new Configuration(),
            new DnsResolver(NullLogger<DnsResolver>.Instance),
            handler,
            [FastProxy, SlowProxy],
            startBackgroundCheck: false,
            probeTimeout: TimeSpan.FromSeconds(5),
            expectedSha256: ProbeSha256);

        var checkTask = provider.CheckProxiesAsync(force: true, refreshConfiguration: false);

        await Task.WhenAll(fastFinished.Task, slowStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (provider.SelectFastestProxy() is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.False(checkTask.IsCompleted);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        slowRelease.TrySetResult();
        await checkTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ExponentialBackoffEscalatesOnConsecutiveFailuresAndDecaysGradually()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            if (request.RequestUri!.ToString().StartsWith(SlowProxy, StringComparison.Ordinal))
            {
                await Task.Delay(160, ct);
            }

            return CreateProbeResponse();
        });

        using var provider = new GithubProxyProvider(
            NullLogger<GithubProxyProvider>.Instance,
            new Configuration(),
            new DnsResolver(NullLogger<DnsResolver>.Instance),
            handler,
            [FastProxy, SlowProxy],
            startBackgroundCheck: false,
            utcNow: () => now,
            probeTimeout: TimeSpan.FromSeconds(2),
            expectedSha256: ProbeSha256,
            nextRandom: _ => 0);

        await provider.CheckProxiesAsync(force: true, refreshConfiguration: false);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        // 1st failure -> 30s penalty
        provider.ReportProxyFailure(new Uri(FastProxy + "https://github.com/a/b"));
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());
        Assert.Equal([SlowProxy, FastProxy], provider.SelectMultipleFastProxies(2));

        now = now.AddSeconds(29);
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());

        // Expires at 30s -> trial recovery puts FastProxy back first
        now = now.AddSeconds(2);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        // Fails again immediately -> 2nd consecutive failure escalates to 2m penalty
        provider.ReportProxyFailure(new Uri(FastProxy + "https://github.com/a/b"));
        now = now.AddSeconds(60);
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());

        now = now.AddSeconds(61);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        // Single success clears active block but only decrements failure history (2 -> 1),
        // so an immediate failure escalates right back to 2m instead of resetting to 30s
        provider.ReportProxyFailure(new Uri(FastProxy + "https://github.com/a/b")); // failures = 3 (5m)
        provider.ReportProxySuccess(new Uri(FastProxy + "https://github.com/a/b"), 10); // active block cleared, failures = 2
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        provider.ReportProxyFailure(new Uri(FastProxy + "https://github.com/a/b")); // failures = 3 (5m)
        now = now.AddMinutes(4);
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());

        now = now.AddMinutes(2);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        // Even when all proxies are penalized, fallback still returns available nodes
        provider.ReportProxyFailure(new Uri(FastProxy + "https://github.com/a/b"));
        provider.ReportProxyFailure(new Uri(SlowProxy + "https://github.com/a/b"));
        Assert.NotNull(provider.SelectFastestProxy());
    }

    [Fact]
    public async Task EwmaRuntimeLatencyDemotesDegradingNodeWithoutWaitingForReprobe()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateProbeResponse()));
        using var provider = new GithubProxyProvider(
            NullLogger<GithubProxyProvider>.Instance,
            new Configuration(),
            new DnsResolver(NullLogger<DnsResolver>.Instance),
            handler,
            [FastProxy, SlowProxy],
            startBackgroundCheck: false,
            probeTimeout: TimeSpan.FromSeconds(2),
            expectedSha256: ProbeSha256,
            nextRandom: _ => 0);

        await provider.CheckProxiesAsync(force: true, refreshConfiguration: false);

        // Seed distinct runtime baselines via EWMA
        provider.RecordProxySuccess(FastProxy, 100);
        provider.RecordProxySuccess(SlowProxy, 250);
        var initialFast = provider.ProxyLatencies[FastProxy];

        // FastProxy degrades to 2500ms on real requests -> EWMA rises rapidly above SlowProxy
        provider.ReportProxySuccess(new Uri(FastProxy + "https://github.com/a/b"), 2500);
        provider.ReportProxySuccess(new Uri(FastProxy + "https://github.com/a/b"), 2500);

        Assert.True(provider.ProxyLatencies[FastProxy] > initialFast);
        Assert.True(provider.ProxyLatencies[FastProxy] > provider.ProxyLatencies[SlowProxy]);
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());
        Assert.Equal([SlowProxy, FastProxy], provider.SelectMultipleFastProxies(2));
    }

    [Theory]
    [InlineData(100, 80)]
    [InlineData(250, 80)]
    [InlineData(400, 120)]
    [InlineData(600, 180)]
    [InlineData(900, 250)]
    [InlineData(1500, 250)]
    public void ComputeFastWindowScalesProportionallyWithinClampedBounds(long minLatencyMs, long expectedWindowMs)
    {
        Assert.Equal(expectedWindowMs, GithubProxyProvider.ComputeFastWindowMs(minLatencyMs));
    }

    [Fact]
    public async Task ProportionalWindowBalancesTopTierPrimaryAndExcludesMuchSlowerNodes()
    {
        var chosenIndex = 0;
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateProbeResponse()));
        using var provider = new GithubProxyProvider(
            NullLogger<GithubProxyProvider>.Instance,
            new Configuration(),
            new DnsResolver(NullLogger<DnsResolver>.Instance),
            handler,
            [FastProxy, PeerProxy, SlowProxy],
            startBackgroundCheck: false,
            probeTimeout: TimeSpan.FromSeconds(2),
            expectedSha256: ProbeSha256,
            nextRandom: _ => chosenIndex);

        await provider.CheckProxiesAsync(force: true, refreshConfiguration: false);

        // Converge latencies: FastProxy ~400ms, PeerProxy ~500ms (within +120ms window), SlowProxy ~800ms (outside 520ms window!)
        for (var i = 0; i < 20; i++)
        {
            provider.RecordProxySuccess(FastProxy, 400);
            provider.RecordProxySuccess(PeerProxy, 500);
            provider.RecordProxySuccess(SlowProxy, 800);
        }

        // Top tier has {FastProxy, PeerProxy}; SlowProxy (800ms) is excluded from top tier
        chosenIndex = 0;
        Assert.Equal(FastProxy, provider.SelectFastestProxy());
        Assert.Equal([FastProxy, PeerProxy, SlowProxy], provider.SelectMultipleFastProxies(3));

        chosenIndex = 1;
        Assert.Equal(PeerProxy, provider.SelectFastestProxy());
        Assert.Equal([PeerProxy, FastProxy, SlowProxy], provider.SelectMultipleFastProxies(3));

        // Even if RNG returns a large index, clamping ensures SlowProxy is never promoted to primary
        chosenIndex = 99;
        Assert.Equal(PeerProxy, provider.SelectFastestProxy());
        Assert.Equal([PeerProxy, FastProxy, SlowProxy], provider.SelectMultipleFastProxies(3));
    }

    [Fact]
    public async Task ProbeTimeoutCutsOffHangingNodeQuickly()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            if (request.RequestUri!.ToString().StartsWith(SlowProxy, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return CreateProbeResponse();
        });

        using var provider = new GithubProxyProvider(
            NullLogger<GithubProxyProvider>.Instance,
            new Configuration(),
            new DnsResolver(NullLogger<DnsResolver>.Instance),
            handler,
            [FastProxy, SlowProxy],
            startBackgroundCheck: false,
            probeTimeout: TimeSpan.FromMilliseconds(120),
            expectedSha256: ProbeSha256);

        await provider.CheckProxiesAsync(force: true, refreshConfiguration: false).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(provider.ProxyLatencies[FastProxy] < GithubProxyProvider.MaxProxyLatencyMs);
        Assert.Equal(long.MaxValue, provider.ProxyLatencies[SlowProxy]);
    }

    private static HttpResponseMessage CreateProbeResponse()
    {
        var content = new ByteArrayContent(ProbePayload);
        content.Headers.ContentLength = ProbePayload.Length;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
