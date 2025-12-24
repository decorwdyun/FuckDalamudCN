using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Utility;
using FastDalamudCN.Network;

namespace FastDalamudCN.Windows;

internal class ConfigWindow : Window
{
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IHttpCacheService _httpCacheService;

    private DateTime _lastCanCheckTime = DateTime.MinValue;

    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        IHttpCacheService httpCacheService
    ) : base("FastDalamudCN - 配置")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _httpCacheService = httpCacheService;
        Flags = ImGuiWindowFlags.AlwaysAutoResize;
        // Size = new Vector2(500, 300);
        SizeCondition = ImGuiCond.Always;
    }

    private void Save()
    {
        _pluginInterface.SavePluginConfig(_configuration);
    }

    public override void Draw()
    {
        using var tabBar = ImRaii.TabBar("FastDalamudCNConfigTabs");
        if (!tabBar)
            return;

        DrawNetworkTab();
        DrawUnbanTab();
        DrawAboutTab();
    }

    private void DrawUnbanTab()
    {
        using var tab = ImRaii.TabItem("原Unban##FastDalamudCN-Unban");
        if (!tab)
            return;
        ImGui.AlignTextToFramePadding();

        ImGui.Text("OtterCorp 卫月已全面移除插件封锁相关代码、所以不再需要 Unban 了");
        ImGui.Text($"如果你不需要本插件的其他功能的话，可以直接卸载！");
    }

    private void DrawNetworkTab()
    {
        using var tab = ImRaii.TabItem("网络相关##FastGithubTab-Network");
        if (!tab)
            return;
        ImGui.AlignTextToFramePadding();

        var enableFastGithub = _configuration.EnableFastGithub;
        if (ImGui.Checkbox("开启卫月第三方 Github 插件仓库加速",
                ref enableFastGithub))
        {
            _configuration.EnableFastGithub = enableFastGithub;
            _httpCacheService.ClearCache();
            Save();
        }

        ImGuiComponents.HelpMarker(
            "只会加速 https://raw.githubusercontent.com 之类的 Github 相关的库链\n其他的比如 https://love.puni.sh 则不会加速");

        var enableMainRepoPluginLocalization = _configuration.EnableMainRepoPluginLocalization;
        if (ImGui.Checkbox("开启主库插件简介翻译",
                ref enableMainRepoPluginLocalization))
        {
            _configuration.EnableMainRepoPluginLocalization = enableMainRepoPluginLocalization;
            _httpCacheService.ClearCache();
            Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("只会翻译主库插件\n纯机翻，对效果别期待太多（聊胜于无）");

        var enablePluginManifestCache = _configuration.EnablePluginManifestCache;
        if (ImGui.Checkbox("开启缓存",
                ref enablePluginManifestCache))
        {
            _configuration.EnablePluginManifestCache = enablePluginManifestCache;
            _httpCacheService.ClearCache();
            Save();
        }

        if (enablePluginManifestCache)
        {
            ImGui.SameLine();
            if (ImGui.Button("清除缓存"))
            {
                _httpCacheService.ClearCache();
            }
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Github 仓库缓存15分钟，其他仓库5分钟\n开启后在短时间内重复打开插件管理器将无需等待（推荐开启）");

        var alwaysTrue = true;
        using (ImRaii.Disabled(alwaysTrue))
        {
            ImGui.Checkbox("优化 Dalamud 识别系统代理的行为", ref alwaysTrue);
        }

        ImGuiComponents.HelpMarker($"将忽略卫月本体的代理配置，转为固定使用系统代理{Environment.NewLine}"
                                   + "这将解决卫月本体偶尔无法识别系统代理导致无法加载插件列表的问题");

        ImGui.TextColored(ImGuiColors.HealerGreen, $"自本次启动以来共为你加速 {_githubProxyPool.AcceleratedCount} 次");
        if (ImGui.CollapsingHeader("Debug###FastDalamudCN-Debug"))
        {
            ImGui.Text("以下代理中任意一个可用即可");
            ImGui.BeginDisabled(_lastCanCheckTime > DateTime.Now);

            var buttonText = "立即测速";
            if (_lastCanCheckTime > DateTime.Now)
                buttonText = $"休息一下吧, 还需等待 {(_lastCanCheckTime - DateTime.Now).Seconds} 秒";

            if (ImGui.Button(buttonText))
            {
                _lastCanCheckTime = DateTime.Now.AddSeconds(20);
                _githubProxyPool.CheckProxies();
            }

            ImGui.EndDisabled();
            ImGui.Separator();
            foreach (var (domain, latency) in _githubProxyPool.ProxyLatencies.OrderBy(kvp => kvp.Value))
                if (latency > 1000 * 30)
                    ImGui.TextColored(ImGuiColors.DalamudRed, $"{domain}: 完全无法访问");
                else
                    ImGui.Text($"{domain}: {latency}毫秒（往返RTT）");
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
        ImGui.TextColored(ImGuiColors.TankBlue, "https://github.com/decorwdyun/FastDalamudCN");
        ImGui.NewLine();

        ImGui.Separator();
        if (ImGui.CollapsingHeader("插件 Github 加速和主库翻译的工作原理?"))
        {
            ImGui.PushTextWrapPos();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("两个功能的工作原理都是劫持 Dalamud 本体的网络请求，重定向至本插件内置的反代服务器");
            ImGui.NewLine();
        }

        if (ImGui.Button("打开插件主页")) Util.OpenLink("https://github.com/decorwdyun/FastDalamudCN");
        ImGui.SameLine();
        if (ImGui.Button("问题反馈")) Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins/issues/new");
        ImGui.SameLine();
        if (ImGui.Button("作者维护的更多插件")) Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins");
    }
}