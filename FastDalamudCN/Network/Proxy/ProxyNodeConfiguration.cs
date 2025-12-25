namespace FastDalamudCN.Network.Proxy;

internal static class ProxyNodeConfiguration
{
    public static List<ProxyNode> CreateDefaultNodes()
    {
        var sources = new[]
        {
            new
            {
                Url = "https://gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FastDalamudCN/random.bin",
                Tags = new[] { "cloudflare" }
            },
            new
            {
                Url = "https://hk.gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FastDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url = "https://edgeone.gh-proxy.org/https://github.com/decorwdyun/DalamudPlugins/blob/main/FastDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url = "https://ghfast.top/https://raw.githubusercontent.com/decorwdyun/DalamudPlugins/main/FastDalamudCN/random.bin",
                Tags = new[] { "standard" }
            },
            new
            {
                Url = "https://fb.xuolu.com/https://github.com/decorwdyun/DalamudPlugins/blob/main/FastDalamudCN/random.bin",
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

    private static string ExtractPrefix(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var portPart = uri.IsDefaultPort ? "" : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{portPart}/";
    }
}
