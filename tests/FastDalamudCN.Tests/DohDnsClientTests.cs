using System.Net;
using System.Net.Sockets;
using FastDalamudCN.Network.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDalamudCN.Tests;

public class DohDnsClientTests
{
    private static readonly Uri PrimaryEndpoint = new("https://223.5.5.5/dns-query");
    private static readonly Uri SecondaryEndpoint = new("https://1.12.12.12/dns-query");

    [Fact]
    public async Task ResolvesOverRfc8484AndRespectsClampedTtlCache()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(PrimaryEndpoint, request.RequestUri);
            Assert.Equal("application/dns-message", request.Content?.Headers.ContentType?.MediaType);
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "application/dns-message");

            // Server returns TTL=5s, which should be clamped to 30s minimum
            var packet = DnsWireCodecTests.BuildResponsePacket(
                "repo.example",
                DnsWireCodec.NoError,
                [DnsWireCodecTests.BuildAddressRecord(12, 5, IPAddress.Parse("104.16.0.1"))]);
            return Task.FromResult(CreateDnsResponse(packet));
        });

        using var client = new DohDnsClient(
            NullLogger<DohDnsClient>.Instance,
            handler,
            [PrimaryEndpoint, SecondaryEndpoint],
            utcNow: () => now);

        var first = await client.ResolveAsync("repo.example", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("104.16.0.1")], first);
        Assert.Equal(1, calls);

        now = now.AddSeconds(29);
        var second = await client.ResolveAsync("REPO.EXAMPLE.", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("104.16.0.1")], second);
        Assert.Equal(1, calls);

        now = now.AddSeconds(2);
        var third = await client.ResolveAsync("repo.example", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("104.16.0.1")], third);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DeduplicatesConcurrentLookupsAndIsolatesCallerCancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);

            var packet = DnsWireCodecTests.BuildResponsePacket(
                "repo.example",
                DnsWireCodec.NoError,
                [DnsWireCodecTests.BuildAddressRecord(12, 60, IPAddress.Parse("104.16.0.9"))]);
            return CreateDnsResponse(packet);
        });

        using var client = new DohDnsClient(
            NullLogger<DohDnsClient>.Instance,
            handler,
            [PrimaryEndpoint],
            perEndpointTimeout: TimeSpan.FromSeconds(5),
            overallTimeout: TimeSpan.FromSeconds(5));

        using var caller1Cts = new CancellationTokenSource();
        var task1 = client.ResolveAsync("repo.example", AddressFamily.InterNetwork, caller1Cts.Token);
        await started.Task;

        var task2 = client.ResolveAsync("repo.example", AddressFamily.InterNetwork, default);
        caller1Cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);

        gate.SetResult();
        var result = await task2;

        Assert.Equal([IPAddress.Parse("104.16.0.9")], result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailsOverToSecondaryAndPrioritizesHealthyEndpoint()
    {
        var requestedEndpoints = new List<Uri>();
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            requestedEndpoints.Add(request.RequestUri!);
            if (request.RequestUri == PrimaryEndpoint)
            {
                var servFail = DnsWireCodecTests.BuildResponsePacket("a.example", DnsWireCodec.ServerFailure, []);
                return Task.FromResult(CreateDnsResponse(servFail));
            }

            var ok = DnsWireCodecTests.BuildResponsePacket(
                "a.example",
                DnsWireCodec.NoError,
                [DnsWireCodecTests.BuildAddressRecord(12, 60, IPAddress.Parse("1.2.3.4"))]);
            return Task.FromResult(CreateDnsResponse(ok));
        });

        using var client = new DohDnsClient(
            NullLogger<DohDnsClient>.Instance,
            handler,
            [PrimaryEndpoint, SecondaryEndpoint]);

        var first = await client.ResolveAsync("a.example", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("1.2.3.4")], first);
        Assert.Equal([PrimaryEndpoint, SecondaryEndpoint], requestedEndpoints);

        // Next uncached host should go straight to healthy secondary while primary is penalized
        requestedEndpoints.Clear();
        var second = await client.ResolveAsync("b.example", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("1.2.3.4")], second);
        Assert.Equal([SecondaryEndpoint], requestedEndpoints);

        // ClearCache also resets endpoint penalties
        client.ClearCache();
        requestedEndpoints.Clear();
        var third = await client.ResolveAsync("c.example", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("1.2.3.4")], third);
        Assert.Equal([PrimaryEndpoint, SecondaryEndpoint], requestedEndpoints);
    }

    [Fact]
    public async Task RejectsOversizedPayloadAndFailsOverToSecondary()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri == PrimaryEndpoint)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[70_000])
                });
            }

            var ok = DnsWireCodecTests.BuildResponsePacket(
                "repo.example",
                DnsWireCodec.NoError,
                [DnsWireCodecTests.BuildAddressRecord(12, 60, IPAddress.Parse("9.9.9.9"))]);
            return Task.FromResult(CreateDnsResponse(ok));
        });

        using var client = new DohDnsClient(
            NullLogger<DohDnsClient>.Instance,
            handler,
            [PrimaryEndpoint, SecondaryEndpoint]);

        var addresses = await client.ResolveAsync("repo.example", AddressFamily.InterNetwork, default);
        Assert.Equal([IPAddress.Parse("9.9.9.9")], addresses);
    }

    [Fact]
    public async Task ReturnsEmptyOnNxDomainWithoutFailingOver()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            var nx = DnsWireCodecTests.BuildResponsePacket("missing.example", DnsWireCodec.NameError, []);
            return Task.FromResult(CreateDnsResponse(nx));
        });

        using var client = new DohDnsClient(
            NullLogger<DohDnsClient>.Instance,
            handler,
            [PrimaryEndpoint, SecondaryEndpoint]);

        var addresses = await client.ResolveAsync("missing.example", AddressFamily.InterNetwork, default);
        Assert.Empty(addresses);
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage CreateDnsResponse(byte[] payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };
        response.Content.Headers.ContentType = new("application/dns-message");
        return response;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
