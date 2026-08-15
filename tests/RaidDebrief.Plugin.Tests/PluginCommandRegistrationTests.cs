using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class PluginCommandRegistrationTests
{
    [Fact]
    public void RegistersOnlyNonNativeCommandNames()
    {
        Assert.Equal(["/rdebrief", "/rdb"], Plugin.RegisteredCommandNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("replay")]
    [InlineData(" RePlAy ")]
    public void CommandsAlwaysOpenTheReplayMainWindow(string arguments)
    {
        var openCount = 0;
        var router = new PluginCommandRouter(() => openCount++);

        router.Execute(arguments);

        Assert.Equal(1, openCount);
    }
}
