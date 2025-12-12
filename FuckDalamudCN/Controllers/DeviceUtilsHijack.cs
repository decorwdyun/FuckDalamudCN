using System.Reflection;
using Dalamud.Plugin;
using FuckDalamudCN.Utils;
using Microsoft.Extensions.Logging;

namespace FuckDalamudCN.Controllers;

internal class DeviceUtilsHijack
{
    private readonly ILogger<DeviceUtilsHijack> _logger;

    public DeviceUtilsHijack(
        IDalamudPluginInterface pluginInterface,
        DalamudBranchDetector dalamudBranchDetector,
        ILogger<DeviceUtilsHijack> logger
    )
    {
        _logger = logger;
        if (dalamudBranchDetector.Branch != DalamudBranch.Otter) return;
    
        var deviceUtilsType = pluginInterface.GetType().Assembly.GetTypes()
            .FirstOrDefault(t => t.FullName == "Dalamud.Utility.DeviceUtils");

        if (deviceUtilsType == null)
        {
            _logger.LogInformation("未找到机器码相关函数，已跳过随机机器码修改");
            return;
        }
    
        ReplaceDeviceId(deviceUtilsType);
    }

    private void ReplaceDeviceId(Type deviceUtilsType)
    { 
        var fieldInfo = deviceUtilsType.GetField("deviceId", BindingFlags.Static | BindingFlags.NonPublic);
        if (fieldInfo == null)
        {
            _logger.LogWarning("未能找到 deviceId 字段");
            return;
        }
        var spoofedLazy = new Lazy<string>(() => MachineCodeGenerator.Instance.MachineCode);

        fieldInfo.SetValue(null, spoofedLazy);

        _logger.LogInformation($"成功将 DeviceId 替换为: {MachineCodeGenerator.Instance.MachineCode}");
    }
}