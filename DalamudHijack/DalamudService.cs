using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudHijack;

public class DalamudService
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; }
    [PluginService] public static IPluginLog PluginLog { get; private set; }
}