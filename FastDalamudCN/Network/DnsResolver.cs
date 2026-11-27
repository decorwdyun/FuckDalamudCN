using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using FastDalamudCN.Network.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network;

internal readonly record struct DnsResolutionResult(IPAddress[] Addresses, bool UsedDoh);

internal sealed class DnsResolver
{
    private const string PreferredHost = "cf.951886.xyz";
    private const int MaxTrackedHosts = 512;

    private static readonly TimeSpan DohPreferenceDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DohCooldownDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentResolutionWindow = TimeSpan.FromMinutes(2);

    private static readonly IPNetwork FakeIpRange = IPNetwork.Parse("198.18.0.0/15");
    private static readonly IPNetwork[] SuspiciousSystemRanges =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4")
    ];

    private static readonly string[] NonDohHostSuffixes =
    [
        ".local",
        ".lan",
        ".internal",
        ".localhost",
        ".arpa"
    ];

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

    private readonly ILogger<DnsResolver> _logger;
    private readonly Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> _lookup;
    private readonly IDohDnsClient? _dohClient;
    private readonly Func<DateTime> _utcNow;

    private readonly ConcurrentDictionary<string, DateTime> _dohPreferredHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _dohCooldownHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostResolutionRecord> _lastResolutions = new(StringComparer.OrdinalIgnoreCase);

    [ActivatorUtilitiesConstructor]
    public DnsResolver(ILogger<DnsResolver> logger, IDohDnsClient dohClient)
        : this(logger, System.Net.Dns.GetHostAddressesAsync, dohClient, null)
    {
    }

    public DnsResolver(ILogger<DnsResolver> logger)
        : this(logger, System.Net.Dns.GetHostAddressesAsync, null, null)
    {
    }

    public DnsResolver(
        ILogger<DnsResolver> logger,
        Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> lookup)
        : this(logger, lookup, null, null)
    {
    }

    internal DnsResolver(
        ILogger<DnsResolver> logger,
        Func<string, AddressFamily, CancellationToken, Task<IPAddress[]>> lookup,
        IDohDnsClient? dohClient,
        Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _lookup = lookup;
        _dohClient = dohClient;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task<IPAddress[]> GetSortedAddressesAsync(
        string hostname,
        bool preferCloudflare,
        CancellationToken token)
    {
        var result = await ResolveAsync(hostname, preferCloudflare, enableDohFallback: false, token)
            .ConfigureAwait(false);
        return result.Addresses;
    }

    public async Task<DnsResolutionResult> ResolveAsync(
        string hostname,
        bool preferCloudflare,
        bool enableDohFallback,
        CancellationToken token)
    {
        var normalizedHost = NormalizeHost(hostname);
        var canUseDoh = enableDohFallback && _dohClient is not null && IsDohEligibleHost(normalizedHost);

        if (canUseDoh && IsHostInWindow(_dohPreferredHosts, normalizedHost))
        {
            var dohAddresses = await TryDohLookupAsync(hostname, token).ConfigureAwait(false);
            if (dohAddresses.Length > 0)
            {
                RecordLastResolution(normalizedHost, dohAddresses, usedDoh: true);
                var sortedDoh = await ApplyCloudflarePreferredAsync(
                    hostname, dohAddresses, preferCloudflare, canUseDoh, token).ConfigureAwait(false);
                return new DnsResolutionResult(sortedDoh, UsedDoh: true);
            }

            _dohPreferredHosts.TryRemove(normalizedHost, out _);
        }

        IPAddress[] original = [];
        SocketException? systemException = null;
        try
        {
            original = await _lookup(hostname, AddressFamily.InterNetwork, token).ConfigureAwait(false);
        }
        catch (SocketException ex) when (!token.IsCancellationRequested)
        {
            systemException = ex;
        }

        var isSystemInvalid = systemException is not null ||
                              original.Length == 0 ||
                              original.Any(IsSuspiciousSystemAddress);

        if (isSystemInvalid && canUseDoh)
        {
            _logger.LogDebug("系统 DNS 解析 {Hostname} 异常或返回无效地址，尝试使用 DoH 兜底", hostname);
            var dohAddresses = await TryDohLookupAsync(hostname, token).ConfigureAwait(false);
            if (dohAddresses.Length > 0)
            {
                MarkHostDohPreferred(normalizedHost);
                RecordLastResolution(normalizedHost, dohAddresses, usedDoh: true);
                var sortedDoh = await ApplyCloudflarePreferredAsync(
                    hostname, dohAddresses, preferCloudflare, canUseDoh, token).ConfigureAwait(false);
                return new DnsResolutionResult(sortedDoh, UsedDoh: true);
            }
        }

        if (systemException is not null)
        {
            throw systemException;
        }

        if (IsDohEligibleHost(normalizedHost))
        {
            RecordLastResolution(normalizedHost, original, usedDoh: false);
        }

        var sorted = await ApplyCloudflarePreferredAsync(
            hostname, original, preferCloudflare, canUseDoh, token).ConfigureAwait(false);
        return new DnsResolutionResult(sorted, UsedDoh: false);
    }

    public async Task<IPAddress[]?> TryResolveFallbackViaDohAsync(
        string hostname,
        IReadOnlyCollection<IPAddress> failedAddresses,
        bool preferCloudflare,
        bool enableDohFallback,
        CancellationToken token)
    {
        var normalizedHost = NormalizeHost(hostname);
        if (!enableDohFallback ||
            _dohClient is null ||
            !IsDohEligibleHost(normalizedHost) ||
            failedAddresses.Any(IsFakeIpAddress) ||
            IsHostInWindow(_dohCooldownHosts, normalizedHost))
        {
            return null;
        }

        var dohAddresses = await TryDohLookupAsync(hostname, token).ConfigureAwait(false);
        if (dohAddresses.Length == 0)
        {
            SetDohCooldown(normalizedHost);
            return null;
        }

        var failedSet = failedAddresses.ToHashSet();
        if (!dohAddresses.Any(address => !failedSet.Contains(address)))
        {
            SetDohCooldown(normalizedHost);
            return null;
        }

        MarkHostDohPreferred(normalizedHost);
        RecordLastResolution(normalizedHost, dohAddresses, usedDoh: true);
        _logger.LogInformation("连接到 {Hostname} 失败且 DoH 返回新地址，切换至 DoH 结果重试", hostname);

        return await ApplyCloudflarePreferredAsync(
            hostname, dohAddresses, preferCloudflare, enableDohFallback: true, token).ConfigureAwait(false);
    }

    public async Task<bool> TryPromoteHostToDohAsync(
        string hostname,
        bool enableDohFallback,
        CancellationToken token)
    {
        var normalizedHost = NormalizeHost(hostname);
        if (!enableDohFallback || _dohClient is null || !IsDohEligibleHost(normalizedHost))
        {
            return false;
        }

        if (!_lastResolutions.TryGetValue(normalizedHost, out var lastResolution))
        {
            return false;
        }

        var now = _utcNow();
        if (now - lastResolution.ResolvedAtUtc > RecentResolutionWindow ||
            lastResolution.UsedDoh ||
            lastResolution.OriginalAddresses.Any(IsFakeIpAddress) ||
            IsHostInWindow(_dohCooldownHosts, normalizedHost))
        {
            return false;
        }

        var dohAddresses = await TryDohLookupAsync(hostname, token).ConfigureAwait(false);
        if (dohAddresses.Length == 0)
        {
            SetDohCooldown(normalizedHost);
            return false;
        }

        var originalSet = lastResolution.OriginalAddresses.ToHashSet();
        if (!dohAddresses.Any(address => !originalSet.Contains(address)))
        {
            SetDohCooldown(normalizedHost);
            return false;
        }

        MarkHostDohPreferred(normalizedHost);
        RecordLastResolution(normalizedHost, dohAddresses, usedDoh: true);
        _logger.LogInformation("检测到 {Hostname} 的系统 DNS 与 DoH 解析结果不一致，临时切换至 DoH", hostname);
        return true;
    }

    public void ClearCache()
    {
        _dohPreferredHosts.Clear();
        _dohCooldownHosts.Clear();
        _lastResolutions.Clear();
        _dohClient?.ClearCache();
    }

    internal static bool ShouldTriggerDohFallback(HttpRequestException exception)
    {
        if (exception.HttpRequestError is HttpRequestError.SecureConnectionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError)
        {
            return true;
        }

        for (Exception? current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException or IOException or SocketException)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDohEligibleHost(string hostname)
    {
        var normalized = NormalizeHost(hostname);
        if (normalized.Length == 0 ||
            IPAddress.TryParse(normalized, out _) ||
            normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            !normalized.Contains('.'))
        {
            return false;
        }

        foreach (var suffix in NonDohHostSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsFakeIpAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && FakeIpRange.Contains(address);

    internal static bool IsSuspiciousSystemAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        if (FakeIpRange.Contains(address))
        {
            return false;
        }

        return SuspiciousSystemRanges.Any(range => range.Contains(address));
    }

    private async Task<IPAddress[]> ApplyCloudflarePreferredAsync(
        string hostname,
        IPAddress[] original,
        bool preferCloudflare,
        bool enableDohFallback,
        CancellationToken token)
    {
        if (!preferCloudflare || original.Length == 0 || !original.All(IsCloudflareAddress))
        {
            return original;
        }

        var preferred = await ResolveCloudflarePreferredAsync(hostname, enableDohFallback, token).ConfigureAwait(false);
        return preferred.Length > 0
            ? preferred.Concat(original).Distinct().ToArray()
            : original;
    }

    private async Task<IPAddress[]> ResolveCloudflarePreferredAsync(
        string hostname,
        bool enableDohFallback,
        CancellationToken token)
    {
        var canUseDoh = enableDohFallback && _dohClient is not null;
        if (canUseDoh && IsHostInWindow(_dohPreferredHosts, PreferredHost))
        {
            var dohPreferred = ExtractCloudflarePreferred(await TryDohLookupAsync(PreferredHost, token).ConfigureAwait(false));
            if (dohPreferred.Length > 0)
            {
                return dohPreferred;
            }

            _dohPreferredHosts.TryRemove(PreferredHost, out _);
        }

        SocketException? systemException = null;
        try
        {
            var candidates = await _lookup(PreferredHost, AddressFamily.InterNetwork, token).ConfigureAwait(false);
            var preferred = ExtractCloudflarePreferred(candidates);
            if (preferred.Length > 0)
            {
                return preferred;
            }
        }
        catch (SocketException ex) when (!token.IsCancellationRequested)
        {
            systemException = ex;
        }

        if (canUseDoh)
        {
            var dohCandidates = await TryDohLookupAsync(PreferredHost, token).ConfigureAwait(false);
            var dohPreferred = ExtractCloudflarePreferred(dohCandidates);
            if (dohPreferred.Length > 0)
            {
                MarkHostDohPreferred(PreferredHost);
                return dohPreferred;
            }
        }

        if (systemException is not null)
        {
            _logger.LogWarning(systemException, "解析 {PreferredHost} 失败，使用 {Hostname} 的原始地址", PreferredHost, hostname);
        }
        else
        {
            _logger.LogWarning("{PreferredHost} 未返回 Cloudflare IPv4 地址，使用 {Hostname} 的原始地址", PreferredHost, hostname);
        }

        return [];
    }

    private async Task<IPAddress[]> TryDohLookupAsync(string hostname, CancellationToken token)
    {
        if (_dohClient is null)
        {
            return [];
        }

        try
        {
            var addresses = await _dohClient.ResolveAsync(hostname, AddressFamily.InterNetwork, token)
                .ConfigureAwait(false);
            return addresses
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IsSuspiciousSystemAddress(address))
                .Distinct()
                .ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or TimeoutException
                                       or FormatException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "DoH 解析 {Hostname} 失败", hostname);
            return [];
        }
    }

    private bool IsHostInWindow(ConcurrentDictionary<string, DateTime> map, string host)
    {
        if (!map.TryGetValue(host, out var expiry))
        {
            return false;
        }

        if (expiry > _utcNow())
        {
            return true;
        }

        map.TryRemove(new KeyValuePair<string, DateTime>(host, expiry));
        return false;
    }

    private void MarkHostDohPreferred(string host)
    {
        _dohPreferredHosts[host] = _utcNow().Add(DohPreferenceDuration);
        _dohCooldownHosts.TryRemove(host, out _);
    }

    private void SetDohCooldown(string host)
    {
        _dohCooldownHosts[host] = _utcNow().Add(DohCooldownDuration);
    }

    private void RecordLastResolution(string host, IPAddress[] addresses, bool usedDoh)
    {
        var now = _utcNow();
        if (_lastResolutions.Count >= MaxTrackedHosts)
        {
            foreach (var (trackedHost, record) in _lastResolutions)
            {
                if (now - record.ResolvedAtUtc > RecentResolutionWindow)
                {
                    _lastResolutions.TryRemove(new KeyValuePair<string, HostResolutionRecord>(trackedHost, record));
                }
            }
        }

        _lastResolutions[host] = new HostResolutionRecord(addresses, usedDoh, now);
    }

    private static IPAddress[] ExtractCloudflarePreferred(IPAddress[] candidates) =>
        candidates.Where(IsCloudflareAddress).Distinct().Take(3).ToArray();

    private static string NormalizeHost(string hostname) =>
        hostname.Trim().TrimEnd('.');

    private static bool IsCloudflareAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && CloudflareRanges.Any(range => range.Contains(address));

    private readonly record struct HostResolutionRecord(
        IPAddress[] OriginalAddresses,
        bool UsedDoh,
        DateTime ResolvedAtUtc);
}
