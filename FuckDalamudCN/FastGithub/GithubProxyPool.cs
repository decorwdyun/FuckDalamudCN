using System.Collections.Concurrent;
using System.Net;
using FuckDalamudCN;
using Microsoft.Extensions.Logging;


namespace FuckDalamudCN.FastGithub;

internal sealed class GithubProxyPool : IDisposable
{
    private readonly ILogger<GithubProxyPool> _logger;
    private readonly Configuration _configuration;

    private readonly List<string> _proxies =
    [
        "https://gh-proxy.com/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/icon.png?raw=true",
        "https://ghfast.top/https://raw.githubusercontent.com/decorwdyun/DalamudPlugins/main/FuckDalamudCN/icon.png",
        "https://github.moeyy.xyz/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/icon.png?raw=true",
        "https://fb.xuolu.com/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/icon.png?raw=true",
    ];

    private const string ExpectedSha256 = "2fc7fa19f3a3b1f2ce526611dac9b2668170d5ee367d60d42c5db0115ec0f679";

    private readonly HttpClient _httpClient;
    public readonly ConcurrentDictionary<string, long> ProxyResponseTimes;
    private readonly Timer _timer;

    public int AcceleratedCount { get; private set; }

    public GithubProxyPool(
        ILogger<GithubProxyPool> logger,
        Configuration configuration
    )
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = new HttpClient(new HttpClientHandler()
        {
            AllowAutoRedirect = false,
            UseProxy = true
        })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        ProxyResponseTimes = new ConcurrentDictionary<string, long>();

        foreach (var proxy in _proxies)
        {
            ProxyResponseTimes[GetPrefix(proxy)] = 9999999;
        }

        _timer = new Timer(_ => CheckProxies().Wait(), null, Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(30));
    }
    public Task CheckProxies()
    {
        if (!_configuration.EnableFastGithub) return Task.CompletedTask;

        var tasks = _proxies.Select(proxy => CheckDomainWithRetry(proxy, retries: 2)).ToList();
        return Task.WhenAll(tasks);
    }

    private async Task CheckDomainWithRetry(string url, int retries)
    {
        for (var i = 0; i < retries; i++)
        {
            var uri = new Uri(url);
            var prefix = GetPrefix(url);
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                var response = await _httpClient.GetAsync(uri);
                stopwatch.Stop();

                if (response is { IsSuccessStatusCode: true, Content.Headers.ContentLength: not null })
                {
                    var contentBytes = await response.Content.ReadAsByteArrayAsync();
                    var sha256 = System.Security.Cryptography.SHA256.HashData(contentBytes);
                    var hashString = Convert.ToHexStringLower(sha256);
                   
                    if (hashString != ExpectedSha256)
                    {
                        ProxyResponseTimes[prefix] = long.MaxValue;
                    }
                    else
                    {
                        ProxyResponseTimes[prefix] = stopwatch.ElapsedMilliseconds;
                    }
                }
                else
                {
                    ProxyResponseTimes[prefix] = long.MaxValue;
                }
            }
            catch
            {
                ProxyResponseTimes[prefix] = long.MaxValue;
            }
        }
    }

    public string? GetFastestDomain()
    {
        var ordered = ProxyResponseTimes
            .Where(kvp => kvp.Value < 300000)
            .OrderBy(kvp => kvp.Value)
            .ToList();

        if (ordered.Count == 0)
            return null;

        var minLatency = ordered[0].Value;
        var candidates = ordered
            .Where(kvp => kvp.Value <= minLatency + 120)
            .Select(kvp => kvp.Key)
            .ToList();

        if (candidates.Count == 0)
            return null;

        var random = new Random();
        return candidates[random.Next(candidates.Count)];
    }

    private string GetPrefix(string url)
    {
        var uri = new Uri(url);
        if (uri.Port != 80 && uri.Port != 443)
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}/";
        }

        return $"{uri.Scheme}://{uri.Host}/";
    }

    public void IncreaseSuccessCount()
    {
        AcceleratedCount++;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _httpClient.Dispose();
    }
}