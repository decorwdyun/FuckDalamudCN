namespace FastDalamudCN.Network.Abstractions;

internal interface IProxySelector
{
    Uri? BuildProxyUri(Uri originalUri, HashSet<string>? excludePrefixes = null);
    
    List<Uri> BuildMultipleProxyUris(Uri originalUri, int count, HashSet<string>? excludePrefixes = null);
}
