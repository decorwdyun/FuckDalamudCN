using System.Net;
using System.Net.Sockets;

namespace FastDalamudCN.Network.Abstractions;

internal interface IDohDnsClient
{
    Task<IPAddress[]> ResolveAsync(string hostname, AddressFamily addressFamily, CancellationToken cancellationToken);

    void ClearCache();
}
