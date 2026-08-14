using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class PluginCommandRegistrationTests
{
    [Fact]
    public void RegistersLongAndShortCommandNames()
    {
        Assert.Equal(["/rdebrief", "/rd"], Plugin.RegisteredCommandNames);
    }
}
