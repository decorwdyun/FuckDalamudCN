using System.Net;
using System.Net.Sockets;
using Dalamud.Logging.Internal;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.FastGithub;

public class HappyEyeballsCallback : IDisposable
{
    private static readonly ModuleLog Log = new("HTTP");

    /*
     * ToDo: Eventually add in some kind of state management to cache DNS and IP Family.
     * For now, this is ignored as the HTTPClient will keep connections alive, but there are benefits to sharing
     * cached lookups between different clients. We just need to be able to easily expire those lookups first.
     */

    private readonly AddressFamily forcedAddressFamily = AddressFamily.Unspecified;
    private readonly ILogger _logger;
    private readonly DynamicHttpWindowsProxy.DynamicHttpWindowsProxy _dynamicHttpWindowsProxy;
    private readonly int connectionBackoff = 75;

    /// <summary>
    /// Initializes a new instance of the <see cref="HappyEyeballsCallback"/> class.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="dynamicHttpWindowsProxy"></param>
    public HappyEyeballsCallback(
        ILogger<HappyEyeballsCallback> logger,
        DynamicHttpWindowsProxy.DynamicHttpWindowsProxy dynamicHttpWindowsProxy
        )
    {
        _logger = logger;
        _dynamicHttpWindowsProxy = dynamicHttpWindowsProxy;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The connection callback to provide to a <see cref="SocketsHttpHandler"/>.
    /// </summary>
    /// <param name="context">The context for an HTTP connection.</param>
    /// <param name="token">The cancellation token to abort this request.</param>
    /// <returns>Returns a Stream for consumption by HttpClient.</returns>
    public async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var sortedRecords = await this.GetSortedAddresses(context.DnsEndPoint.Host, token);

        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task<NetworkStream>>();

        var delayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken.Token);
        for (var i = 0; i < sortedRecords.Count; i++)
        {
            var record = sortedRecords[i];

            delayCts.CancelAfter(this.connectionBackoff * i);

            var task = this.AttemptConnection(record, context.DnsEndPoint.Port, linkedToken.Token, delayCts.Token);
            tasks.Add(task);

            var nextDelayCts = CancellationTokenSource.CreateLinkedTokenSource(linkedToken.Token);
            _ = task.ContinueWith(_ => { nextDelayCts.Cancel(); }, TaskContinuationOptions.OnlyOnFaulted);
            delayCts = nextDelayCts;
        }

        var stream = await AsyncUtils.FirstSuccessfulTask(tasks).ConfigureAwait(false);

        // If we're here, it means we have a successful connection. A failure to connect would have caused the above
        // line to explode, so we're safe to clean everything up.
        linkedToken.Cancel();
        tasks.ForEach(task => { task.ContinueWith(this.CleanupConnectionTask); });

        return stream;
    }

    private async Task<NetworkStream> AttemptConnection(IPAddress address, int port, CancellationToken token, CancellationToken delayToken)
    {
        await AsyncUtils.CancellableDelay(-1, delayToken).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(address, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<List<IPAddress>> GetSortedAddresses(string hostname, CancellationToken token)
    {
        // This method abuses DNS ordering and LINQ a bit. We can normally assume that addresses will be provided in
        // the order the system wants to use. GroupBy will return its groups *in the order they're discovered*. Meaning,
        // the first group created will always be the preferred group, and all other groups are in preference order.
        // This means a straight zipper merge is nice and clean and gives us most -> least preferred, repeating.
        var dnsRecords = await Dns.GetHostAddressesAsync(hostname, this.forcedAddressFamily, token);

        var groups = dnsRecords
                     .GroupBy(a => a.AddressFamily)
                     .Select(g => g.Select(v => v)).ToArray();

        return Util.ZipperMerge(groups).ToList();
    }

    private void CleanupConnectionTask(Task task)
    {

    }
}