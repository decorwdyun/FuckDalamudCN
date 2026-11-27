using Newtonsoft.Json;
using Xunit;

namespace FastDalamudCN.Tests;

public class ConfigurationTests
{
    [Fact]
    public void CloudflarePreferredIpAndDohFallbackDefaultToEnabledForNewAndExistingConfigs()
    {
        var fresh = new Configuration();
        Assert.True(fresh.EnableCloudflarePreferredIp);
        Assert.True(fresh.EnableDohFallback);

        var existing = JsonConvert.DeserializeObject<Configuration>("{\"Version\":1,\"EnableFastGithub\":false}")!;

        Assert.True(existing.EnableCloudflarePreferredIp);
        Assert.True(existing.EnableDohFallback);
        Assert.False(existing.EnableFastGithub);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void SavesNetworkPreferencesIndependently(
        bool preferCloudflare,
        bool accelerateGithub,
        bool enableDohFallback)
    {
        var config = new Configuration
        {
            EnableCloudflarePreferredIp = preferCloudflare,
            EnableFastGithub = accelerateGithub,
            EnableDohFallback = enableDohFallback
        };

        var loaded = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(config))!;

        Assert.Equal(preferCloudflare, loaded.EnableCloudflarePreferredIp);
        Assert.Equal(accelerateGithub, loaded.EnableFastGithub);
        Assert.Equal(enableDohFallback, loaded.EnableDohFallback);
    }
}
