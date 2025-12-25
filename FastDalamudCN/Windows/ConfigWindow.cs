using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Utility;
using FastDalamudCN.Network;
using FastDalamudCN.Network.Abstractions;
using FastDalamudCN.Network.Proxy;

namespace FastDalamudCN.Windows;

internal class ConfigWindow : Window
{
    private readonly Configuration _configuration;
    private readonly GithubProxyProvider _proxyProvider;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IHttpCacheService _httpCacheService;

    private DateTime _lastCanCheckTime = DateTime.MinValue;

    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        GithubProxyProvider proxyProvider,
        IHttpCacheService httpCacheService
    ) : base("FastDalamudCN - 配置")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _proxyProvider = proxyProvider;
        _httpCacheService = httpCacheService;
        Flags = ImGuiWindowFlags.AlwaysAutoResize;
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
        if (ImGui.Checkbox("开启卫月第三方仓库加速",
                ref enableFastGithub))
        {
            _configuration.EnableFastGithub = enableFastGithub;
            _httpCacheService.ClearCache();
            Save();
        }

        ImGuiComponents.HelpMarker(
            "只会反代加速 https://raw.githubusercontent.com 之类的 Github 相关的库链\n其他的比如 https://love.puni.sh 如果使用 CloudFlare则会使用优选IP直连");

        var enableMainRepoPluginLocalization = _configuration.EnableMainRepoPluginLocalization;
        if (ImGui.Checkbox("开启主库插件简介翻译",
                ref enableMainRepoPluginLocalization))
        {
            _configuration.EnableMainRepoPluginLocalization = enableMainRepoPluginLocalization;
            _httpCacheService.ClearCache();
            Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("只会翻译主库插件\n纯机翻，对效果别期待太多");

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
        ImGuiComponents.HelpMarker("Github 仓库缓存15分钟，其他仓库5分钟，缓存只会被使用4次\n开启后在短时间内重复打开插件管理器将无需等待（推荐开启）");

        var alwaysTrue = true;
        using (ImRaii.Disabled(alwaysTrue))
        {
            ImGui.Checkbox("优化 Dalamud 识别系统代理的行为", ref alwaysTrue);
        }

        ImGuiComponents.HelpMarker($"将忽略卫月本体的代理配置，转为固定使用系统代理{Environment.NewLine}"
                                   + "这将解决卫月本体偶尔无法识别系统代理导致无法加载插件列表的问题");


        ImGui.TextColored(ImGuiColors.HealerGreen, $"自本次启动以来共为你加速 {_proxyProvider.AcceleratedCount} 次");


        if (ImGui.TreeNodeEx("FAQS###FastDalamudCN-Notes"))
        {
            if (ImGui.TreeNode("新添加第三方裤链后第一次刷新很慢?"))
            {
                ImGui.TextWrapped("这是正常的，因为添加新裤链时，FDCN 还未来得及接管，这时候包括此前已接管的旧裤链也会失效");
                ImGui.TextWrapped("也就是说：这时候的速度基本等于你没开 FDCN 时本应有的速度");
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TreePop();
            }
    
            ImGui.Spacing();
    
            if (ImGui.TreeNode("插件 Github 加速和主库翻译的工作原理?"))
            {
                ImGui.TextWrapped("Github 加速工作原理为劫持卫月本体的网络请求，将访问 Github 服务的请求重定向到内置的反代服务器");
                ImGui.TextWrapped("主库翻译的工作原理也是劫持访问主库的网络请求，直接在本机处理服务器响应，将插件简介替换为插件内置的翻译");
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TreePop();
            }
    
            ImGui.Spacing();
            ImGui.TreePop();
        }
    
        ImGui.Separator();

        if (ImGui.CollapsingHeader("Debug###FastDalamudCN-Debug"))
        {
            ImGui.Text("以下代理中任意一个可用即可");
            ImGui.BeginDisabled(_lastCanCheckTime > DateTime.Now);

            var buttonText = "立即测速";
            if (_lastCanCheckTime > DateTime.Now)
                buttonText = $"休息一下吧, 还需等待 {(_lastCanCheckTime - DateTime.Now).Seconds} 秒";

            if (ImGui.Button(buttonText))
            {
#if !DEBUG
                _lastCanCheckTime = DateTime.Now.AddSeconds(20);
#endif
                _ = _proxyProvider.CheckProxiesAsync();
            }

            ImGui.EndDisabled();
            ImGui.Separator();
            foreach (var (domain, latency) in _proxyProvider.ProxyLatencies.OrderBy(kvp => kvp.Value))
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
        ImGui.TextColored(ImGuiColors.TankBlue, "https://github.com/decorwdyun/FuckDalamudCN");
        ImGui.NewLine();

        ImGui.Separator();

        if (ImGui.Button("打开插件主页")) Util.OpenLink("https://github.com/decorwdyun/FastDalamudCN");
        ImGui.SameLine();
        if (ImGui.Button("问题反馈")) Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins/issues/new");
        ImGui.SameLine();
        if (ImGui.Button("作者维护的更多插件")) Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins");
    }
}