using FastDalamudCN.Model;
namespace FastDalamudCN.Network;

public sealed class HijackedPluginRepositoryStore
{
    private sealed record Snapshot(
        IReadOnlyList<HijackedPluginRepositoryInfo> Items,
        Dictionary<string, HijackedPluginRepositoryInfo> Map);

    private volatile Snapshot _snapshot = new([], new Dictionary<string, HijackedPluginRepositoryInfo>());

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

        _snapshot = new Snapshot(items.Values.ToList(), items);
    }

    public void UpdateSnapshots(IEnumerable<HijackedPluginRepositoryInfo> snapshot)
    {
        MergeSnapshots(snapshot);
    }

    public void Clear()
    {
        _snapshot = new Snapshot([], new Dictionary<string, HijackedPluginRepositoryInfo>());
    }

    public bool ContainsPluginMasterUrl(string pluginMasterUrl)
    {
        return !string.IsNullOrEmpty(pluginMasterUrl) && _snapshot.Map.ContainsKey(pluginMasterUrl);
    }

    public bool TryGetRepositoryInfo(string pluginMasterUrl, out HijackedPluginRepositoryInfo? info)
    {
        if (!string.IsNullOrEmpty(pluginMasterUrl)) return _snapshot.Map.TryGetValue(pluginMasterUrl, out info);
        info = null;
        return false;
    }
}
