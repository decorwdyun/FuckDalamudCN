using System.Net;
using FastDalamudCN.Network;
using Xunit;

namespace FastDalamudCN.Tests;

public class HttpRequestMessageExtensionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("https://proxy.example/manifest")]
    public void CloneForSendPreservesRequestSettings(string? targetUrl)
    {
        var originalUri = new Uri("https://repo.example/manifest");
        var targetUri = targetUrl is null ? null : new Uri(targetUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, originalUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent("manifest")
        };
        var option = new HttpRequestOptionsKey<int>("attempt");
        request.Options.Set(option, 1);
        request.Headers.Add("X-Source", "repository");

        using var clone = request.CloneForSend(targetUri);

        Assert.Equal(targetUri ?? originalUri, clone.RequestUri);
        Assert.Equal(originalUri, request.RequestUri);
        Assert.Equal(request.Method, clone.Method);
        Assert.Equal(request.Version, clone.Version);
        Assert.Equal(request.VersionPolicy, clone.VersionPolicy);
        Assert.Same(request.Content, clone.Content);
        Assert.Equal("repository", Assert.Single(clone.Headers.GetValues("X-Source")));
        Assert.True(clone.Options.TryGetValue(option, out var attempt));
        Assert.Equal(1, attempt);

        clone.Headers.Remove("X-Source");
        clone.Options.Set(option, 2);
        Assert.True(request.Headers.Contains("X-Source"));
        Assert.True(request.Options.TryGetValue(option, out var originalAttempt));
        Assert.Equal(1, originalAttempt);
    }
}
