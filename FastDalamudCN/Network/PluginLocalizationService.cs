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

    private PluginTranslationEntry? GetTranslation(string internalName)
    {
        return _translations?.TryGetValue(internalName, out var entry) == true ? entry : null;
    }

    public async Task TranslatePluginDescriptionsAsync(HttpResponseMessage response, Uri originalUri,
        CancellationToken ct)
    {
        if (!_configuration.EnableMainRepoPluginLocalization || !ShouldTranslateRepository(originalUri))
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
                    if (!string.IsNullOrEmpty(internalName))
                    {
                        var translation = GetTranslation(internalName);
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
        return _pluginRepositoryStore.TryGetRepositoryInfo(originalUri.ToString(), out var info) &&
               info is { IsThirdParty: false };
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
    public TranslationPair Punchline { get; set; } = new();
    public TranslationPair Description { get; set; } = new();
}

public class TranslationPair
{
    public string? Original { get; set; }
    public string? Translated { get; set; }
}
