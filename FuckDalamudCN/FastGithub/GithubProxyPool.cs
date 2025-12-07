using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.FastGithub;

internal sealed class ProxyNode
{
    public string CheckUrl { get; init; } = string.Empty;
    public string Prefix { get; init; } = string.Empty;
    public HashSet<string> Tags { get; init; } = [];
}

internal sealed class GithubProxyPool : IDisposable
{
    private const string ExpectedSha256 = "1a17f2c74ee9a1c22cb0f04bee90902e0e0c5b1fa739fd3957fcee4f42365c27";
    private readonly Configuration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubProxyPool> _logger;

    private readonly List<ProxyNode> _nodes;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _checkSemaphore;

    public GithubProxyPool(ILogger<GithubProxyPool> logger, Configuration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _nodes = InitializeNodes();
        _checkSemaphore = new SemaphoreSlim(1, 1);
        
        _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true })
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        foreach (var node in _nodes) ProxyLatencies[node.Prefix] = long.MaxValue;

        Task.Run(() => CheckProxies(true));

        _timer = new Timer(async void (_) =>
        {
            try
            {
                await CheckProxies();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "代理池定时检查任务发生未处理异常");
            }
        }, null, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(60));
    }

    public int AcceleratedCount { get; private set; }

    public ConcurrentDictionary<string, long> ProxyLatencies { get; } = new();

    public void Dispose()
    {
        _timer.Dispose();
        _httpClient.Dispose();
    }

    public void IncreaseSuccessCount()
    {
        AcceleratedCount++;
    }

    public async Task CheckProxies(bool force = false)
    {
        if (!_configuration.EnableFastGithub && !force) return;
        
        if (!await _checkSemaphore.WaitAsync(0))
        {
            return;
        }
        
        CancellationTokenSource? cts = null;
        try
        {
            cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tasks = _nodes.Select(node => CheckSingleNodeAsync(node, 2, cts.Token)).ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            cts?.Dispose();
            _checkSemaphore.Release();
        }
    }

    private async Task CheckSingleNodeAsync(ProxyNode node, int retries, CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < retries; i++)
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await _httpClient.GetAsync(node.CheckUrl, cancellationToken);
                stopwatch.Stop();
                if (response is { IsSuccessStatusCode: true, Content.Headers.ContentLength: > 0 })
                {
                    var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    var sha256 = SHA256.HashData(contentBytes);
                    if (Convert.ToHexStringLower(sha256) == ExpectedSha256)
                    {
                        ProxyLatencies[node.Prefix] = stopwatch.ElapsedMilliseconds;
                        return;
                    }
                }
            }
            catch
            {
                /* ignored */
            }

        ProxyLatencies[node.Prefix] = long.MaxValue;
    }

    public string? GetFastestDomain(string originalUrl, IEnumerable<string>? excludePrefixes = null)
    {
        var excluded = excludePrefixes?.ToHashSet() ?? new HashSet<string>();

        var validNodes = _nodes
            .Where(node => !excluded.Contains(node.Prefix))
            .Select(node => new { Node = node, Latency = ProxyLatencies.GetValueOrDefault(node.Prefix, long.MaxValue) })
            .Where(x => x.Latency < 300000) // 基础存活判断
            .ToList();

        if (validNodes.Count == 0) return null;

        List<string> candidatePrefixes;
        var isZip = originalUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        if (isZip)
        {
            var zipCandidates = validNodes
                .Where(x => x.Node.Tags.Contains("short-cache"))
                .ToList();

            if (zipCandidates.Count > 0)
            {
                var min = zipCandidates.Min(x => x.Latency);
                candidatePrefixes = zipCandidates
                    .Where(x => x.Latency <= min + 120)
                    .Select(x => x.Node.Prefix)
                    .ToList();
            }
            else
            {
                var min = validNodes.Min(x => x.Latency);
                candidatePrefixes = validNodes
                    .Where(x => x.Latency <= min + 120)
                    .Select(x => x.Node.Prefix)
                    .ToList();
            }
        }
        else
        {
            var min = validNodes.Min(x => x.Latency);
            candidatePrefixes = validNodes
                .Where(x => x.Latency <= min + 120)
                .Select(x => x.Node.Prefix)
                .ToList();
        }

        if (candidatePrefixes.Count == 0) return null;

        var selected = candidatePrefixes[Random.Shared.Next(candidatePrefixes.Count)];

        return selected;
    }

    private static List<ProxyNode> InitializeNodes()
    {
        static string ExtractPrefix(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
            var portPart = uri.IsDefaultPort ? "" : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.Host}{portPart}/";
        }

        var sources = new[]
        {
            new
            {
                Url =
                    "https://gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url =
                    "https://hk.gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url =
                    "https://edgeone.gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url =
                    "https://ghfast.top/https://raw.githubusercontent.com/decorwdyun/DalamudPlugins/main/FuckDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url =
                    "https://gh.5050net.cn/https://raw.githubusercontent.com/decorwdyun/DalamudPlugins/main/FuckDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url =
                    "https://fb.xuolu.com/https://github.com/decorwdyun/DalamudPlugins/blob/main/FuckDalamudCN/random.bin",
                Tags = new[] { "short-cache" }
            }
        };

        return sources.Select(s => new ProxyNode
        {
            CheckUrl = s.Url,
            Prefix = ExtractPrefix(s.Url),
            Tags = s.Tags.ToHashSet()
        }).ToList();
    }
}