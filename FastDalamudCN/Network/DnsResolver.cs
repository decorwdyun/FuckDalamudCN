using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

internal sealed class DnsResolver(
    ILogger<DnsResolver> logger,
    Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> lookup)
{
    public DnsResolver(ILogger<DnsResolver> logger) : this(logger, Dns.GetHostAddressesAsync)
    {
    }

    private const string PreferredHost = "cf.951886.xyz";
    private static readonly IPNetwork[] CloudflareRanges =
    [
        IPNetwork.Parse("173.245.48.0/20"),
        IPNetwork.Parse("103.21.244.0/22"),
        IPNetwork.Parse("103.22.200.0/22"),
        IPNetwork.Parse("103.31.4.0/22"),
        IPNetwork.Parse("141.101.64.0/18"),
        IPNetwork.Parse("108.162.192.0/18"),
        IPNetwork.Parse("190.93.240.0/20"),
        IPNetwork.Parse("188.114.96.0/20"),
        IPNetwork.Parse("197.234.240.0/22"),
        IPNetwork.Parse("198.41.128.0/17"),
        IPNetwork.Parse("162.158.0.0/15"),
        IPNetwork.Parse("104.16.0.0/13"),
        IPNetwork.Parse("104.24.0.0/14"),
        IPNetwork.Parse("172.64.0.0/13"),
        IPNetwork.Parse("131.0.72.0/22")
    ];

    public async Task<IPAddress[]> GetSortedAddressesAsync(string hostname, bool preferCloudflare, CancellationToken token)
    {
        var original = await lookup(hostname, AddressFamily.InterNetwork, token);
        if (!preferCloudflare || original.Length == 0 || !original.All(IsCloudflareAddress))
            return original;

        try
        {
            var candidates = await lookup(PreferredHost, AddressFamily.InterNetwork, token);
            var preferred = candidates.Where(IsCloudflareAddress).Distinct().Take(3).ToArray();
            if (preferred.Length > 0)
                return preferred.Concat(original).Distinct().ToArray();

            logger.LogWarning("{PreferredHost} 未返回 Cloudflare IPv4 地址，使用 {Hostname} 的原始地址", PreferredHost, hostname);
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "解析 {PreferredHost} 失败，使用 {Hostname} 的原始地址", PreferredHost, hostname);
        }

        return original;
    }

    private static bool IsCloudflareAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && CloudflareRanges.Any(range => range.Contains(address));
}
