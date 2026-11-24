using System.Text.Json;

namespace FastDalamudCN.Network.Proxy;

internal static class ProxyNodeConfiguration
{
    public const string ProbeFileUrl =
        "https://github.com/decorwdyun/DalamudPlugins/blob/main/FastDalamudCN/random.bin";

    public const string RemoteNodesUrl =
        "https://raw.githubusercontent.com/decorwdyun/FuckDalamudCN/master/proxy-nodes.json";

    public static string[] CreateBootstrapNodes() =>
    [
        "https://gh-proxy.org/",
        "https://v4.gh-proxy.org/",
        "https://fb.xuolu.com/"
    ];

    public static async Task<string[]> ReadRemoteNodesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var prefixes = await JsonSerializer.DeserializeAsync<string?[]>(stream, cancellationToken: cancellationToken);

        return (prefixes ?? [])
            .Select(NormalizePrefix)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizePrefix(string? prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority) + "/";
    }
}
