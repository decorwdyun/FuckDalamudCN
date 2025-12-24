using System.Net;
using System.Net.Sockets;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

internal class DnsResolver(ILogger<DnsResolver> logger) : IDisposable
{
    private const string HijackCname = "cf-cname.xingpingcn.top";

    private const AddressFamily ForcedAddressFamily = AddressFamily.InterNetwork;

    private static readonly List<CidrRange> CachedRanges;

    static DnsResolver()
    {
        var rawRanges = new List<(string Ip, int Prefix)>
        {
            ("173.245.48.0", 20),
            ("103.21.244.0", 22),
            ("103.22.200.0", 22),
            ("103.31.4.0", 22),
            ("141.101.64.0", 18),
            ("108.162.192.0", 18),
            ("190.93.240.0", 20),
            ("188.114.96.0", 20),
            ("197.234.240.0", 22),
            ("198.41.128.0", 17),
            ("162.158.0.0", 15),
            ("104.16.0.0", 13),
            ("104.24.0.0", 14),
            ("172.64.0.0", 13),
            ("131.0.72.0", 22)
        };

        CachedRanges = new List<CidrRange>(rawRanges.Count);
        foreach (var (ipStr, prefix) in rawRanges)
        {
            CachedRanges.Add(new CidrRange(IPAddress.Parse(ipStr), prefix));
        }
    }

    private static bool IsCloudflareIp(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = ip.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        var ipNum = BitConverter.ToUInt32(bytes, 0);

        return CachedRanges.Any(range => (ipNum & range.Mask) == range.Network);
    }

    public async Task<List<IPAddress>> GetSortedAddressesAsync(string hostname, CancellationToken token)
    {
        var dnsRecords = await Dns.GetHostAddressesAsync(hostname, ForcedAddressFamily, token);

        if (dnsRecords.Length > 0 &&
            !string.IsNullOrEmpty(HijackCname) &&
            dnsRecords.All(IsCloudflareIp))
        {
            try
            {
                var cnameRecords = await Dns.GetHostAddressesAsync(HijackCname, ForcedAddressFamily, token);
                if (cnameRecords.Length > 0)
                {
                    dnsRecords[0] = cnameRecords[0];
                }
                else
                {
                    logger.LogWarning("CNAME {_hijackCname} resolved to empty IPs for {Hostname}", HijackCname,
                        hostname);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve CNAME {_hijackCname} for hijack of {Hostname}", HijackCname,
                    hostname);
            }
        }

        var groups = dnsRecords
            .GroupBy(a => a.AddressFamily)
            .Select(g => g.ToArray())
            .ToArray<IEnumerable<IPAddress>>();

#if DEBUG
        logger.LogTrace($"{hostname} resolved to {string.Join(", ", groups.SelectMany(g => g))}.");
#endif
        return Util.ZipperMerge(groups).ToList();
    }

    public void Dispose()
    {
    }

    private readonly struct CidrRange
    {
        public readonly uint Network;
        public readonly uint Mask;

        public CidrRange(IPAddress ip, int prefixLength)
        {
            var bytes = ip.GetAddressBytes();
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);

            var ipNum = BitConverter.ToUInt32(bytes, 0);
            var mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);

            Network = ipNum & mask;
            Mask = mask;
        }
    }
}