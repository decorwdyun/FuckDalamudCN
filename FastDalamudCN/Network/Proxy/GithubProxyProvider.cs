using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Proxy;

internal sealed class GithubProxyProvider : IDisposable
{
    internal const string ExpectedSha256 = "1a17f2c74ee9a1c22cb0f04bee90902e0e0c5b1fa739fd3957fcee4f42365c27";
    internal const int MaxProxyLatencyMs = 4_000;
    private const int FastProxyWindowMs = 120;

    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProxyFailurePenalty = TimeSpan.FromSeconds(60);

    private readonly Configuration _configuration;
    private readonly DnsResolver _dnsResolver;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubProxyProvider> _logger;
    private readonly string[] _bootstrapNodes;
    private readonly string _expectedSha256;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _probeTimeout;
    private readonly Timer? _timer;
    private readonly object _latencyLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _penalizedProxies = new(StringComparer.OrdinalIgnoreCase);

    private volatile IReadOnlyDictionary<string, long> _proxyLatencies;
    private string[] _remoteNodes = [];
    private int _checkInProgress;
    private volatile bool _disposed;

    [ActivatorUtilitiesConstructor]
    public GithubProxyProvider(
        ILogger<GithubProxyProvider> logger,
        Configuration configuration,
        HappyEyeballsCallback happyEyeballsCallback,
        DnsResolver dnsResolver)
        : this(
            logger,
            configuration,
            dnsResolver,
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3),
                ConnectCallback = happyEyeballsCallback.ConnectCallback
            },
            ProxyNodeConfiguration.CreateBootstrapNodes(),
            startBackgroundCheck: true,
            disposeHandler: true)
    {
    }

    internal GithubProxyProvider(
        ILogger<GithubProxyProvider> logger,
        Configuration configuration,
        DnsResolver dnsResolver,
        HttpMessageHandler handler,
        string[] bootstrapNodes,
        bool startBackgroundCheck = false,
        Func<DateTime>? utcNow = null,
        TimeSpan? probeTimeout = null,
        bool disposeHandler = false,
        string? expectedSha256 = null)
    {
        _logger = logger;
        _configuration = configuration;
        _dnsResolver = dnsResolver;
        _bootstrapNodes = bootstrapNodes;
        _expectedSha256 = expectedSha256 ?? ExpectedSha256;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _probeTimeout = probeTimeout ?? DefaultProbeTimeout;
        _proxyLatencies = _bootstrapNodes.ToDictionary(prefix => prefix, _ => long.MaxValue, StringComparer.OrdinalIgnoreCase);

        _httpClient = new HttpClient(handler, disposeHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (startBackgroundCheck)
        {
            Task.Run(() => CheckProxiesAsync(force: true, refreshConfiguration: true));

            _timer = new Timer(async void (_) =>
            {
                try
                {
                    await CheckProxiesAsync(refreshConfiguration: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "代理池定时检查任务发生未处理异常");
                }
            }, null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
        }
    }

    public IReadOnlyDictionary<string, long> ProxyLatencies => _proxyLatencies;

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _httpClient.Dispose();
    }

    public void PenalizeProxy(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        _penalizedProxies[prefix] = _utcNow().Add(ProxyFailurePenalty);
    }

    public void ClearProxyPenalty(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        _penalizedProxies.TryRemove(prefix, out _);
    }

    public void ReportProxyFailure(Uri proxyUri)
    {
        if (TryExtractPrefix(proxyUri, out var prefix))
        {
            PenalizeProxy(prefix);
        }
    }

    public void ReportProxySuccess(Uri proxyUri)
    {
        if (TryExtractPrefix(proxyUri, out var prefix))
        {
            ClearProxyPenalty(prefix);
        }
    }

    public async Task CheckProxiesAsync(
        bool force = false,
        bool refreshConfiguration = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || (!_configuration.EnableFastGithub && !force) ||
            Interlocked.Exchange(ref _checkInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var knownNodes = _bootstrapNodes.Concat(_remoteNodes).Distinct(StringComparer.OrdinalIgnoreCase);
            var measured = await MeasureNodesAsync(knownNodes, PublishIncrementalLatency, cancellationToken);
            UpdateProxyLatencies(measured);

            if (!refreshConfiguration || _disposed)
            {
                return;
            }

            var remoteNodes = await FetchRemoteNodesAsync(measured, cancellationToken);
            if (remoteNodes == null)
            {
                return;
            }

            var newNodes = remoteNodes.Where(prefix => !measured.ContainsKey(prefix));
            foreach (var (prefix, latency) in await MeasureNodesAsync(newNodes, null, cancellationToken))
            {
                measured[prefix] = latency;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _remoteNodes = remoteNodes;
            UpdateProxyLatencies(measured);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        finally
        {
            Volatile.Write(ref _checkInProgress, 0);
        }
    }

    public string? SelectFastestProxy(IEnumerable<string>? excludePrefixes = null)
    {
        var (healthy, penalized) = PartitionAvailableNodes(excludePrefixes);
        var pool = healthy.Count > 0 ? healthy : penalized;
        if (pool.Count == 0)
        {
            return null;
        }

        var maxLatency = pool.Min(node => node.Value) + FastProxyWindowMs;
        var candidates = pool.Where(node => node.Value <= maxLatency).ToArray();

        return candidates[Random.Shared.Next(candidates.Length)].Key;
    }

    public List<string> SelectMultipleFastProxies(int count, IEnumerable<string>? excludePrefixes = null)
    {
        var (healthy, penalized) = PartitionAvailableNodes(excludePrefixes);
        return healthy
            .OrderBy(node => node.Value)
            .Concat(penalized.OrderBy(node => node.Value))
            .Take(count)
            .Select(node => node.Key)
            .ToList();
    }

    private (List<KeyValuePair<string, long>> Healthy, List<KeyValuePair<string, long>> Penalized)
        PartitionAvailableNodes(IEnumerable<string>? excludePrefixes)
    {
        var excluded = excludePrefixes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = _utcNow();

        var healthy = new List<KeyValuePair<string, long>>();
        var penalized = new List<KeyValuePair<string, long>>();

        foreach (var node in _proxyLatencies)
        {
            if (node.Value >= MaxProxyLatencyMs || excluded?.Contains(node.Key) == true)
            {
                continue;
            }

            if (_penalizedProxies.TryGetValue(node.Key, out var expiry))
            {
                if (expiry > now)
                {
                    penalized.Add(node);
                    continue;
                }

                _penalizedProxies.TryRemove(new KeyValuePair<string, DateTime>(node.Key, expiry));
            }

            healthy.Add(node);
        }

        return (healthy, penalized);
    }

    private void PublishIncrementalLatency(string prefix, long latency)
    {
        if (_disposed || latency >= MaxProxyLatencyMs)
        {
            return;
        }

        ClearProxyPenalty(prefix);

        lock (_latencyLock)
        {
            var current = _proxyLatencies;
            if (!current.ContainsKey(prefix))
            {
                return;
            }

            var updated = new Dictionary<string, long>(current, StringComparer.OrdinalIgnoreCase)
            {
                [prefix] = latency
            };
            _proxyLatencies = updated;
        }
    }

    private void UpdateProxyLatencies(IReadOnlyDictionary<string, long> measured)
    {
        var activeNodes = _remoteNodes.Any(prefix => measured.TryGetValue(prefix, out var latency) && latency < MaxProxyLatencyMs)
            ? _remoteNodes
            : _bootstrapNodes;

        lock (_latencyLock)
        {
            _proxyLatencies = activeNodes.ToDictionary(
                prefix => prefix,
                prefix => measured.TryGetValue(prefix, out var latency) ? latency : long.MaxValue,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<Dictionary<string, long>> MeasureNodesAsync(
        IEnumerable<string> prefixes,
        Action<string, long>? onNodeMeasured,
        CancellationToken cancellationToken)
    {
        var measurements = await Task.WhenAll(prefixes.Select(async prefix =>
        {
            var latency = await CheckSingleNodeAsync(prefix, cancellationToken);
            onNodeMeasured?.Invoke(prefix, latency);
            return (Prefix: prefix, Latency: latency);
        }));

        return measurements.ToDictionary(node => node.Prefix, node => node.Latency, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string[]?> FetchRemoteNodesAsync(
        IReadOnlyDictionary<string, long> measured,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in _bootstrapNodes.OrderBy(prefix =>
                     measured.TryGetValue(prefix, out var latency) ? latency : long.MaxValue))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, prefix + ProxyNodeConfiguration.RemoteNodesUrl);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();

                var nodes = await ProxyNodeConfiguration.ReadRemoteNodesAsync(response.Content, timeout.Token);
                if (nodes.Length > 0)
                {
                    _logger.LogDebug("已通过 {Proxy} 加载 {NodeCount} 个远端代理节点", prefix, nodes.Length);
                    return nodes;
                }

                _logger.LogDebug("通过 {Proxy} 获取的远端列表没有有效代理节点", prefix);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _logger.LogDebug(ex, "通过 {Proxy} 获取远端代理节点失败", prefix);
            }
        }

        _logger.LogWarning("未能获取远端代理节点，沿用已知的节点配置");
        return null;
    }

    private async Task<long> CheckSingleNodeAsync(string prefix, CancellationToken cancellationToken)
    {
        try
        {
            var checkUrl = prefix + ProxyNodeConfiguration.ProbeFileUrl;
            var minLatencyMs = long.MaxValue;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(_probeTimeout);

                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    using var response = await _httpClient.GetAsync(checkUrl, probeCts.Token);
                    stopwatch.Stop();

                    if (response is { IsSuccessStatusCode: true, Content.Headers.ContentLength: > 0 })
                    {
                        var contentBytes = await response.Content.ReadAsByteArrayAsync(probeCts.Token);
                        var sha256 = SHA256.HashData(contentBytes);

                        if (Convert.ToHexStringLower(sha256) != _expectedSha256)
                        {
                            continue;
                        }

                        minLatencyMs = Math.Min(minLatencyMs, stopwatch.ElapsedMilliseconds);
                    }
                }
                catch (HttpRequestException ex) when (
                    attempt == 0 &&
                    _configuration.EnableDohFallback &&
                    !cancellationToken.IsCancellationRequested &&
                    Uri.TryCreate(checkUrl, UriKind.Absolute, out var probeUri) &&
                    DnsResolver.ShouldTriggerDohFallback(ex))
                {
                    var promoted = await _dnsResolver.TryPromoteHostToDohAsync(
                        probeUri.Host, enableDohFallback: true, cancellationToken);
                    if (!promoted)
                    {
                        break;
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            if (minLatencyMs < MaxProxyLatencyMs)
            {
                ClearProxyPenalty(prefix);
            }

            return minLatencyMs;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return long.MaxValue;
        }
    }

    private static bool TryExtractPrefix(Uri proxyUri, out string prefix)
    {
        if (proxyUri.IsAbsoluteUri && proxyUri.Scheme == Uri.UriSchemeHttps)
        {
            prefix = proxyUri.GetLeftPart(UriPartial.Authority) + "/";
            return true;
        }

        prefix = string.Empty;
        return false;
    }
}
