using System.Net;
using System.Net.Sockets;
using FastDalamudCN.Network;
using FastDalamudCN.Network.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FastDalamudCN.Tests;

public class HappyEyeballsCallbackTests
{
    private const string Host = "repo.example";

    [Fact]
    public async Task DisposesLosingConnectedStreamsWhenMultipleAttemptsSucceed()
    {
        var anyDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var streamA = new TrackableStream(() => anyDisposed.TrySetResult());
        var streamB = new TrackableStream(() => anyDisposed.TrySetResult());

        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.1"), IPAddress.Parse("203.0.113.2") }));

        using var callback = new HappyEyeballsCallback(
            NullLogger<HappyEyeballsCallback>.Instance,
            new Configuration { EnableCloudflarePreferredIp = false, EnableDohFallback = false },
            resolver,
            async (ip, _, _) =>
            {
                if (Interlocked.Increment(ref startedCount) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await bothStarted.Task;
                return ip.Equals(IPAddress.Parse("203.0.113.1")) ? streamA : streamB;
            },
            connectionBackoffMs: 10,
            attemptTimeout: TimeSpan.FromSeconds(2));

        var selected = await callback.ConnectAsync(Host, 443, default);
        var other = ReferenceEquals(selected, streamA) ? streamB : streamA;

        Assert.True(ReferenceEquals(selected, streamA) || ReferenceEquals(selected, streamB));
        await anyDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(((TrackableStream)selected).Disposed);
        Assert.True(other.Disposed);
        selected.Dispose();
    }

    [Fact]
    public async Task Stage2ATcpTimeoutFallsBackToDohAndConnectsToNewAddress()
    {
        var poisonedIp = IPAddress.Parse("31.13.70.36");
        var cleanIp = IPAddress.Parse("203.0.113.42");
        var cleanStream = new TrackableStream();
        var attempted = new List<IPAddress>();

        var doh = new StubDohDnsClient((_, _, _) => Task.FromResult(new[] { cleanIp }));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(new[] { poisonedIp }),
            doh);

        using var callback = new HappyEyeballsCallback(
            NullLogger<HappyEyeballsCallback>.Instance,
            new Configuration { EnableCloudflarePreferredIp = false, EnableDohFallback = true },
            resolver,
            async (ip, _, ct) =>
            {
                attempted.Add(ip);
                if (ip.Equals(poisonedIp))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return cleanStream;
            },
            connectionBackoffMs: 10,
            attemptTimeout: TimeSpan.FromMilliseconds(60));

        var connected = await callback.ConnectAsync(Host, 443, default);

        Assert.Same(cleanStream, connected);
        Assert.Equal([poisonedIp, cleanIp], attempted);
        Assert.Equal(1, doh.CallCount);
        cleanStream.Dispose();
    }

    [Fact]
    public async Task Stage2ASkipsDohWhenFakeIpConnectionFails()
    {
        var fakeIp = IPAddress.Parse("198.18.0.5");
        var doh = new StubDohDnsClient((_, _, _) => throw new InvalidOperationException("DoH should not be called for Fake-IP"));
        var resolver = new DnsResolver(
            NullLogger<DnsResolver>.Instance,
            (_, _, _) => Task.FromResult(new[] { fakeIp }),
            doh);

        using var callback = new HappyEyeballsCallback(
            NullLogger<HappyEyeballsCallback>.Instance,
            new Configuration { EnableCloudflarePreferredIp = false, EnableDohFallback = true },
            resolver,
            (_, _, _) => Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)),
            connectionBackoffMs: 10,
            attemptTimeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<Exception>(async () => await callback.ConnectAsync(Host, 443, default));
        Assert.Equal(0, doh.CallCount);
    }

    private sealed class TrackableStream(Action? onDispose = null) : MemoryStream
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed)
            {
                Disposed = true;
                onDispose?.Invoke();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StubDohDnsClient(
        Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> handler) : IDohDnsClient
    {
        public int CallCount { get; private set; }

        public Task<IPAddress[]> ResolveAsync(string hostname, AddressFamily addressFamily, CancellationToken cancellationToken)
        {
            CallCount++;
            return handler(hostname, addressFamily, cancellationToken);
        }

        public void ClearCache()
        {
        }
    }
}
