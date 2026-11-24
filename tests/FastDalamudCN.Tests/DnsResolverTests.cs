using System.Net;
using System.Net.Sockets;
using FastDalamudCN.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDalamudCN.Tests;

public class DnsResolverTests
{
    private const string Host = "repo.example";

    [Fact]
    public async Task PrefersThreeDistinctCloudflareAddressesAndKeepsOriginalFallbacks()
    {
        var original = Addresses("104.16.0.1", "104.16.0.2");
        var candidates = Addresses("203.0.113.1", "172.64.0.1", "172.64.0.1", "104.16.0.2", "172.71.255.255", "104.16.0.3");
        var resolver = CreateResolver(original, _ => Task.FromResult(candidates));

        var result = await resolver.GetSortedAddressesAsync(Host, true, default);

        Assert.Equal(Addresses("172.64.0.1", "104.16.0.2", "172.71.255.255", "104.16.0.1"), result);
    }

    [Theory]
    [InlineData(false, "104.16.0.1")]
    [InlineData(true)]
    [InlineData(true, "172.72.0.0")]
    [InlineData(true, "172.71.255.255", "172.72.0.0")]
    public async Task SkipsPreferredLookupWhenDisabledOrOriginalsAreIneligible(bool enabled, params string[] records)
    {
        var original = Addresses(records);
        var resolver = CreateResolver(original, _ => throw new InvalidOperationException("Unexpected preferred lookup"));

        Assert.Equal(original, await resolver.GetSortedAddressesAsync(Host, enabled, default));
    }

    [Theory]
    [InlineData()]
    [InlineData("203.0.113.1", "::ffff:104.16.0.1")]
    public async Task NoValidPreferredAddressesUsesOriginals(params string[] records)
    {
        var original = Addresses("104.16.0.1");
        var resolver = CreateResolver(original, _ => Task.FromResult(Addresses(records)));

        Assert.Equal(original, await resolver.GetSortedAddressesAsync(Host, true, default));
    }

    [Fact]
    public async Task PreferredDnsFailureUsesOriginals()
    {
        var original = Addresses("104.16.0.1", "104.16.0.2");
        var resolver = CreateResolver(original, _ => throw new SocketException((int)SocketError.HostNotFound));

        Assert.Equal(original, await resolver.GetSortedAddressesAsync(Host, true, default));
    }

    [Fact]
    public async Task CancellationDuringPreferredLookupPropagates()
    {
        using var cts = new CancellationTokenSource();
        var resolver = CreateResolver(Addresses("104.16.0.1"), token =>
        {
            cts.Cancel();
            return Task.FromCanceled<IPAddress[]>(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.GetSortedAddressesAsync(Host, true, cts.Token));
    }

    private static IPAddress[] Addresses(params string[] records) => records.Select(IPAddress.Parse).ToArray();

    private static DnsResolver CreateResolver(IPAddress[] original, Func<CancellationToken, Task<IPAddress[]>> preferredLookup)
    {
        return new DnsResolver(NullLogger<DnsResolver>.Instance, (host, family, token) =>
        {
            Assert.Equal(AddressFamily.InterNetwork, family);
            if (host == Host)
                return Task.FromResult(original);

            Assert.Equal("cf.951886.xyz", host);
            return preferredLookup(token);
        });
    }
}
