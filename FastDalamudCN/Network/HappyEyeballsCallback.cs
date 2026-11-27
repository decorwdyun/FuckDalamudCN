using System.Collections.Concurrent;
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
            return await ConnectToAddressesAsync(resolution.Addresses, port, token)
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

            _logger.LogDebug("使用 DoH 返回地址重新连接 {Host}", host);
            return await ConnectToAddressesAsync(fallbackAddresses, port, token)
                .ConfigureAwait(false);
        }
    }

    private async Task<Stream> ConnectToAddressesAsync(
        IPAddress[] addresses,
        int port,
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
        prioritized.AddRange(penalized);

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ctsToDispose = new List<CancellationTokenSource>(prioritized.Count + 1) { linkedCts };
        var tasks = new List<Task<Stream>>(prioritized.Count);

        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
        ctsToDispose.Add(delayCts);

        for (var i = 0; i < prioritized.Count; i++)
        {
            var record = prioritized[i];
            delayCts.CancelAfter(_connectionBackoffMs * i);

            var task = AttemptConnection(record, port, linkedCts.Token, delayCts.Token);
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

            _ = CleanupAfterRaceAsync(tasks, winningStream, ctsToDispose);
        }
    }

    private async Task<Stream> AttemptConnection(
        IPAddress address,
        int port,
        CancellationToken token,
        CancellationToken delayToken)
    {
        await AsyncUtils.CancellableDelay(-1, delayToken).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using var perAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        perAttemptCts.CancelAfter(_attemptTimeout);

        try
        {
            var stream = await _connector(address, port, perAttemptCts.Token).ConfigureAwait(false);
            _penalizedAddresses.TryRemove(address, out _);
            return stream;
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
        List<Task<Stream>> tasks,
        Stream? winningStream,
        List<CancellationTokenSource> ctsToDispose)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // 落败任务的取消与连接异常在此统一吸收，防止出现未观察异常
        }

        foreach (var task in tasks)
        {
            if (task.IsCompletedSuccessfully && !ReferenceEquals(task.Result, winningStream))
            {
                try
                {
                    task.Result.Dispose();
                }
                catch
                {
                }
            }
        }

        foreach (var cts in ctsToDispose)
        {
            cts.Dispose();
        }
    }
}
