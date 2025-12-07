using Dalamud.Plugin;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Utils;

public enum DalamudBranch
{
    Otter,
    Other,
}

public class DalamudBranchDetector
{
    public DalamudBranch Branch { get; }

    public DalamudBranchDetector(IDalamudPluginInterface pluginInterface, ILogger<DalamudBranchDetector> logger)
    {
        var dalamudAssembly = pluginInterface.GetType().Assembly;
        var eventTrackingType = dalamudAssembly.GetType("Dalamud.Support.EventTracking", false);

        Branch = eventTrackingType != null ? DalamudBranch.Otter : DalamudBranch.Other;
        logger.LogInformation($"DalamudBranchDetector: {Branch.ToString()}");
    }
}