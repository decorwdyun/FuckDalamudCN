using System.Reflection;
using Dalamud.Plugin;

namespace DalamudHijack.Reflection;

public class DalamudReflector
{
    internal static object GetService(IDalamudPluginInterface pluginInterface, string serviceFullName)
    {
        return pluginInterface.GetType().Assembly.GetType("Dalamud.Service`1", true)
            ?.MakeGenericType(pluginInterface.GetType().Assembly.GetType(serviceFullName, true)).
            GetMethod("Get").Invoke(null, BindingFlags.Default, null, [], null);
    }
}