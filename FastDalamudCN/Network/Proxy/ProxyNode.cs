namespace FastDalamudCN.Network.Proxy;

internal sealed class ProxyNode
{
    public required string CheckUrl { get; init; }
    
    public required string Prefix { get; init; }
    
    public required HashSet<string> Tags { get; init; }
}
