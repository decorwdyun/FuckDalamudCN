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

    public static HttpResponseMessage? HandleAnalyticsAndPrivacy(HttpRequestMessage request, ILogger logger)
    {
        var uriStr = request.RequestUri?.ToString();
        var host = request.RequestUri?.Host;

        if (uriStr == "https://api.bilibili.com/x/web-interface/zone")
            return BilibiliZoneMockResponseFactory.CreateZoneResponse();

        if (uriStr == "https://aonyx.ffxiv.wang/Dalamud/ToS?tosHash=true")
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request };

        if (host == "aonyx.ffxiv.wang" && uriStr != null && uriStr.EndsWith("/Dalamud/Analytics/Start"))
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };

        return null;
    }
}

public static class BilibiliZoneMockResponseFactory
{
    public static HttpResponseMessage CreateZoneResponse()
    {
        var dataObj = new JObject
        {
            ["addr"] = IpGenerator.GenerateRandomIp()
        };

        var rootObj = new JObject
        {
            ["data"] = dataObj
        };

        var json = rootObj.ToString(Formatting.None);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        return response;
    }
}