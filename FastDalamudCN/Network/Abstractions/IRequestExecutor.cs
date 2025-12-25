namespace FastDalamudCN.Network.Abstractions;

internal interface IRequestExecutor
{
    Task<HttpResponseMessage> ExecuteAsync(
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendFunc,
        CancellationToken cancellationToken);
}
