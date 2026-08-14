using Newtonsoft.Json;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class PluginConfigurationTests
{
    [Fact]
    public void HotEffectsDefaultToHiddenForExistingConfiguration()
    {
        var configuration = JsonConvert.DeserializeObject<PluginConfiguration>("{}");

        Assert.NotNull(configuration);
        Assert.False(configuration.ShowHotEffects);
    }

    [Fact]
    public void HotEffectsSettingRoundTrips()
    {
        var json = JsonConvert.SerializeObject(new PluginConfiguration
        {
            ShowHotEffects = true,
        });
        var configuration = JsonConvert.DeserializeObject<PluginConfiguration>(json);

        Assert.NotNull(configuration);
        Assert.True(configuration.ShowHotEffects);
    }
}
