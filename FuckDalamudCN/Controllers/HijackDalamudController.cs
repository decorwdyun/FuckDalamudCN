using System.Reflection;  
using System.Runtime.CompilerServices;  
using System.Runtime.Loader;  
using Dalamud.Plugin;  
using Microsoft.Extensions.Logging;  
  
namespace FuckDalamudCN.Controllers;  
  
public class HijackDalamudController(IDalamudPluginInterface pluginInterface, ILogger<HijackDalamudController> logger) : IDisposable  
{  
    private HijackAssemblyLoadContext? _patcherContext;  
    private WeakReference? _contextWeakReference;  
    private Action? _unpatchAction;  
  
    public async void Hijack(TimeSpan delay)  
    {  
        try  
        {  
            logger.LogDebug($"{pluginInterface.GetType().Assembly.Location}");  
            if (_patcherContext != null)  
            {  
                UnloadAndCleanup();  
            }  
  
            if (_contextWeakReference is { IsAlive: true })  
            {  
                logger.LogError("Failed to unload the previous patcher context. Aborting hijack.");  
                return;  
            }  
          
            await Task.Run(async () =>  
            {  
                try  
                {  
                    await Task.Delay(delay);  
                    LoadAndRunPatcher();  
                }  
                catch (Exception ex)  
                {  
                    logger.LogError(ex, "Failed to load and run the patcher after delay.");  
                }  
            });  
        }  
        catch (Exception e)  
        {  
            logger.LogError(e, "An error occurred while trying to hijack Dalamud.");  
            if (_patcherContext != null)  
            {  
                UnloadAndCleanup();  
            }  
        }  
    }  
  
    private void LoadAndRunPatcher()  
    {  
        var patcherDirectory = pluginInterface.AssemblyLocation.DirectoryName;  
        if (patcherDirectory == null)  
        {  
            logger.LogError("Could not determine patcher directory.");  
            return;  
        }  
  
        var patcherPath = Path.Combine(patcherDirectory, "Hijack/DalamudHijack.dll");  
        var harmonyPath = Path.Combine(patcherDirectory, "Hijack/0Harmony.dll");  
  
        if (!File.Exists(patcherPath) || !File.Exists(harmonyPath))  
        {  
            logger.LogError("Patcher or Harmony assembly not found.");  
            return;  
        }  
  
        try  
        {  
            var harmonyAssemblyName = new AssemblyName("0Harmony");  
            var isHarmonyLoaded = AppDomain.CurrentDomain.GetAssemblies()  
                .Any(a => a.GetName().Name == harmonyAssemblyName.Name);  
  
            if (!isHarmonyLoaded)  
            {  
                using var fs = new FileStream(harmonyPath, FileMode.Open, FileAccess.Read);  
                AssemblyLoadContext.Default.LoadFromStream(fs);  
                logger.LogInformation("Successfully loaded 0Harmony into the default context.");  
            }  
            else  
            {  
                logger.LogInformation("0Harmony is already loaded in the default context.");  
            }  
  
            _patcherContext = new HijackAssemblyLoadContext(patcherPath,pluginInterface.GetType().Assembly.Location, logger);  
            _contextWeakReference = new WeakReference(_patcherContext, trackResurrection: true);  
              
  
            var patcherAssemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(patcherPath));     
            var patcherAssembly = _patcherContext.LoadFromAssemblyName(patcherAssemblyName);     
  
            var patcherType = patcherAssembly.GetType("DalamudHijack.DalamudHijack");  
            if (patcherType == null)  
            {  
                logger.LogError("Could not find type 'DalamudHijack.DalamudHijack' in the assembly.");  
                return;  
            }  
  
            var initializeMethod = patcherType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);  
            if (initializeMethod == null)  
            {  
                logger.LogError("Could not find static method 'Initialize' in the patcher type.");  
                return;  
            }  
  
            var unpatchMethod = patcherType.GetMethod("Unpatch", BindingFlags.Public | BindingFlags.Static);  
            if (unpatchMethod == null)  
            {  
                logger.LogWarning("Could not find static method 'Unpatch' in the patcher type. Unpatching will be skipped on unload.");  
            }  
            else  
            {  
                _unpatchAction = () => unpatchMethod.Invoke(null, null);  
            }  
              
            initializeMethod.Invoke(null,[pluginInterface]);  
            logger.LogInformation("Patcher loaded and initialized successfully in a new context.");  
        }  
        catch (Exception ex)  
        {  
            logger.LogError(ex, "Failed to load or run the patcher assembly.");  
            if (_patcherContext != null)  
            {  
                UnloadAndCleanup();  
            }  
        }  
    }  
  
    [MethodImpl(MethodImplOptions.NoInlining)]  
    private void UnloadAndCleanup()  
    {  
        if (_patcherContext == null) return;  
  
        logger.LogInformation("Requesting unload of the patcher context.");  
        try  
        {  
            if (_unpatchAction != null)  
            {  
                logger.LogInformation("Executing unpatcher action.");  
                _unpatchAction.Invoke();  
            }  
            _patcherContext.Unload();  
        }  
        catch (Exception ex)  
        {  
            logger.LogError(ex, "Exception while unloading context.");  
        }  
        finally  
        {  
            _patcherContext = null;  
            _unpatchAction = null;  
        }  
      
        GC.Collect();  
        GC.WaitForPendingFinalizers();  
        logger.LogInformation("GC finished. Context alive: {IsAlive}", _contextWeakReference?.IsAlive ?? false);  
    }  
  
    public void Dispose()  
    {  
        UnloadAndCleanup();  
    }  
  
    private class HijackAssemblyLoadContext(string pluginPath, string dalamudDir, ILogger logger)  
        : AssemblyLoadContext(isCollectible: true)  
    {  
        private readonly AssemblyDependencyResolver _pluginResolver = new(pluginPath);  
        private readonly AssemblyDependencyResolver _dalamudResolver = new(dalamudDir);  
  
        protected override Assembly? Load(AssemblyName assemblyName)  
        {  
            if (assemblyName.Name == "Dalamud")  
            {  
                return typeof(IDalamudPluginInterface).Assembly;  
            }  
  
            if (assemblyName.Name == "0Harmony")  
            {  
                var existing = AppDomain.CurrentDomain.GetAssemblies()  
                    .FirstOrDefault(a => a.GetName().Name == "0Harmony");  
                logger.LogDebug("Found existing assembly {Assembly}", existing?.GetName().Name);  
                if (existing != null) return existing;  
                  
                try { return Default.LoadFromAssemblyName(assemblyName); } catch {}  
            }  
  
            string? assemblyPath = _pluginResolver.ResolveAssemblyToPath(assemblyName);  
            if (assemblyPath != null)  
            {  
                using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);  
                return LoadFromStream(fs);  
            }  
              
            assemblyPath = _dalamudResolver.ResolveAssemblyToPath(assemblyName);  
            if (assemblyPath != null)  
            {  
                logger.LogDebug("Resolved {Assembly} from Dalamud dir: {Path}", assemblyName.Name, assemblyPath);  
                using var fs = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);  
                return LoadFromStream(fs);  
            }  
  
            try  
            {  
                return Default.LoadFromAssemblyName(assemblyName);  
            }  
            catch  
            {  
                return null;  
            }  
        }  
    }  
}