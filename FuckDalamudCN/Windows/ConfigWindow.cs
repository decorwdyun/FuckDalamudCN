using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Utility;
using FuckDalamudCN.Controllers;
using FuckDalamudCN.FastGithub;
using ImGuiNET;

namespace FuckDalamudCN.Windows;

internal class ConfigWindow: Window
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly HappyHttpClientHijack _happyHttpClientHijack;
    private readonly UnbanController _unbanController;

    internal readonly StyleModel? Style = StyleModel.Deserialize(
        "DS1H4sIAAAAAAAACqVYy27bRhT9FYOrBDAMksN5UDs/2ngRF0bkIml3lDSWGNEiS1GOEyNAkF233XcVoJsC7bYfVAP5jM7MnRcpBSjJbJQZzzlz3/cOH4MsmEQn4XEwCyaPwRuxCOXqp2BCT8KPx8E8mCC5sQgm6g9cHzOnwhOExbFbATwOlnp/pTlzDc5meuOtRmONTtQda32s0PA7fYq0Tm06u0Ttlno30bux2q32pJS7vwSTVG7U+ner7230eqcB93r/nf590NK/t6xYs0rVPxy8K8v0NtLbCMxZCiUfgxv+0FgYC1EaR0SDmVxgehz8LFcpJozEVNz3WgkhKCT2It9ms4IvLAfBYcrCRHMQyhIUJZpDLRBNPI7X+WZRvjtbOtGJ+keNBnapGKIQMxYjDBRCKkRoyBgVTOervFgMJpKyXJfVrvIZBHtEo9QwpEIxbJWJMMNpiGJPmbOyXvDa4uMooiShkca7pcIj36DCgWGUkpBZlukqE4YZoc33dXbHPW0i6U3CDIO8TfpDM8RAyIw0iRQWO5rL8p7Xnptjqq404Re3/IxwnCYRWCY8SRzL6bzJ713iokQFiyFxFlEsiYk/xUL0hTLu8qZoaRamNEnC2GgGS+snGrGQyKh2gQsEHWkETiWAo9FLTRPjJJWXdGnOy6LIqq1nnN5MV3yzO8vqYd4Cium8FnLMWiT9g8ayvKhlqRwtjKQZHDoHuAYHEHCdr/h8fZXV62+UK5bKLDQZSmSxiv3AmRa5SM2WbfpqYxlGqnK2a5rSdCJxIFZ3mZCz5xUaewEH+QjormcSiFNDglv1ikD5MoUXxHVcHXWwPBuaVkIMB1yA9kkueeaXzl7FCkF+IUszolgxSzLSP1NeZXXWlIN06lCMzSDDM1KlV3ybf+Av6rwarpPjGKmUIxqp1c2wQidCRZdP4BgRcypVcAo8I1pki+fHzW0537W6Uh/lUtWkOlQjLX1Rztf5Znld8/ucu9EmkcVJSKNpKEyhdu5s1S5qpyRN9t1d1bz3Wp6pXabZ+iFzXZTNy3zDt676m9nM1fzoEKDr3NT4D+ok2MFITCAucIfoMt825VLMQY6lVSOZNF5irEfM6jDHnkDt0V2byZZ/IWzkZ6FwasGhzLVmDhsaB6ynMHo2bepy43DIWFH+R8f3YeDLfLlyzw3ZB8BZNgj3cK9GPA7aJKeFuVk9oQSB+hVI9QsIop81U17wecP990SfMEVwNJZxWmfLi7qsbrJ6yZvevhcnbcj/kN1fCgMWLSNiMaKIq63jpQlMmXFRYY0qOODJJXKnS/Ztu9AO8iK/8yzDjBb6WlNDpPpX5SIrAPf/QOIRL5/Y4rkQTILzrKmq3Xyeb46eTfO7quBHp4u3u21zhJ4fPfv62+enP/7695/fn379++unT09f/jy6yh+eB+IbATxus8EJO+szyETm+0TfMQqAi/5DHABdJWYExamt7EIbSkxKpYm4zn9x3w597i+HVpvVgBwCZO7c0DJpO18YtFMPZ77uSE7Z86Q1wH8SZl1BVRx4uPXA2lxYnE1AUNB2SFDQtlyLdN3AYEzy+j7bDHyvlEPLhJvwKMhsP+iY+wG5bwrxaWtYlLhB2VoJcK0xJYUa78vq2nkfpwmgaKHiLx//A3sPPNh6FAAA"
        );
    internal bool StylePushed = false;
    
    private DateTime _lastCanCheckTime = DateTime.MinValue;
    
    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        HappyHttpClientHijack happyHttpClientHijack,
        UnbanController unbanController
    ) : base("FuckDalamudCN - 配置")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _happyHttpClientHijack = happyHttpClientHijack;
        _unbanController = unbanController;
        Flags = ImGuiWindowFlags.AlwaysAutoResize;
        // Size = new Vector2(500, 300);
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
        DrawAboutTab();
    }

    public override void PreDraw()
    {
        Style?.Push();
        StylePushed = true;
    }

    public override void PostDraw()
    {
        if (!StylePushed) return;

        Style?.Pop();
        StylePushed = false;
    }

    private void DrawUnbanTab()
    {
        using var tab = ImRaii.TabItem("Unban###FastGithubTab-FuckDalamudCN");
        if (!tab)
            return;
        var alwaysTrue = true;
        using (ImRaii.Disabled(alwaysTrue)){
            ImGui.Checkbox($"解除插件封锁（Unban）###{_unbanController.UnbannedRecord.Count}", ref alwaysTrue);
        }
        ImGuiComponents.HelpMarker($"某插件搜不到、插件被自动禁用、提示兼容性问题？{Environment.NewLine}"+
                                   $"这都有可能是因为 OtterCorp Dalamud 的插件封锁政策。{Environment.NewLine}"
                                   + "我们将帮你解除不合理的封锁政策");
        using (ImRaii.Disabled(alwaysTrue)){
            ImGui.Checkbox("阻止 Dalamud CN 收集用户隐私数据", ref alwaysTrue);
        }
        ImGuiComponents.HelpMarker($"OtterCorp Dalamud 会在每次登录时都收集大量数据上传到服务器{Environment.NewLine}" +
                                   $"包括但不限于机器码、用户账号ID、角色ID、已安装的插件列表，是否使用外置 exe 进行 Unban 等等{Environment.NewLine}" +
                                   "我们将帮你完全阻止这个数据的上传");
        if (_unbanController.UnbannedRecord.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "Unban 记录：");
            if (_unbanController.UnbannedRecord.Count < 5)
            {
                foreach (var (pluginName, note, time) in _unbanController.UnbannedRecord)
                {
                    ImGui.Text($"{time:yyyy-MM-dd HH:mm:ss} {pluginName} {note}");
                }
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "严重错误，请联系作者");
                foreach (var (pluginName, note, time) in _unbanController.UnbannedRecord.TakeLast(100))
                {
                    ImGui.TextColored(ImGuiColors.DalamudRed,$"{time:yyyy-MM-dd HH:mm:ss} {pluginName} {note}");
                }
           
            }
        }
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
            if (enableFastGithub) _happyHttpClientHijack.Enable();
        }

        if (_configuration.EnableFastGithub)
        {
            var alwaysTrue = true;
            using (ImRaii.Disabled(alwaysTrue)){
                ImGui.Checkbox("优化 Dalamud 识别系统代理的行为", ref alwaysTrue);
            }
            ImGuiComponents.HelpMarker($"将忽略卫月本体的代理配置，转为固定使用系统代理{Environment.NewLine}"
                                       + "这将解决卫月本体偶尔无法识别系统代理导致无法加载插件列表的问题");

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
                        ImGui.Text($"{domain}: {latency}毫秒（往返RTT）");  
                    }
                }

            }
        }
    }

    private void DrawAboutTab()
    {
        using var tab = ImRaii.TabItem("关于插件###FastGithubTab-About");
        if (!tab)
            return;

        ImGui.TextColored(ImGuiColors.DalamudRed, "本插件完全开源免费，从未委托任何人在任何渠道售卖。");
        ImGui.TextColored(ImGuiColors.DalamudRed, "如果你是付费购买的本插件，请立即退款并差评举报。");
        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.HealerGreen, "插件主页：");
        ImGui.TextColored(ImGuiColors.TankBlue, "https://github.com/decorwdyun/FuckDalamudCN");
        ImGui.NewLine();

        if (ImGui.Button("打开插件主页"))
        {
            Util.OpenLink("https://github.com/decorwdyun/FuckDalamudCN");
        }
        ImGui.SameLine();
        if (ImGui.Button("问题反馈"))
        {
            Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins/issues/new");
        }
        ImGui.SameLine();
        if (ImGui.Button("作者维护的更多插件"))
        {
            Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins");
        }

    }
    
}