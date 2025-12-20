using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Style;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Utility;
using FuckDalamudCN.Controllers;
using FuckDalamudCN.Network;
using FuckDalamudCN.Utils;

namespace FuckDalamudCN.Windows;

internal class ConfigWindow : Window
{
    private readonly Configuration _configuration;
    private readonly GithubProxyPool _githubProxyPool;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly UnbanController _unbanController;
    private readonly DalamudBranchDetector _dalamudBranchDetector;
    private readonly IHttpCacheService _httpCacheService;

    private DateTime _lastCanCheckTime = DateTime.MinValue;

    public ConfigWindow(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        GithubProxyPool githubProxyPool,
        UnbanController unbanController,
        IHttpCacheService httpCacheService
    ) : base("FuckDalamudCN - 配置")
    {
        _pluginInterface = pluginInterface;
        _configuration = configuration;
        _githubProxyPool = githubProxyPool;
        _unbanController = unbanController;
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
        using var tabBar = ImRaii.TabBar("FuckDalamudCNConfigTabs");
        if (!tabBar)
            return;
        
        DrawUnbanTab();
        DrawNetworkTab();
        DrawAboutTab();
    }

    private void DrawUnbanTab()
    {
        using var tab = ImRaii.TabItem("Unban###FastGithubTab-FuckDalamudCN");
        if (!tab)
            return;
        var alwaysTrue = true;
        using (ImRaii.Disabled(alwaysTrue))
        {
            ImGui.Checkbox($"解除插件封锁（Unban）###{_unbanController.UnbannedRecord.Count}", ref alwaysTrue);
        }

        ImGuiComponents.HelpMarker($"某插件搜不到、插件被自动禁用、提示兼容性问题？{Environment.NewLine}" +
                                   $"这都有可能是因为 OtterCorp Dalamud 的插件封锁政策。{Environment.NewLine}"
                                   + "我们将帮你解除不合理的封锁政策");
        using (ImRaii.Disabled(alwaysTrue))
        {
            ImGui.Checkbox("阻止 OtterCorp Dalamud 收集用户隐私数据", ref alwaysTrue);
        }

        ImGuiComponents.HelpMarker($"OtterCorp Dalamud 会在每次登录时都收集数据上传到服务器{Environment.NewLine}" +
                                   $"包括但不限于机器码、用户账号ID、角色ID、已安装的插件列表，是否使用外置 exe 进行 Unban 等等{Environment.NewLine}" +
                                   "我们将帮你完全阻止这个数据的上传");
        if (_unbanController.UnbannedRecord.Count > 0)
        {
            ImGui.TextColored(ImGuiColors.HealerGreen, "Unban 记录：");
            if (_unbanController.UnbannedRecord.Count < 5)
            {
                foreach (var (pluginName, note, time) in _unbanController.UnbannedRecord)
                    ImGui.Text($"{time:yyyy-MM-dd HH:mm:ss} {pluginName} {note}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, "严重错误，请联系作者");
                foreach (var (pluginName, note, time) in _unbanController.UnbannedRecord.TakeLast(100))
                    ImGui.TextColored(ImGuiColors.DalamudRed, $"{time:yyyy-MM-dd HH:mm:ss} {pluginName} {note}");
            }
        }
    }

    private void DrawNetworkTab()
    {
        using var tab = ImRaii.TabItem("网络相关##FastGithubTab-Network");
        if (!tab)
            return;
        ImGui.AlignTextToFramePadding();
        
        ImGui.TextColored(ImGuiColors.DalamudOrange, $"这个页面基本上是对卫月插件管理器的各种优化\n网络不好或者不想开梯子推荐都开启");

        
        var enableFastGithub = _configuration.EnableFastGithub;
        if (ImGui.Checkbox("开启卫月第三方 Github 插件仓库加速",
                ref enableFastGithub))
        {
            _configuration.EnableFastGithub = enableFastGithub;
            _httpCacheService.ClearCache();
            Save();
        }
        ImGuiComponents.HelpMarker("只会加速 https://raw.githubusercontent.com 之类的 Github 相关的库链\n其他的比如 https://love.puni.sh 则不会加速");
        
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
        if (ImGui.CollapsingHeader("Debug###FuckDalamudCN-Debug"))
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
        ImGui.TextColored(ImGuiColors.TankBlue, "https://github.com/decorwdyun/FuckDalamudCN");
        ImGui.NewLine();

        if (ImGui.Button("打开插件主页")) Util.OpenLink("https://github.com/decorwdyun/FuckDalamudCN");
        ImGui.SameLine();
        if (ImGui.Button("问题反馈")) Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins/issues/new");
        ImGui.SameLine();
        if (ImGui.Button("作者维护的更多插件")) Util.OpenLink("https://github.com/decorwdyun/DalamudPlugins");
    }
}