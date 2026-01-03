using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

internal class HappyEyeballsCallback(
    ILogger<HappyEyeballsCallback> logger,
    DnsResolver dnsResolver)
    : IDisposable
{
    private readonly ILogger _logger = logger;
    private const int ConnectionBackoff = 100;
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FailurePenalty = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<IPAddress, DateTime> _penalizedAddresses = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var sortedRecords = await dnsResolver.GetSortedAddressesAsync(context.DnsEndPoint.Host, token);
        var now = DateTime.UtcNow;

        foreach (var (ip, expiry) in _penalizedAddresses)
        {
            if (expiry <= now)
            {
                _penalizedAddresses.TryRemove(ip, out _);
            }
        }

        var prioritized = new List<IPAddress>(sortedRecords.Count);
        var penalized = new List<IPAddress>();
        foreach (var record in sortedRecords)
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

        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task<NetworkStream>>();

        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken.Token);
        for (var i = 0; i < prioritized.Count; i++)
        {
            var record = prioritized[i];

            delayCts.CancelAfter(ConnectionBackoff * i);

            var task = AttemptConnection(record, context.DnsEndPoint.Port, linkedToken.Token, delayCts.Token);
            tasks.Add(task);

            var nextDelayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken.Token);
            _ = task.ContinueWith(_ => { nextDelayCts.Cancel(); }, TaskContinuationOptions.OnlyOnFaulted);
            delayCts = nextDelayCts;
        }

        var stream = await AsyncUtils.FirstSuccessfulTask(tasks).ConfigureAwait(false);

        // If we're here, it means we have a successful connection. A failure to connect would have caused the above
        // line to explode, so we're safe to clean everything up.
        linkedToken.Cancel();
        tasks.ForEach(task => { task.ContinueWith(CleanupConnectionTask); });
// #if DEBUG
        // try
        // {
        //     if (stream is { Socket.RemoteEndPoint: IPEndPoint endPoint })
        //     {
        //         logger.LogTrace("已建立连接: {Hostname} -> {IpAddress}", context.DnsEndPoint.Host, endPoint.Address);
        //     }
        // }
        // catch
        // {
        //     // ignored
        // }
// #endif
        return stream;
    }

    private async Task<NetworkStream> AttemptConnection(IPAddress address, int port, CancellationToken token,
        CancellationToken delayToken)
    {
        await AsyncUtils.CancellableDelay(-1, delayToken).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using var perAttemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        perAttemptCts.CancelAfter(AttemptTimeout);

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(address, port, perAttemptCts.Token).ConfigureAwait(false);
            _penalizedAddresses.TryRemove(address, out _);
            return new NetworkStream(socket, true);
        }
        catch
        {
            _penalizedAddresses[address] = DateTime.UtcNow.Add(FailurePenalty);
            socket.Dispose();
            throw;
        }
    }

    private void CleanupConnectionTask(Task task)
    {
    }
}
