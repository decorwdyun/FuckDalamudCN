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

        // Wait briefly for incremental callback after the 2nd probe returns
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (provider.SelectFastestProxy() is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        // Fast node is already selectable while slow node is still hanging!
        Assert.False(checkTask.IsCompleted);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        slowRelease.TrySetResult();
        await checkTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PenalizedProxyIsDeprioritizedAndRecoversAfterExpiryOrSuccess()
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
            expectedSha256: ProbeSha256);

        await provider.CheckProxiesAsync(force: true, refreshConfiguration: false);

        // Initially FastProxy is faster by >120ms window
        Assert.Equal(FastProxy, provider.SelectFastestProxy());
        Assert.Equal([FastProxy, SlowProxy], provider.SelectMultipleFastProxies(2));

        // Penalizing FastProxy shifts priority to SlowProxy, while keeping FastProxy as fallback
        provider.ReportProxyFailure(new Uri(FastProxy + "https://github.com/a/b"));
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());
        Assert.Equal([SlowProxy, FastProxy], provider.SelectMultipleFastProxies(2));

        // Even if both are penalized, fallback still returns available nodes instead of null
        provider.ReportProxyFailure(new Uri(SlowProxy + "https://github.com/a/b"));
        Assert.NotNull(provider.SelectFastestProxy());

        // Reporting success clears penalty immediately
        provider.ReportProxySuccess(new Uri(FastProxy + "https://github.com/a/b"));
        Assert.Equal(FastProxy, provider.SelectFastestProxy());

        // Penalty also expires after 60 seconds
        provider.PenalizeProxy(FastProxy);
        provider.ClearProxyPenalty(SlowProxy);
        Assert.Equal(SlowProxy, provider.SelectFastestProxy());

        now = now.AddSeconds(61);
        Assert.Equal(FastProxy, provider.SelectFastestProxy());
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
