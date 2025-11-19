using System.Collections.Concurrent;
using System.Net;
using FuckDalamudCN;
using Microsoft.Extensions.Logging;


namespace FuckDalamudCN.FastGithub;

internal sealed class ProxyConfig
{
    public string Url { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
}

internal sealed class GithubProxyPool : IDisposable
{
    private readonly ILogger<GithubProxyPool> _logger;
    private readonly Configuration _configuration;

    private readonly List<ProxyConfig> _proxies =
    [
        new()
        {
            Url =
                "https://gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
            Tags = []
        },
        new()
        {
            Url =
                "https://hk.gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
            Tags = []
        },
        new()
        {
            Url =
                "https://edgeone.gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
            Tags = []
        },
        new()
        {
            Url =
                "https://ghfast.top/https://raw.githubusercontent.com/decorwdyun/DalamudPlugins/main/FuckDalamudCN/random.bin",
            Tags = []
        },
        new()
        {
            Url =
                "https://fb.xuolu.com/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
            Tags = ["short-cache"]
        },
    ];

    private const string ExpectedSha256 = "1a17f2c74ee9a1c22cb0f04bee90902e0e0c5b1fa739fd3957fcee4f42365c27";

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
            ProxyResponseTimes[GetPrefix(proxy.Url)] = 9999999;
        }

        CheckProxies(true);
        _timer = new Timer(async void (_) =>
        {
            try
            {
                await CheckProxies(false);
            }
            catch (Exception e)
            {
                // ignored
            }
        }, null, Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(30));
    }

    public Task CheckProxies(bool force = false)
    {
        if (!_configuration.EnableFastGithub && !force) return Task.CompletedTask;
        var tasks = _proxies.Select(proxy => CheckDomainWithRetry(proxy.Url, retries: 2)).ToList();
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
                        return;
                    }
                    else
                    {
                        ProxyResponseTimes[prefix] = stopwatch.ElapsedMilliseconds;
                    }
                }
                else
                {
                    _logger.LogWarning($"{url} {response.ReasonPhrase} ({response.StatusCode})");
                    ProxyResponseTimes[prefix] = long.MaxValue;
                }
            }
            catch (Exception e)
            {
                ProxyResponseTimes[prefix] = long.MaxValue;
            }
        }
    }

    public string? GetFastestDomain(string requestUrl)
    {
        if (requestUrl.EndsWith(".zip"))
        {
            var preferredProxies = _proxies
                .Where(p => p.Tags.Contains("short-cache"))
                .Select(p => GetPrefix(p.Url))
                .Where(prefix => ProxyResponseTimes.ContainsKey(prefix) && ProxyResponseTimes[prefix] < 300)
                .ToList();

            if (preferredProxies.Count > 0)
            {
                var selectedProxy = preferredProxies[Random.Shared.Next(preferredProxies.Count)];
                _logger.LogDebug("URL  {Url} is a zip file, using short-cache proxy {Proxy}", requestUrl,
                    selectedProxy);
                return selectedProxy;
            }
        }

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

        var defaultRandom = new Random();
        return candidates[defaultRandom.Next(candidates.Count)];
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