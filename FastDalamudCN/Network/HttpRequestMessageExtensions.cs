namespace FastDalamudCN.Network;

internal static class HttpRequestMessageExtensions
{
    public static HttpRequestMessage CloneForSend(this HttpRequestMessage request, Uri? targetUri = null)
    {
        var clone = new HttpRequestMessage(request.Method, targetUri ?? request.RequestUri)
        {
            Content = request.Content,
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return clone;
    }
}
