using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using FastDalamudCN.Network;
using FastDalamudCN.Network.Abstractions;
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

    [Fact]
    public async Task HealthySystemDnsDoesNotInvokeDoh()
    {
        var doh = new StubDohDnsClient((_, _, _) => throw new InvalidOperationException("DoH should not be called"));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(Addresses("203.0.113.10")),
            doh);

        var result = await resolver.ResolveAsync(Host, preferCloudflare: true, enableDohFallback: true, default);

        Assert.False(result.UsedDoh);
        Assert.Equal(Addresses("203.0.113.10"), result.Addresses);
        Assert.Equal(0, doh.CallCount);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.2")]
    [InlineData("240.0.0.1")]
    public async Task Stage1SuspiciousSystemAddressFallsBackToDohAndRemembersHost(string suspiciousIp)
    {
        var systemCalls = 0;
        var doh = new StubDohDnsClient((host, _, _) =>
        {
            Assert.Equal(Host, host);
            return Task.FromResult(Addresses("203.0.113.50"));
        });
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) =>
            {
                systemCalls++;
                return Task.FromResult(Addresses(suspiciousIp));
            },
            doh);

        var first = await resolver.ResolveAsync(Host, preferCloudflare: false, enableDohFallback: true, default);
        Assert.True(first.UsedDoh);
        Assert.Equal(Addresses("203.0.113.50"), first.Addresses);
        Assert.Equal(1, systemCalls);
        Assert.Equal(1, doh.CallCount);

        // Subsequent call uses DoH directly while host preference is active
        var second = await resolver.ResolveAsync(Host, preferCloudflare: false, enableDohFallback: true, default);
        Assert.True(second.UsedDoh);
        Assert.Equal(Addresses("203.0.113.50"), second.Addresses);
        Assert.Equal(1, systemCalls);
        Assert.Equal(2, doh.CallCount);
    }

    [Fact]
    public async Task Stage1SystemExceptionFallsBackToDohAndTriggersCloudflarePreferredIp()
    {
        var doh = new StubDohDnsClient((host, _, _) => host switch
        {
            Host => Task.FromResult(Addresses("104.16.0.1")),
            "cf.951886.xyz" => Task.FromResult(Addresses("172.64.0.10", "172.64.0.11")),
            _ => throw new InvalidOperationException($"Unexpected DoH host {host}")
        });
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (host, _, _) => host switch
            {
                Host => throw new SocketException((int)SocketError.HostNotFound),
                "cf.951886.xyz" => Task.FromResult(Addresses("203.0.113.99")), // non-CF, triggers DoH fallback for preferred host too
                _ => throw new InvalidOperationException($"Unexpected system host {host}")
            },
            doh);

        var result = await resolver.ResolveAsync(Host, preferCloudflare: true, enableDohFallback: true, default);

        Assert.True(result.UsedDoh);
        Assert.Equal(Addresses("172.64.0.10", "172.64.0.11", "104.16.0.1"), result.Addresses);
    }

    [Theory]
    [InlineData("repo.example", "198.18.0.1")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("nas.local", "127.0.0.1")]
    [InlineData("intranet", "127.0.0.1")]
    public async Task Stage1SkipsDohForFakeIpAndNonPublicHosts(string host, string returnedIp)
    {
        var doh = new StubDohDnsClient((_, _, _) => throw new InvalidOperationException("DoH should not be called"));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(Addresses(returnedIp)),
            doh);

        var result = await resolver.ResolveAsync(host, preferCloudflare: true, enableDohFallback: true, default);

        Assert.False(result.UsedDoh);
        Assert.Equal(Addresses(returnedIp), result.Addresses);
        Assert.Equal(0, doh.CallCount);
    }

    [Fact]
    public async Task Stage1SystemFailureRethrowsWhenDohAlsoFails()
    {
        var doh = new StubDohDnsClient((_, _, _) => throw new HttpRequestException("DoH offline"));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => throw new SocketException((int)SocketError.TryAgain),
            doh);

        await Assert.ThrowsAsync<SocketException>(() =>
            resolver.ResolveAsync(Host, preferCloudflare: false, enableDohFallback: true, default));
    }

    [Fact]
    public async Task Stage2TcpFallbackReturnsNewAddressesWhenDohDiffersAndAppliesCloudflarePreferred()
    {
        var doh = new StubDohDnsClient((host, _, _) => host switch
        {
            Host => Task.FromResult(Addresses("104.16.0.5")),
            "cf.951886.xyz" => Task.FromResult(Addresses("172.64.0.20")),
            _ => throw new InvalidOperationException()
        });
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (host, _, _) => host == "cf.951886.xyz"
                ? Task.FromResult(Addresses("172.64.0.20"))
                : Task.FromResult(Addresses("31.13.70.36")),
            doh);

        var fallback = await resolver.TryResolveFallbackViaDohAsync(
            Host,
            Addresses("31.13.70.36"),
            preferCloudflare: true,
            enableDohFallback: true,
            default);

        Assert.Equal(Addresses("172.64.0.20", "104.16.0.5"), fallback);
    }

    [Fact]
    public async Task Stage2TcpFallbackEntersCooldownWhenDohReturnsSameAddresses()
    {
        var doh = new StubDohDnsClient((_, _, _) => Task.FromResult(Addresses("203.0.113.10")));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(Addresses("203.0.113.10")),
            doh);

        var first = await resolver.TryResolveFallbackViaDohAsync(
            Host, Addresses("203.0.113.10"), preferCloudflare: false, enableDohFallback: true, default);
        Assert.Null(first);
        Assert.Equal(1, doh.CallCount);

        // Cooldown suppresses repeated DoH query
        var second = await resolver.TryResolveFallbackViaDohAsync(
            Host, Addresses("203.0.113.10"), preferCloudflare: false, enableDohFallback: true, default);
        Assert.Null(second);
        Assert.Equal(1, doh.CallCount);

        resolver.ClearCache();
        Assert.Equal(1, doh.ClearCacheCalls);
    }

    [Fact]
    public async Task Stage2TcpFallbackNeverTriggersForFakeIp()
    {
        var doh = new StubDohDnsClient((_, _, _) => throw new InvalidOperationException("DoH should not be called"));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(Addresses("198.18.0.2")),
            doh);

        var fallback = await resolver.TryResolveFallbackViaDohAsync(
            Host, Addresses("198.18.0.2"), preferCloudflare: true, enableDohFallback: true, default);

        Assert.Null(fallback);
        Assert.Equal(0, doh.CallCount);
    }

    [Fact]
    public async Task Stage2TlsPromoteRespectsSystemHistoryFakeIpProxyAndDiffCheck()
    {
        var dohResult = Addresses("203.0.113.88");
        var doh = new StubDohDnsClient((_, _, _) => Task.FromResult(dohResult));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (host, _, _) => host switch
            {
                Host => Task.FromResult(Addresses("31.13.70.36")),
                "fakeip.example" => Task.FromResult(Addresses("198.18.0.5")),
                "sameip.example" => Task.FromResult(Addresses("203.0.113.88")),
                _ => Task.FromResult(Addresses("203.0.113.1"))
            },
            doh);

        // 1. Host never resolved directly (e.g. request went through system HTTP proxy) -> skip DoH
        Assert.False(await resolver.TryPromoteHostToDohAsync("proxied.example", enableDohFallback: true, default));
        Assert.Equal(0, doh.CallCount);

        // 2. Host resolved to Fake-IP -> skip DoH
        await resolver.ResolveAsync("fakeip.example", preferCloudflare: false, enableDohFallback: true, default);
        Assert.False(await resolver.TryPromoteHostToDohAsync("fakeip.example", enableDohFallback: true, default));
        Assert.Equal(0, doh.CallCount);

        // 3. Host resolved to same IP as DoH -> returns false & cools down
        await resolver.ResolveAsync("sameip.example", preferCloudflare: false, enableDohFallback: true, default);
        Assert.False(await resolver.TryPromoteHostToDohAsync("sameip.example", enableDohFallback: true, default));
        Assert.Equal(1, doh.CallCount);
        Assert.False(await resolver.TryPromoteHostToDohAsync("sameip.example", enableDohFallback: true, default));
        Assert.Equal(1, doh.CallCount);

        // 4. Host resolved to poisoned public IP, DoH differs -> promotes to DoH & next ResolveAsync uses DoH
        await resolver.ResolveAsync(Host, preferCloudflare: false, enableDohFallback: true, default);
        Assert.True(await resolver.TryPromoteHostToDohAsync(Host, enableDohFallback: true, default));

        var promoted = await resolver.ResolveAsync(Host, preferCloudflare: false, enableDohFallback: true, default);
        Assert.True(promoted.UsedDoh);
        Assert.Equal(Addresses("203.0.113.88"), promoted.Addresses);

        // 5. Already using DoH -> second promote returns false without looping
        Assert.False(await resolver.TryPromoteHostToDohAsync(Host, enableDohFallback: true, default));
    }

    [Fact]
    public void ShouldTriggerDohFallbackMatchesTlsAndConnectionErrorsOnly()
    {
        Assert.True(DnsResolver.ShouldTriggerDohFallback(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "TLS failed", new AuthenticationException())));
        Assert.True(DnsResolver.ShouldTriggerDohFallback(
            new HttpRequestException("IO failure", new IOException("reset"))));
        Assert.False(DnsResolver.ShouldTriggerDohFallback(
            new HttpRequestException("Not Found", null, HttpStatusCode.NotFound)));
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

    private sealed class StubDohDnsClient(
        Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> handler) : IDohDnsClient
    {
        public int CallCount { get; private set; }
        public int ClearCacheCalls { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string hostname, AddressFamily addressFamily, CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(hostname, addressFamily, cancellationToken);
        }

        public void ClearCache() => ClearCacheCalls++;
    }
}
