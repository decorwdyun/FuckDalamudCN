using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using FastDalamudCN.Network.Abstractions;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Dns;

internal sealed class DohDnsClient : IDohDnsClient, IDisposable
{
    private const string DnsMessageMediaType = "application/dns-message";
    private const int MaxResponseBytes = 65_535;
    private const int MaxCacheEntries = 512;

    private static readonly TimeSpan DefaultPerEndpointTimeout = TimeSpan.FromMilliseconds(1_200);
    private static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromMilliseconds(2_500);
    private static readonly TimeSpan MinCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EndpointFailurePenalty = TimeSpan.FromSeconds(60);

    internal static readonly Uri[] DefaultEndpoints =
    [
        new("https://223.5.5.5/dns-query"),
        new("https://1.12.12.12/dns-query"),
        new("https://223.6.6.6/dns-query"),
        new("https://120.53.53.53/dns-query")
    ];

    private readonly ILogger<DohDnsClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<Uri> _endpoints;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _perEndpointTimeout;
    private readonly TimeSpan _overallTimeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<IPAddress[]>>> _inFlight = new();
    private readonly ConcurrentDictionary<Uri, DateTime> _penalizedEndpoints = new();

    private long _cacheGeneration;
    private int _disposed;

    public DohDnsClient(ILogger<DohDnsClient> logger)
        : this(
            logger,
            CreateDefaultHandler(),
            DefaultEndpoints,
            disposeHandler: true)
    {
    }

    internal DohDnsClient(
        ILogger<DohDnsClient> logger,
        HttpMessageHandler handler,
        IReadOnlyList<Uri> endpoints,
        Func<DateTime>? utcNow = null,
        TimeSpan? perEndpointTimeout = null,
        TimeSpan? overallTimeout = null,
        bool disposeHandler = false)
    {
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("至少需要配置一个 DoH 端点。", nameof(endpoints));
        }

        _logger = logger;
        _endpoints = endpoints;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _perEndpointTimeout = perEndpointTimeout ?? DefaultPerEndpointTimeout;
        _overallTimeout = overallTimeout ?? DefaultOverallTimeout;
        _httpClient = new HttpClient(handler, disposeHandler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<IPAddress[]> ResolveAsync(
        string hostname,
        AddressFamily addressFamily,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedHost = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        var key = new CacheKey(normalizedHost, addressFamily);
        var now = _utcNow();

        if (_cache.TryGetValue(key, out var cached))
        {
            if (cached.ExpiresAtUtc > now)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    var remainingSec = Math.Max(0, (cached.ExpiresAtUtc - now).TotalSeconds);
                    _logger.LogTrace("[DoH] 命中缓存: {Hostname} ({Family}) -> [{Addresses}] (剩余 {RemainingSec:F0}s)",
                        normalizedHost, addressFamily, FormatAddresses(cached.Addresses), remainingSec);
                }

                return cached.Addresses;
            }

            _cache.TryRemove(new KeyValuePair<CacheKey, CacheEntry>(key, cached));
        }

        var generation = Volatile.Read(ref _cacheGeneration);
        var joinedInFlight = true;
        var sharedLazy = _inFlight.GetOrAdd(key, cacheKey =>
        {
            joinedInFlight = false;
            Lazy<Task<IPAddress[]>>? self = null;
            self = new Lazy<Task<IPAddress[]>>(
                () => ExecuteLookupAndCleanupAsync(cacheKey, generation, self!),
                LazyThreadSafetyMode.ExecutionAndPublication);
            return self;
        });

        if (joinedInFlight && _logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("[DoH] 合并并发查询: {Hostname} ({Family})", normalizedHost, addressFamily);
        }

        try
        {
            return await sharedLazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    public void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        _cache.Clear();
        _penalizedEndpoints.Clear();
        _logger.LogTrace("[DoH] 已清空缓存与端点惩罚记录");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _httpClient.Dispose();
    }

    private async Task<IPAddress[]> ExecuteLookupAndCleanupAsync(
        CacheKey key,
        long generation,
        Lazy<Task<IPAddress[]>> self)
    {
        try
        {
            return await QueryEndpointsAsync(key, generation).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<CacheKey, Lazy<Task<IPAddress[]>>>(key, self));
        }
    }

    private async Task<IPAddress[]> QueryEndpointsAsync(CacheKey key, long generation)
    {
        var queryPacket = DnsWireCodec.BuildQuery(key.Hostname, key.AddressFamily);
        var orderedEndpoints = GetOrderedEndpoints();

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
        overallCts.CancelAfter(_overallTimeout);
        var startTimestamp = Stopwatch.GetTimestamp();

        Exception? lastException = null;
        var attemptedCount = 0;

        foreach (var endpoint in orderedEndpoints)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var remaining = _overallTimeout - elapsed;
            if (remaining <= TimeSpan.Zero || overallCts.IsCancellationRequested)
            {
                break;
            }

            attemptedCount++;
            var endpointBudget = remaining < _perEndpointTimeout ? remaining : _perEndpointTimeout;
            using var endpointCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            endpointCts.CancelAfter(endpointBudget);
            var endpointStart = Stopwatch.GetTimestamp();

            try
            {
                var wireResponse = await SendQueryAsync(endpoint, queryPacket, key.AddressFamily, endpointCts.Token)
                    .ConfigureAwait(false);
                var endpointMs = (long)Stopwatch.GetElapsedTime(endpointStart).TotalMilliseconds;

                if (wireResponse.ResponseCode == DnsWireCodec.NoError)
                {
                    _penalizedEndpoints.TryRemove(endpoint, out _);
                    if (wireResponse.Addresses.Length > 0)
                    {
                        StoreCacheEntry(key, wireResponse.Addresses, wireResponse.MinTtl, generation);
                    }

                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        var clampedTtlSec = (int)ClampTtl(wireResponse.MinTtl).TotalSeconds;
                        _logger.LogTrace("[DoH] 解析成功: {Hostname} ({Family}) via {Endpoint} -> [{Addresses}] (TTL={TtlSec}s, 耗时 {ElapsedMs}ms)",
                            key.Hostname, key.AddressFamily, endpoint.Host, FormatAddresses(wireResponse.Addresses), clampedTtlSec, endpointMs);
                    }

                    return wireResponse.Addresses;
                }

                if (wireResponse.ResponseCode == DnsWireCodec.NameError)
                {
                    _penalizedEndpoints.TryRemove(endpoint, out _);
                    _logger.LogTrace("[DoH] 域名不存在(NXDOMAIN): {Hostname} ({Family}) via {Endpoint} (耗时 {ElapsedMs}ms)",
                        key.Hostname, key.AddressFamily, endpoint.Host, endpointMs);
                    return [];
                }

                _penalizedEndpoints[endpoint] = _utcNow().Add(EndpointFailurePenalty);
                lastException = new HttpRequestException(
                    $"DoH 端点 {endpoint} 返回异常 RCODE={wireResponse.ResponseCode}");
                _logger.LogTrace("[DoH] 端点异常响应: {Hostname} via {Endpoint} | RCODE={RCode} (耗时 {ElapsedMs}ms, 惩罚 {PenaltySec}s)",
                    key.Hostname, endpoint.Host, wireResponse.ResponseCode, endpointMs, (int)EndpointFailurePenalty.TotalSeconds);
            }
            catch (OperationCanceledException ex) when (_disposeCts.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(DohDnsClient), ex);
            }
            catch (Exception ex)
            {
                var endpointMs = (long)Stopwatch.GetElapsedTime(endpointStart).TotalMilliseconds;
                _penalizedEndpoints[endpoint] = _utcNow().Add(EndpointFailurePenalty);
                lastException = ex;
                var reason = ex is OperationCanceledException ? "超时" : $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogTrace("[DoH] 端点失败: {Hostname} via {Endpoint} | {Reason} (耗时 {ElapsedMs}ms, 惩罚 {PenaltySec}s)",
                    key.Hostname, endpoint.Host, reason, endpointMs, (int)EndpointFailurePenalty.TotalSeconds);
            }
        }

        var totalMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _logger.LogTrace("[DoH] 全部端点失败: {Hostname} ({Family}) | 已尝试 {AttemptedCount}/{TotalEndpoints} 个端点, 总耗时 {TotalMs}ms",
            key.Hostname, key.AddressFamily, attemptedCount, orderedEndpoints.Count, totalMs);

        throw lastException ?? new TimeoutException($"DoH 解析 {key.Hostname} 超时");
    }

    private async Task<DnsWireResponse> SendQueryAsync(
        Uri endpoint,
        byte[] queryPacket,
        AddressFamily addressFamily,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new ByteArrayContent(queryPacket)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(DnsMessageMediaType);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(DnsMessageMediaType));

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new FormatException($"DoH 响应体过大: {response.Content.Headers.ContentLength} 字节。");
        }

        var responseBytes = await ReadBoundedBytesAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return DnsWireCodec.ParseResponse(responseBytes, addressFamily);
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxResponseBytes)
            {
                throw new FormatException("DoH 响应体超过最大长度限制。");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private List<Uri> GetOrderedEndpoints()
    {
        var now = _utcNow();
        var healthy = new List<Uri>(_endpoints.Count);
        var penalized = new List<Uri>();

        foreach (var endpoint in _endpoints)
        {
            if (_penalizedEndpoints.TryGetValue(endpoint, out var expiry))
            {
                if (expiry > now)
                {
                    penalized.Add(endpoint);
                    continue;
                }

                _penalizedEndpoints.TryRemove(endpoint, out _);
            }

            healthy.Add(endpoint);
        }

        healthy.AddRange(penalized);
        return healthy;
    }

    private void StoreCacheEntry(CacheKey key, IPAddress[] addresses, TimeSpan? minTtl, long generation)
    {
        if (Volatile.Read(ref _cacheGeneration) != generation)
        {
            return;
        }

        var ttl = ClampTtl(minTtl);
        var now = _utcNow();

        if (_cache.Count >= MaxCacheEntries)
        {
            PruneExpiredEntries(now);
        }

        _cache[key] = new CacheEntry(addresses, now.Add(ttl));
    }

    private void PruneExpiredEntries(DateTime now)
    {
        foreach (var (cacheKey, entry) in _cache)
        {
            if (entry.ExpiresAtUtc <= now)
            {
                _cache.TryRemove(new KeyValuePair<CacheKey, CacheEntry>(cacheKey, entry));
            }
        }
    }

    private static TimeSpan ClampTtl(TimeSpan? ttl)
    {
        if (ttl is null || ttl.Value < MinCacheTtl)
        {
            return MinCacheTtl;
        }

        return ttl.Value > MaxCacheTtl ? MaxCacheTtl : ttl.Value;
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromMilliseconds(1_500),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = DecompressionMethods.None
    };

    private static string FormatAddresses(IPAddress[] addresses) =>
        addresses.Length == 0 ? "空" : string.Join(", ", addresses.Select(a => a.ToString()));

    private readonly record struct CacheKey(string Hostname, AddressFamily AddressFamily);

    private readonly record struct CacheEntry(IPAddress[] Addresses, DateTime ExpiresAtUtc);
}
