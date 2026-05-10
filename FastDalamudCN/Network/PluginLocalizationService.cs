using Dalamud.Plugin;
using FastDalamudCN.Controllers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FastDalamudCN.Network;

public class PluginLocalizationService : IDisposable
{
    private readonly string _assetPath;

    private readonly Configuration _configuration;
    private readonly HijackedPluginRepositoryStore _pluginRepositoryStore;
    private readonly ILogger<PluginLocalizationService> _logger;
    private Dictionary<string, PluginTranslationEntry>? _translations;

    public PluginLocalizationService(
        IDalamudPluginInterface dalamudPluginInterface,
        Configuration configuration,
        HijackedPluginRepositoryStore pluginRepositoryStore,
        ILogger<PluginLocalizationService> logger)
    {
        _configuration = configuration;
        _pluginRepositoryStore = pluginRepositoryStore;
        _logger = logger;
        _assetPath = Path.Combine(dalamudPluginInterface.AssemblyLocation.Directory!.FullName, "Assets",
            "translations.json");
        LoadTranslations();
    }

    private void LoadTranslations()
    {
        if (!File.Exists(_assetPath))
        {
            _logger.LogWarning($"插件本地化文件未找到");
            _translations = new Dictionary<string, PluginTranslationEntry>();
            return;
        }

        try
        {
            var json = File.ReadAllText(_assetPath);
            _translations = JsonConvert.DeserializeObject<Dictionary<string, PluginTranslationEntry>>(json) ??
                            new Dictionary<string, PluginTranslationEntry>();
        }
        catch (Exception)
        {
            _translations = new Dictionary<string, PluginTranslationEntry>();
        }
    }

    // 重新读取 translations.json
    
    public void RefreshTranslations()
    {
        var oldCount = _translations?.Count ?? 0;
        LoadTranslations();
        var newCount = _translations?.Count ?? 0;
        _logger.LogInformation($"汉化文件已刷新，条目数：{oldCount} → {newCount}");
    }

    private PluginTranslationEntry? GetTranslation(string internalName)
    {
        return _translations?.TryGetValue(internalName, out var entry) == true ? entry : null;
    }


    // 添加 ExDownloadLinkInstall 过滤黑名单，如果添加指定链接则排除翻译

    private PluginTranslationEntry? GetTranslation(string internalName, string? downloadLinkInstall)
    {
        if (_translations == null || !_translations.TryGetValue(internalName, out var entry))
            return null;

        // 没有黑名单，所有版本都翻译
        if (entry.ExDownloadLinkInstalls == null || entry.ExDownloadLinkInstalls.Count == 0)
            return entry;

        // 有黑名单，跳过当前下载链接
        if (!string.IsNullOrEmpty(downloadLinkInstall) &&
            entry.ExDownloadLinkInstalls.Any(d => string.Equals(d, downloadLinkInstall, StringComparison.OrdinalIgnoreCase)))
            return null;

        return entry;
    }

    public async Task TranslatePluginDescriptionsAsync(HttpResponseMessage response, Uri originalUri,
        CancellationToken ct)
    {
        if (!ShouldTranslateRepository(originalUri))
            return;

        var jsonString = await response.Content.ReadAsStringAsync(ct);

        try
        {
            var plugins = JsonConvert.DeserializeObject<List<JObject>>(jsonString);

            if (plugins != null)
            {
                foreach (var plugin in plugins)
                {
                    var internalName = plugin["InternalName"]?.ToString();
                    var downloadLinkInstall = plugin["DownloadLinkInstall"]?.ToString();
                    if (!string.IsNullOrEmpty(internalName))
                    {
                        var translation = GetTranslation(internalName, downloadLinkInstall);
                        if (translation != null)
                        {
                            plugin["Punchline"] =
                                $"{translation.Punchline.Translated}";
                            plugin["Description"] =
                                $"{translation.Punchline.Original?.Replace("\n", " ").Replace("\r", " ")} \n\n{translation.Description.Translated}\n\n{translation.Description.Original}\n\n注：这是由 FastDalamudCN 提供的机翻";
                        }
                    }
                }

                var modifiedJsonString = JsonConvert.SerializeObject(plugins);

                response.Content =
                    new StringContent(modifiedJsonString, System.Text.Encoding.UTF8, "application/json");
            }
        }
        catch (Exception)
        {
            // ignored
        }
    }

    private bool ShouldTranslateRepository(Uri originalUri)
    {
        if (_configuration.EnableMainRepoPluginLocalization &&
            _pluginRepositoryStore.TryGetRepositoryInfo(originalUri.ToString(), out var info) &&
            info is { IsThirdParty: false })
            return true;
        // 添加第三方仓库翻译开关
        if (_configuration.EnableThirdPartyPluginLocalization &&
            _pluginRepositoryStore.TryGetRepositoryInfo(originalUri.ToString(), out var tpInfo) &&
            tpInfo is { IsThirdParty: true })
            return true;
        return false;
    }

    public void Dispose()
    {
        _translations?.Clear();
        _translations = null;
    }
}

// ReSharper disable once ClassNeverInstantiated.Global
public class PluginTranslationEntry
{
    public List<string>? ExDownloadLinkInstalls { get; set; }
    public TranslationPair Punchline { get; set; } = new();
    public TranslationPair Description { get; set; } = new();
}

public class TranslationPair
{
    public string? Original { get; set; }
    public string? Translated { get; set; }
}
