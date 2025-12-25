using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Proxy;

internal sealed class GithubProxyProvider : IDisposable
{
    private const string ExpectedSha256 = "1a17f2c74ee9a1c22cb0f04bee90902e0e0c5b1fa739fd3957fcee4f42365c27";

    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubProxyProvider> _logger;
    private readonly List<ProxyNode> _nodes;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _checkSemaphore;
    private readonly ConcurrentDictionary<string, long> _proxyLatencies;

    private int _acceleratedCount;

    public GithubProxyProvider(
        ILogger<GithubProxyProvider> logger,
        Configuration configuration,
        HappyEyeballsCallback happyEyeballsCallback)
    {
        _logger = logger;
        _configuration = configuration;
        _nodes = ProxyNodeConfiguration.CreateDefaultNodes();
        _checkSemaphore = new SemaphoreSlim(1, 1);
        _proxyLatencies = new ConcurrentDictionary<string, long>();

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            ConnectCallback = happyEyeballsCallback.ConnectCallback
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        foreach (var node in _nodes)
        {
            _proxyLatencies[node.Prefix] = long.MaxValue;
        }

        Task.Run(() => CheckProxiesAsync(force: true));

        _timer = new Timer(async void (_) =>
        {
            try
            {
                await CheckProxiesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "代理池定时检查任务发生未处理异常");
            }
        }, null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
    }

    public IReadOnlyDictionary<string, long> ProxyLatencies => _proxyLatencies;

    public int AcceleratedCount => _acceleratedCount;

    public void Dispose()
    {
        _timer.Dispose();
        _httpClient.Dispose();
        _checkSemaphore.Dispose();
    }

    public async Task CheckProxiesAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_configuration.EnableFastGithub && !force)
        {
            return;
        }

        if (!await _checkSemaphore.WaitAsync(0, cancellationToken))
        {
            return;
        }

        CancellationTokenSource? cts = null;
        try
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var tasks = _nodes.Select(node => CheckSingleNodeAsync(node, cts.Token)).ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            cts?.Dispose();
            _checkSemaphore.Release();
        }
    }

    public string? SelectFastestProxy(string originalUrl, IEnumerable<string>? excludePrefixes = null)
    {
        var excluded = excludePrefixes?.ToHashSet() ?? new HashSet<string>();

        var validNodes = _nodes
            .Where(node => !excluded.Contains(node.Prefix))
            .Select(node => new NodeWithLatency(
                node,
                _proxyLatencies.GetValueOrDefault(node.Prefix, long.MaxValue)
            ))
            .Where(x => x.Latency < 15000)
            .ToList();

        if (validNodes.Count == 0)
        {
            return null;
        }

        var candidatePrefixes = SelectCandidatesByUrl(originalUrl, validNodes);

        if (candidatePrefixes.Count == 0)
        {
            return null;
        }

        return candidatePrefixes[Random.Shared.Next(candidatePrefixes.Count)];
    }

    public List<string> SelectMultipleFastProxies(string originalUrl, int count, IEnumerable<string>? excludePrefixes = null)
    {
        var excluded = excludePrefixes?.ToHashSet() ?? new HashSet<string>();

        var validNodes = _nodes
            .Where(node => !excluded.Contains(node.Prefix))
            .Select(node => new NodeWithLatency(
                node,
                _proxyLatencies.GetValueOrDefault(node.Prefix, long.MaxValue)
            ))
            .Where(x => x.Latency < 15000)
            .OrderBy(x => x.Latency)
            .ToList();

        if (validNodes.Count == 0) return [];
        
        var candidateNodes = GetCandidateNodesByUrl(originalUrl, validNodes);
        
        return candidateNodes
            .Take(count)
            .Select(x => x.Node.Prefix)
            .ToList();
    }

    public void RecordSuccess()
    {
        Interlocked.Increment(ref _acceleratedCount);
    }

    private record NodeWithLatency(ProxyNode Node, long Latency);

    private List<NodeWithLatency> GetCandidateNodesByUrl(string originalUrl, List<NodeWithLatency> validNodes)
    {
        var isZip = originalUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        if (isZip)
        {
            var zipCandidates = validNodes
                .Where(x => x.Node.Tags.Contains("short-cache"))
                .OrderBy(x => x.Latency)
                .ToList();

            if (zipCandidates.Count > 0)
            {
                return zipCandidates;
            }
        }

        return validNodes.OrderBy(x => x.Latency).ToList();
    }

    private List<string> SelectCandidatesByUrl(string originalUrl, List<NodeWithLatency> validNodes)
    {
        var isZip = originalUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        if (isZip)
        {
            var zipCandidates = validNodes
                .Where(x => x.Node.Tags.Contains("short-cache"))
                .ToList();

            if (zipCandidates.Count > 0)
            {
                var min = zipCandidates.Min(x => x.Latency);
                return zipCandidates
                    .Where(x => x.Latency <= min + 120)
                    .Select(x => x.Node.Prefix)
                    .ToList();
            }
        }

        var minLatency = validNodes.Min(x => x.Latency);
        return validNodes
            .Where(x => x.Latency <= minLatency + 120)
            .Select(x => x.Node.Prefix)
            .ToList();
    }

    private async Task CheckSingleNodeAsync(ProxyNode node, CancellationToken cancellationToken = default)
    {
        try
        {
            var minLatencyMs = double.MaxValue;
            var hasValidResponse = false;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await _httpClient.GetAsync(node.CheckUrl, cancellationToken);
                stopwatch.Stop();

                if (response is { IsSuccessStatusCode: true, Content.Headers.ContentLength: > 0 })
                {
                    var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var sha256 = SHA256.HashData(contentBytes);

                    if (Convert.ToHexStringLower(sha256) != ExpectedSha256)
                    {
                        continue;
                    }

                    var latencyMs = stopwatch.Elapsed.TotalMilliseconds;
                    if (latencyMs < minLatencyMs)
                    {
                        minLatencyMs = latencyMs;
                    }

                    hasValidResponse = true;
                }
            }

            _proxyLatencies[node.Prefix] = hasValidResponse ? (long)minLatencyMs : long.MaxValue;
        }
        catch
        {
            _proxyLatencies[node.Prefix] = long.MaxValue;
        }
    }
}
