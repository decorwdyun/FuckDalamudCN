using System.Net;

namespace FuckDalamudCN.Network;

internal class CachedHttpResponse
{
    public CachedHttpResponse(HttpResponseMessage response, byte[] content)
    {
        StatusCode = response.StatusCode;
        Content = content;
        Headers = response.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        ContentHeaders = response.Content.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private HttpStatusCode StatusCode { get; }
    private Dictionary<string, IEnumerable<string>> Headers { get; }
    private Dictionary<string, IEnumerable<string>> ContentHeaders { get; }
    private byte[] Content { get; }

    public HttpResponseMessage ToHttpResponseMessage(HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(StatusCode)
        {
            Content = new ByteArrayContent(Content),
            RequestMessage = request
        };

        foreach (var header in Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in ContentHeaders)
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return response;
    }
}