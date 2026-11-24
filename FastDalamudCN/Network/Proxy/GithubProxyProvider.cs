using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Proxy;

internal sealed class GithubProxyProvider : IDisposable
{
    private const string ExpectedSha256 = "1a17f2c74ee9a1c22cb0f04bee90902e0e0c5b1fa739fd3957fcee4f42365c27";
    private const int MaxProxyLatencyMs = 15_000;
    private const int FastProxyWindowMs = 120;

    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubProxyProvider> _logger;
    private readonly string[] _bootstrapNodes = ProxyNodeConfiguration.CreateBootstrapNodes();
    private readonly Timer _timer;

    private volatile IReadOnlyDictionary<string, long> _proxyLatencies;
    private string[] _remoteNodes = [];
    private int _checkInProgress;
    private volatile bool _disposed;

    private int _acceleratedCount;

    public GithubProxyProvider(
        ILogger<GithubProxyProvider> logger,
        Configuration configuration,
        HappyEyeballsCallback happyEyeballsCallback)
    {
        _logger = logger;
        _configuration = configuration;
        _proxyLatencies = _bootstrapNodes.ToDictionary(prefix => prefix, _ => long.MaxValue);

        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            ConnectCallback = happyEyeballsCallback.ConnectCallback
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

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

    public IReadOnlyDictionary<string, long> ProxyLatencies => _proxyLatencies;

    public int AcceleratedCount => _acceleratedCount;

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _httpClient.Dispose();
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
            var measured = await MeasureNodesAsync(knownNodes, cancellationToken);
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
            foreach (var (prefix, latency) in await MeasureNodesAsync(newNodes, cancellationToken))
            {
                measured.Add(prefix, latency);
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
        var availableNodes = GetAvailableNodes(excludePrefixes).ToArray();
        if (availableNodes.Length == 0)
        {
            return null;
        }

        var maxLatency = availableNodes.Min(node => node.Value) + FastProxyWindowMs;
        var candidates = availableNodes.Where(node => node.Value <= maxLatency).ToArray();

        return candidates[Random.Shared.Next(candidates.Length)].Key;
    }

    public List<string> SelectMultipleFastProxies(int count, IEnumerable<string>? excludePrefixes = null)
    {
        return GetAvailableNodes(excludePrefixes)
            .OrderBy(node => node.Value)
            .Take(count)
            .Select(node => node.Key)
            .ToList();
    }

    public void RecordSuccess()
    {
        Interlocked.Increment(ref _acceleratedCount);
    }

    private IEnumerable<KeyValuePair<string, long>> GetAvailableNodes(IEnumerable<string>? excludePrefixes)
    {
        var excluded = excludePrefixes?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _proxyLatencies.Where(node => node.Value < MaxProxyLatencyMs && excluded?.Contains(node.Key) != true);
    }

    private void UpdateProxyLatencies(IReadOnlyDictionary<string, long> measured)
    {
        var activeNodes = _remoteNodes.Any(prefix => measured[prefix] < MaxProxyLatencyMs)
            ? _remoteNodes
            : _bootstrapNodes;

        _proxyLatencies = activeNodes.ToDictionary(prefix => prefix, prefix => measured[prefix]);
    }

    private async Task<Dictionary<string, long>> MeasureNodesAsync(IEnumerable<string> prefixes, CancellationToken cancellationToken)
    {
        var measurements = await Task.WhenAll(prefixes.Select(async prefix =>
            (Prefix: prefix, Latency: await CheckSingleNodeAsync(prefix, cancellationToken))));
        return measurements.ToDictionary(node => node.Prefix, node => node.Latency, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string[]?> FetchRemoteNodesAsync(
        IReadOnlyDictionary<string, long> measured,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in _bootstrapNodes.OrderBy(prefix => measured[prefix]))
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
                var stopwatch = Stopwatch.StartNew();
                using var response = await _httpClient.GetAsync(checkUrl, cancellationToken);
                stopwatch.Stop();

                if (response is { IsSuccessStatusCode: true, Content.Headers.ContentLength: > 0 })
                {
                    var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var sha256 = SHA256.HashData(contentBytes);

                    if (Convert.ToHexStringLower(sha256) != ExpectedSha256)
                    {
                        continue;
                    }

                    minLatencyMs = Math.Min(minLatencyMs, stopwatch.ElapsedMilliseconds);
                }
            }

            return minLatencyMs;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return long.MaxValue;
        }
    }
}
