using System.Text.RegularExpressions;
using FastDalamudCN.Network.Abstractions;
using Microsoft.Extensions.Logging;

namespace FastDalamudCN.Network.Proxy;

internal sealed partial class GithubProxySelector(
    Configuration configuration,
    GithubProxyProvider proxyProvider,
    ILogger<GithubProxySelector> logger)
    : IProxySelector
{
    public Uri? BuildProxyUri(Uri originalUri, HashSet<string>? excludePrefixes = null)
    {
        if (!configuration.EnableFastGithub || !RequestFilter.IsGithub(originalUri))
        {
            return null;
        }

        var normalizedUri = NormalizeGithubRawUri(originalUri);
        var fastestPrefix = proxyProvider.SelectFastestProxy(originalUri.ToString(), excludePrefixes);

        if (fastestPrefix == null)
        {
            return null;
        }

        excludePrefixes?.Add(fastestPrefix);

        var proxyUri = new Uri(fastestPrefix + normalizedUri);
        logger.LogTrace("选择代理: {OriginalUri} -> {ProxyUri}", originalUri, proxyUri);

        return proxyUri;
    }

    public List<Uri> BuildMultipleProxyUris(Uri originalUri, int count, HashSet<string>? excludePrefixes = null)
    {
        if (!configuration.EnableFastGithub || !RequestFilter.IsGithub(originalUri))
        {
            return [];
        }

        var normalizedUri = NormalizeGithubRawUri(originalUri);
        var proxyPrefixes = proxyProvider.SelectMultipleFastProxies(originalUri.ToString(), count, excludePrefixes);

        var proxyUris = new List<Uri>();
        foreach (var prefix in proxyPrefixes)
        {
            excludePrefixes?.Add(prefix);
            var proxyUri = new Uri(prefix + normalizedUri);
            proxyUris.Add(proxyUri);
        }

        return proxyUris;
    }
    
    private static Uri NormalizeGithubRawUri(Uri uri)
    {
        if (uri.Host != "raw.githubusercontent.com")
        {
            return uri;
        }

        var path = uri.AbsolutePath;
        var regex = RefHeadRegex();
        var match = regex.Match(path);

        if (match.Success)
        {
            var normalizedPath = $"/{match.Groups[1].Value}/{match.Groups[2].Value}/{match.Groups[3].Value}{match.Groups[4].Value}";
            var builder = new UriBuilder(uri)
            {
                Path = normalizedPath,
                Query = uri.Query
            };
            return builder.Uri;
        }

        return uri;
    }

    [GeneratedRegex(@"^/([^/]+)/([^/]+)/refs/heads/([^/]+)(.*)$")]
    private static partial Regex RefHeadRegex();
}
