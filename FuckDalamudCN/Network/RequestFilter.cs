using System.Net;
using System.Text;
using FuckDalamudCN.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FuckDalamudCN.Network;

internal static class RequestFilter
{
    private static readonly string[] GithubPatterns =
    {
        "https://raw.githubusercontent.com",
        "https://github.com",
        "https://gist.github.com"
    };

    public static bool IsGithub(Uri? uri)
    {
        if (uri == null) return false;
        var uriStr = uri.ToString();
        return GithubPatterns.Any(p => uriStr.StartsWith(p));
    }
}