using Newtonsoft.Json;
using Xunit;

namespace FastDalamudCN.Tests;

public class ConfigurationTests
{
    [Fact]
    public void CloudflarePreferredIpDefaultsToEnabledForNewAndExistingConfigs()
    {
        Assert.True(new Configuration().EnableCloudflarePreferredIp);

        var existing = JsonConvert.DeserializeObject<Configuration>("{\"Version\":1,\"EnableFastGithub\":false}")!;

        Assert.True(existing.EnableCloudflarePreferredIp);
        Assert.False(existing.EnableFastGithub);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void SavesCloudflareAndGithubPreferencesIndependently(bool preferCloudflare, bool accelerateGithub)
    {
        var config = new Configuration
        {
            EnableCloudflarePreferredIp = preferCloudflare,
            EnableFastGithub = accelerateGithub
        };

        var loaded = JsonConvert.DeserializeObject<Configuration>(JsonConvert.SerializeObject(config))!;

        Assert.Equal(preferCloudflare, loaded.EnableCloudflarePreferredIp);
        Assert.Equal(accelerateGithub, loaded.EnableFastGithub);
    }
}
