using FastDalamudCN.Model;
namespace FastDalamudCN.Network;

public sealed class HijackedPluginRepositoryStore
{
    private sealed record Snapshot(IReadOnlyList<HijackedPluginRepositoryInfo> Items, HashSet<string> Urls);

    private volatile Snapshot _snapshot = new([], []);

    public IReadOnlyList<HijackedPluginRepositoryInfo> Snapshots => _snapshot.Items;

    public void MergeSnapshots(IEnumerable<HijackedPluginRepositoryInfo> snapshot)
    {
        var current = _snapshot;
        var items = new Dictionary<string, HijackedPluginRepositoryInfo>(current.Items.Count);

        foreach (var existing in current.Items)
        {
            if (!string.IsNullOrEmpty(existing.PluginMasterUrl))
            {
                items[existing.PluginMasterUrl] = existing;
            }
        }

        foreach (var info in snapshot)
        {
            if (string.IsNullOrEmpty(info.PluginMasterUrl))
            {
                continue;
            }

            items[info.PluginMasterUrl] = info;
        }

        _snapshot = new Snapshot(items.Values.ToList(), new HashSet<string>(items.Keys));
    }

    public void UpdateSnapshots(IEnumerable<HijackedPluginRepositoryInfo> snapshot)
    {
        MergeSnapshots(snapshot);
    }

    public void Clear()
    {
        _snapshot = new Snapshot([], []);
    }

    public bool ContainsPluginMasterUrl(string pluginMasterUrl)
    {
        return !string.IsNullOrEmpty(pluginMasterUrl) && _snapshot.Urls.Contains(pluginMasterUrl);
    }
}
