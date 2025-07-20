// using System.Reflection;
// using DalamudHijack.External;
// using HarmonyLib;
//
// namespace DalamudHijack.Patch;
//
// public class CreatePluginInstancePatch : IPatch
// {
//     private MethodInfo? _patchedMethod;
//     private static List<string> IncompatiblePlugins = new()
//     {
//         "PFRadar",
//         "I-Ching-GL",
//         "NekoHackBox"
//     };
//     public void Apply(Harmony harmony)
//     {
//         var dalamudAssembly = AppDomain.CurrentDomain.GetAssemblies()
//             .FirstOrDefault(a => a.GetName().Name == "Dalamud");
//         if (dalamudAssembly == null) return;
//
//         var localPluginType = dalamudAssembly.GetType("Dalamud.Plugin.Internal.Types.LocalPlugin");
//         if (localPluginType == null) return;
//
//         _patchedMethod =
//             localPluginType.GetMethod("CreatePluginInstance", BindingFlags.NonPublic | BindingFlags.Static);
//         var postfix =
//             typeof(CreatePluginInstancePatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.Public);
//         if (_patchedMethod != null && postfix != null)
//         {
//             harmony.Patch(_patchedMethod, postfix: postfix);
//         }
//     }
//
//     public void Unpatch(Harmony harmony)
//     {
//         if (_patchedMethod != null)
//         {
//             harmony.Unpatch(_patchedMethod, HarmonyPatchType.Postfix, harmony.Id);
//         }
//     }
//
//     public static void Postfix(
//         object manifest,
//         object scope,
//         object type,
//         object dalamudInterface,
//         object __result
//     )
//     {
//         var prop = manifest.GetType().GetProperty("InternalName");
//         if (prop == null)
//         {
//             DalamudService.PluginLog.Error("Failed to get InternalName property from manifest.");
//             return;
//         }
//
//         var internalName = prop.GetValue(manifest) as string;
//         if (string.IsNullOrEmpty(internalName))
//         {
//             DalamudService.PluginLog.Error($"InternalName is null or empty {manifest}");
//             return;
//         }
//     
//         if (__result is Task task)
//         {
//             task.ContinueWith(t =>
//             {
//                 if (t.Exception != null)
//                 {
//                     var exText = t.Exception.ToString();
//                     var lines = exText.Split('\n');
//                     for (var i = 0; i < lines.Length - 1; i++)
//                     {
//                         if (
//                             lines[i].Contains("System.NullReferenceException: Object reference not set to an instance of an object.") &&
//                             lines[i + 1].TrimEnd().EndsWith("(Object)") &&
//                             lines[i + 2].TrimEnd().EndsWith("()") &&
//                             lines[i + 3].TrimEnd().EndsWith("cctor()")
//                         )
//                         {
//                             FuckDalamudCNIPC.ShowIncompatiblePluginWarningWindow(internalName);
//                             break;
//                         }
//                     }
//                 }
//             }, TaskScheduler.Default);
//         }
//     }
// }