using Newtonsoft.Json;

namespace FastDalamudCN.Model;

public class PluginManifest
{
    [JsonProperty("Punchline")] public string Punchline { get; set; } = string.Empty;
    [JsonProperty("Description")] public string Description { get; set; } = string.Empty;
}