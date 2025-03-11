using System.Numerics;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using FuckDalamudCN.Controllers;
using FuckDalamudCN.FastGithub;
using ImGuiNET;

namespace FuckDalamudCN.Windows;

internal class ConfigWindow: Window
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly FastGithubController _fastGithubController;

    private DateTime _lastCanCheckTime = DateTime.MinValue;
    
    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        FastGithubController fastGithubController
    ) : base("FuckDalamudCN - 配置")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _fastGithubController = fastGithubController;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;
        Size = new Vector2(300, 300);
        SizeCondition = ImGuiCond.Always;
    }

    private void Save() => _pluginInterface.SavePluginConfig(_configuration);

    public override void Draw()
    {
        using var tabBar = ImRaii.TabBar("FuckDalamudCNConfigTabs");
        if (!tabBar)
            return;
        DrawUnbanTab();
        DrawFastGithubTab();
    }

    private void DrawUnbanTab()
    {
        using var tab = ImRaii.TabItem("FuckDalamudCN###FastGithubTab-FuckDalamudCN");
        if (!tab)
            return;
        var alwaysTrue = true;
        using (ImRaii.Disabled(alwaysTrue)){
            ImGui.Checkbox("解除插件封锁（Unban）", ref alwaysTrue);
        }
        ImGuiComponents.HelpMarker("如果你遇到过某插件搜不到、插件被自动禁用、提示兼容性问题都有可能是因为国服卫月的插件封锁政策。");
        using (ImRaii.Disabled(alwaysTrue)){
            ImGui.Checkbox("阻止 Dalamud CN 收集用户数据", ref alwaysTrue);
        }
        ImGuiComponents.HelpMarker("包括但不限于机器码、用户账号、角色ID、已安装的插件列表。");
    }

    private void DrawFastGithubTab()
    {
        using var tab = ImRaii.TabItem("Github 加速###FastGithubTab-FastGithub");
        if (!tab)
            return;
        
        var enableFastGithub = _configuration.EnableFastGithub;
        if (ImGui.Checkbox("开启 Dalamud 第三方 Github 仓库加速",
                ref enableFastGithub))
        {
            _configuration.EnableFastGithub = enableFastGithub;
            Save();
            if (enableFastGithub) _fastGithubController.Enable();
        }

        if (_configuration.EnableFastGithub)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, $"自本次启动以来共为你加速 {_githubProxyPool.AcceleratedCount} 次");
            if (ImGui.CollapsingHeader("Debug###FuckDalamudCN-Debug"))
            {
                ImGui.BeginDisabled(_lastCanCheckTime > DateTime.Now);

                var buttonText = "立即测速";
                if (_lastCanCheckTime > DateTime.Now)
                {
                    buttonText = $"休息一下吧, 还需等待 {(_lastCanCheckTime - DateTime.Now).Seconds} 秒";
                }

                if (ImGui.Button(buttonText))
                {
                    _lastCanCheckTime = DateTime.Now.AddSeconds(20);
                    _githubProxyPool.CheckProxies();
                }

                ImGui.EndDisabled();
                ImGui.Separator();
                foreach (var (domain, latency) in _githubProxyPool.ProxyResponseTimes.OrderBy(kvp => kvp.Value))
                {
                    if (latency > 1000 * 30)
                    {
                        ImGui.TextColored(ImGuiColors.DalamudRed,$"{domain}: 完全无法访问");
                    }
                    else
                    {
                        ImGui.Text($"{domain}: {latency}毫秒");  
                    }
                }
            }
        }
    }
}