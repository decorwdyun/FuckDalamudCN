using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dalamud.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

internal class HappyEyeballsCallback : IDisposable
{
    private const int DefaultConnectionBackoffMs = 100;
    private static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromMilliseconds(2_200);
    private static readonly TimeSpan FailurePenalty = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger;
    private readonly Configuration _configuration;
    private readonly DnsResolver _dnsResolver;
    private readonly Func<IPAddress, int, CancellationToken, Task<Stream>> _connector;
    private readonly int _connectionBackoffMs;
    private readonly TimeSpan _attemptTimeout;
    private readonly ConcurrentDictionary<IPAddress, DateTime> _penalizedAddresses = new();

    [ActivatorUtilitiesConstructor]
    public HappyEyeballsCallback(
        ILogger<HappyEyeballsCallback> logger,
        Configuration configuration,
        DnsResolver dnsResolver)
        : this(logger, configuration, dnsResolver, ConnectTcpAsync, DefaultConnectionBackoffMs, DefaultAttemptTimeout)
    {
    }

    internal HappyEyeballsCallback(
        ILogger<HappyEyeballsCallback> logger,
        Configuration configuration,
        DnsResolver dnsResolver,
        Func<IPAddress, int, CancellationToken, Task<Stream>> connector,
        int connectionBackoffMs = DefaultConnectionBackoffMs,
        TimeSpan? attemptTimeout = null)
    {
        _logger = logger;
        _configuration = configuration;
        _dnsResolver = dnsResolver;
        _connector = connector;
        _connectionBackoffMs = connectionBackoffMs;
        _attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken token) =>
        ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token);

    internal async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken token)
    {
        var resolution = await _dnsResolver.ResolveAsync(
            host,
            _configuration.EnableCloudflarePreferredIp,
            _configuration.EnableDohFallback,
            token).ConfigureAwait(false);

        try
        {
            return await ConnectToAddressesAsync(host, resolution.Addresses, port, resolution.UsedDoh, token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!resolution.UsedDoh &&
                                _configuration.EnableDohFallback &&
                                !token.IsCancellationRequested)
        {
            var fallbackAddresses = await _dnsResolver.TryResolveFallbackViaDohAsync(
                host,
                resolution.Addresses,
                _configuration.EnableCloudflarePreferredIp,
                enableDohFallback: true,
                token).ConfigureAwait(false);

            if (fallbackAddresses is not { Length: > 0 })
            {
                throw;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("[TCP] 初始建连全部失败，使用 DoH 新地址重连: {Host}:{Port} -> [{Addresses}]",
                    host, port, string.Join(", ", fallbackAddresses.Select(a => a.ToString())));
            }

            return await ConnectToAddressesAsync(host, fallbackAddresses, port, usedDoh: true, token)
                .ConfigureAwait(false);
        }
    }

    private async Task<Stream> ConnectToAddressesAsync(
        string host,
        IPAddress[] addresses,
        int port,
        bool usedDoh,
        CancellationToken token)
    {
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var now = DateTime.UtcNow;

        foreach (var (ip, expiry) in _penalizedAddresses)
        {
            if (expiry <= now)
            {
                _penalizedAddresses.TryRemove(ip, out _);
            }
        }

        var prioritized = new List<IPAddress>(addresses.Length);
        var penalized = new List<IPAddress>();
        foreach (var record in addresses)
        {
            if (_penalizedAddresses.TryGetValue(record, out var expiry) && expiry > now)
            {
                penalized.Add(record);
            }
            else
            {
                prioritized.Add(record);
            }
        }
        var penalizedCount = penalized.Count;
        prioritized.AddRange(penalized);

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("[TCP] 开始建连: {Host}:{Port} | 候选=[{Addresses}] (DoH={UsedDoh}, 降权={PenalizedCount})",
                host, port, string.Join(", ", prioritized.Select(a => a.ToString())), usedDoh, penalizedCount);
        }

        var raceStart = Stopwatch.GetTimestamp();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ctsToDispose = new List<CancellationTokenSource>(prioritized.Count + 1) { linkedCts };
        var tasks = new List<Task<Stream>>(prioritized.Count);

        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
        ctsToDispose.Add(delayCts);

        for (var i = 0; i < prioritized.Count; i++)
        {
            var record = prioritized[i];
            delayCts.CancelAfter(_connectionBackoffMs * i);

            var task = AttemptConnection(host, record, port, linkedCts.Token, delayCts.Token);
            tasks.Add(task);

            if (i + 1 < prioritized.Count)
            {
                var nextDelayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                ctsToDispose.Add(nextDelayCts);
                _ = task.ContinueWith(
                    static (_, state) =>
                    {
                        try
                        {
                            ((CancellationTokenSource)state!).Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    nextDelayCts,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                delayCts = nextDelayCts;
            }
        }

        Stream? winningStream = null;
        try
        {
            winningStream = await AsyncUtils.FirstSuccessfulTask(tasks).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                var remoteIp = winningStream is NetworkStream { Socket.RemoteEndPoint: IPEndPoint ep }
                    ? ep.Address.ToString()
                    : "已连接";
                var elapsedMs = (long)Stopwatch.GetElapsedTime(raceStart).TotalMilliseconds;
                _logger.LogTrace("[TCP] 建连成功: {Host}:{Port} -> {RemoteIp} (总耗时 {ElapsedMs}ms)",
                    host, port, remoteIp, elapsedMs);
            }

            return winningStream;
        }
        finally
        {
            try
            {
                linkedCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _ = CleanupAfterRaceAsync(host, port, tasks, winningStream, ctsToDispose, _logger);
        }
    }

    private async Task<Stream> AttemptConnection(
        string host,
        IPAddress address,
        int port,
        CancellationToken token,
        CancellationToken delayToken)
    {
        await AsyncUtils.CancellableDelay(-1, delayToken).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using var perAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        perAttemptCts.CancelAfter(_attemptTimeout);
        var attemptStart = Stopwatch.GetTimestamp();

        try
        {
            var stream = await _connector(address, port, perAttemptCts.Token).ConfigureAwait(false);
            _penalizedAddresses.TryRemove(address, out _);
            return stream;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var elapsedMs = (long)Stopwatch.GetElapsedTime(attemptStart).TotalMilliseconds;
            _penalizedAddresses[address] = DateTime.UtcNow.Add(FailurePenalty);
            var reason = ex is OperationCanceledException ? $"超时({(int)_attemptTimeout.TotalMilliseconds}ms)" : $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogTrace("[TCP] 单点建连失败: {Host} -> {IpAddress}:{Port} | {Reason} (耗时 {ElapsedMs}ms, 惩罚 {PenaltySec}s)",
                host, address, port, reason, elapsedMs, (int)FailurePenalty.TotalSeconds);
            throw;
        }
        catch
        {
            _penalizedAddresses[address] = DateTime.UtcNow.Add(FailurePenalty);
            throw;
        }
    }

    private static async Task<Stream> ConnectTcpAsync(IPAddress address, int port, CancellationToken token)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(address, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task CleanupAfterRaceAsync(
        string host,
        int port,
        List<Task<Stream>> tasks,
        Stream? winningStream,
        List<CancellationTokenSource> ctsToDispose,
        ILogger logger)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 落败任务的取消与连接异常在此统一吸收，防止出现未观察异常
        }

        var disposedLosers = 0;
        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully && !ReferenceEquals(task.Result, winningStream))
            {
                try
                {
                    task.Result.Dispose();
                    disposedLosers++;
                }
                catch
                {
                }
            }
        }

        if (disposedLosers > 0)
        {
            logger.LogTrace("[TCP] 已释放 {Count} 个竞速落败连接: {Host}:{Port}", disposedLosers, host, port);
        }

        foreach (var cts in ctsToDispose)
        {
            cts.Dispose();
        }
    }
}
