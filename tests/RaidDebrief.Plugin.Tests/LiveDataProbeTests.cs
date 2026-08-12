using Dalamud.Game.ClientState.Objects.Enums;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class LiveDataProbeTests
{
    [Fact]
    public void NonNetworkedPlayerProxyIsNotRecordable()
    {
        Assert.True(LiveDataProbe.IsRecordablePlayerEntity(0x1000_0001));
        Assert.False(LiveDataProbe.IsRecordablePlayerEntity(0));
        Assert.False(LiveDataProbe.IsRecordablePlayerEntity(0xE000_0000));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void DutyInstanceAcceptsEveryDalamudBoundByDutyFlag(
        bool boundByDuty,
        bool boundByDuty56,
        bool boundByDuty95,
        bool expected)
    {
        Assert.Equal(
            expected,
            LiveDataProbe.IsBoundByDuty(boundByDuty, boundByDuty56, boundByDuty95));
    }

    [Fact]
    public void BattleNpcOmnidirectionalityUsesStaticFlagOrDirectionalDisregard()
    {
        var directionalDisregard =
            new PolledStatusObservation(
                BattleNpcOmnidirectionalityCatalog.DirectionalDisregardStatusId,
                0);

        Assert.True(BattleNpcOmnidirectionalityCatalog.Resolve(
            ObjectKind.BattleNpc,
            baseIsOmnidirectional: true,
            []));
        Assert.True(BattleNpcOmnidirectionalityCatalog.Resolve(
            ObjectKind.BattleNpc,
            baseIsOmnidirectional: false,
            [directionalDisregard]));
        Assert.False(BattleNpcOmnidirectionalityCatalog.Resolve(
            ObjectKind.BattleNpc,
            baseIsOmnidirectional: false,
            [new PolledStatusObservation(1250, 0)]));
        Assert.False(BattleNpcOmnidirectionalityCatalog.Resolve(
            ObjectKind.Pc,
            baseIsOmnidirectional: true,
            [directionalDisregard]));
    }
}
