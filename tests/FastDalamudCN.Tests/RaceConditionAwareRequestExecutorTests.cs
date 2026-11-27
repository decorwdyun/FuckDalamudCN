using System.Diagnostics;
using System.Net;
using FastDalamudCN.Model;
using FastDalamudCN.Network;
using FastDalamudCN.Network.Abstractions;
using FastDalamudCN.Network.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDalamudCN.Tests;

public class RaceConditionAwareRequestExecutorTests
{
    private static readonly Uri RepoMasterUri = new("https://raw.githubusercontent.com/user/repo/main/pluginmaster.json");
    private static readonly Uri PluginZipUri = new("https://github.com/user/repo/releases/download/v1/plugin.zip");
    private static readonly Uri IconUri = new("https://raw.githubusercontent.com/user/repo/main/icon.png");

    [Fact]
    public async Task HedgedRacingOnlySendsPrimaryWhenPrimaryIsFast()
    {
        var selector = new StubProxySelector("https://p1.example/", "https://p2.example/", "https://p3.example/");
        var store = CreateStore(RepoMasterUri);
        var executor = new RaceConditionAwareRequestExecutor(
            NullLogger<RaceConditionAwareRequestExecutor>.Instance,
            selector,
            (response, _) => Task.FromResult(response),
            store,
            maxConcurrentRequests: 3,
            hedgeDelay: TimeSpan.FromMilliseconds(400));

        var sentUris = new List<Uri>();
        using var request = new HttpRequestMessage(HttpMethod.Get, RepoMasterUri);
        using var response = await executor.ExecuteAsync(request, (req, _) =>
        {
            sentUris.Add(req.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(sentUris);
        Assert.StartsWith("https://p1.example/", sentUris[0].ToString());
        Assert.Single(selector.SuccessReports);
        Assert.Empty(selector.FailureReports);
    }

    [Fact]
    public async Task HedgedRacingLaunchesBackupAfterDelayAndDisposesLosingResponse()
    {
        var selector = new StubProxySelector("https://p1.example/", "https://p2.example/");
        var store = CreateStore(RepoMasterUri);
        var executor = new RaceConditionAwareRequestExecutor(
            NullLogger<RaceConditionAwareRequestExecutor>.Instance,
            selector,
            (response, _) => Task.FromResult(response),
            store,
            maxConcurrentRequests: 2,
            hedgeDelay: TimeSpan.FromMilliseconds(40));

        var p1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var p1Disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var p1Content = new TrackableContent(() => p1Disposed.TrySetResult());
        var p2Content = new TrackableContent();

        using var request = new HttpRequestMessage(HttpMethod.Get, RepoMasterUri);
        using var response = await executor.ExecuteAsync(request, async (req, _) =>
        {
            if (req.RequestUri!.Host == "p1.example")
            {
                await p1Release.Task;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = p1Content };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = p2Content };
        }, default);

        Assert.Same(p2Content, response.Content);
        p1Release.TrySetResult();

        await p1Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(p1Content.Disposed);
        Assert.False(p2Content.Disposed);
    }

    [Fact]
    public async Task HedgedRacingLaunchesNextImmediatelyOnFailureAndDisposesErrorResponse()
    {
        var selector = new StubProxySelector("https://p1.example/", "https://p2.example/");
        var store = CreateStore(RepoMasterUri);
        var executor = new RaceConditionAwareRequestExecutor(
            NullLogger<RaceConditionAwareRequestExecutor>.Instance,
            selector,
            (response, _) => Task.FromResult(response),
            store,
            maxConcurrentRequests: 2,
            hedgeDelay: TimeSpan.FromSeconds(5)); // Large delay proves failure triggers immediate failover

        var failedContent = new TrackableContent();
        var stopwatch = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Get, RepoMasterUri);
        using var response = await executor.ExecuteAsync(request, (req, _) =>
        {
            if (req.RequestUri!.Host == "p1.example")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = failedContent });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, default);

        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(failedContent.Disposed);
        Assert.Contains(selector.FailureReports, uri => uri.Host == "p1.example");
        Assert.Contains(selector.SuccessReports, uri => uri.Host == "p2.example");
    }

    [Fact]
    public async Task SingleRequestFailsOverAcrossProxiesOn5xxAndNetworkErrors()
    {
        var selector = new StubProxySelector("https://p1.example/", "https://p2.example/", "https://p3.example/");
        var store = new HijackedPluginRepositoryStore();
        var executor = new RaceConditionAwareRequestExecutor(
            NullLogger<RaceConditionAwareRequestExecutor>.Instance,
            selector,
            (response, _) => Task.FromResult(response),
            store);

        var p1Content = new TrackableContent();
        var attempts = new List<string>();

        using var request = new HttpRequestMessage(HttpMethod.Get, PluginZipUri);
        using var response = await executor.ExecuteAsync(request, (req, _) =>
        {
            attempts.Add(req.RequestUri!.Host);
            return req.RequestUri.Host switch
            {
                "p1.example" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = p1Content }),
                "p2.example" => Task.FromException<HttpResponseMessage>(new HttpRequestException("reset")),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
            };
        }, default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["p1.example", "p2.example", "p3.example"], attempts);
        Assert.True(p1Content.Disposed);
        Assert.Equal(2, selector.FailureReports.Count);
        Assert.Single(selector.SuccessReports);
    }

    [Fact]
    public async Task SingleRequestDoesNotFailOverOrPenalizeOn404()
    {
        var selector = new StubProxySelector("https://p1.example/", "https://p2.example/");
        var store = new HijackedPluginRepositoryStore();
        var executor = new RaceConditionAwareRequestExecutor(
            NullLogger<RaceConditionAwareRequestExecutor>.Instance,
            selector,
            (response, _) => Task.FromResult(response),
            store);

        var calls = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, IconUri);
        using var response = await executor.ExecuteAsync(request, (_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }, default);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Empty(selector.FailureReports);
    }

    private static HijackedPluginRepositoryStore CreateStore(Uri repoUri)
    {
        var store = new HijackedPluginRepositoryStore();
        store.MergeSnapshots([new HijackedPluginRepositoryInfo(repoUri.ToString(), true)]);
        return store;
    }

    private sealed class TrackableContent(Action? onDispose = null) : ByteArrayContent([1, 2, 3])
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                onDispose?.Invoke();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StubProxySelector(params string[] prefixes) : IProxySelector
    {
        public List<Uri> FailureReports { get; } = [];
        public List<Uri> SuccessReports { get; } = [];

        public Uri? BuildProxyUri(Uri originalUri, HashSet<string>? excludePrefixes = null)
        {
            foreach (var prefix in prefixes)
            {
                if (excludePrefixes?.Contains(prefix) == true)
                {
                    continue;
                }

                excludePrefixes?.Add(prefix);
                return new Uri(prefix + originalUri);
            }

            return null;
        }

        public List<Uri> BuildMultipleProxyUris(Uri originalUri, int count, HashSet<string>? excludePrefixes = null) =>
            prefixes
                .Where(prefix => excludePrefixes?.Contains(prefix) != true)
                .Take(count)
                .Select(prefix => new Uri(prefix + originalUri))
                .ToList();

        public void ReportProxyFailure(Uri proxyUri) => FailureReports.Add(proxyUri);

        public void ReportProxySuccess(Uri proxyUri) => SuccessReports.Add(proxyUri);
    }
}
