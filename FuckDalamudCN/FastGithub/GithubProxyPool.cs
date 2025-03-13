using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;


namespace FuckDalamudCN.FastGithub
{
    public class GithubProxyPool : IDisposable
    {
        private readonly ILogger<GithubProxyPool> _logger;

        private readonly List<string> _proxies = new()
        {
            "https://gh-proxy.com/https://github.com/decorwdyun/DalamudPlugins/blob/7a52313df6c4ae0ae4ea049e92627b4ed61e6421/FuckDalamudCN/icon.png?raw=true",
            "https://ghfast.top/https://github.com/decorwdyun/DalamudPlugins/blob/7a52313df6c4ae0ae4ea049e92627b4ed61e6421/FuckDalamudCN/icon.png?raw=true",
        };
        
        private const long ExpectedContentLength = 24009;
        
        private readonly HttpClient _httpClient;
        public readonly ConcurrentDictionary<string, long> ProxyResponseTimes;
        private readonly Timer _timer;

        private int _handledRequestCount;
    
        public int AcceleratedCount => _handledRequestCount;
        
        public GithubProxyPool(
            ILogger<GithubProxyPool> logger,
            DynamicHttpWindowsProxy.DynamicHttpWindowsProxy dynamicHttpWindowsProxy
            )
        {
            _logger = logger;
            _httpClient = new HttpClient(new HttpClientHandler()
            {
                Proxy = dynamicHttpWindowsProxy
            })
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            ProxyResponseTimes = new ConcurrentDictionary<string, long>();
            
            foreach (var proxy in _proxies)
            {
                ProxyResponseTimes[GetPrefix(proxy)] = 9999999;
            }
            
            _timer = new Timer(state => CheckProxies().Wait(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60 * 3));
        }
        
        public Task CheckProxies()
        {
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
                        var contentLength = response.Content.Headers.ContentLength.Value;
                        if (contentLength != ExpectedContentLength)
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
            var fastestDomain = ProxyResponseTimes.OrderBy(kvp => kvp.Value).FirstOrDefault();
            if (fastestDomain.Value > 300000)
            {
                return null;
            }
            return fastestDomain.Key;
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
            _handledRequestCount++;
        }
        
        public void Dispose()
        {
            _timer.Dispose();
            _httpClient.Dispose();
        }
    }
}