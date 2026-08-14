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
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("Boss", true)]
    public void IncompleteActorNameIsSkippedBeforeInvariantGuard(
        string? name,
        bool expected)
    {
        Assert.Equal(expected, LiveDataProbe.IsRecordableActorName(name));
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ProbeWindowRefreshRequiresDutyInstance(
        bool probeRefreshRequested,
        bool isInDutyInstance,
        bool expected)
    {
        Assert.Equal(
            expected,
            LiveDataProbe.ShouldRefreshProbe(probeRefreshRequested, isInDutyInstance));
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

    [Fact]
    public void ActorNameCacheUsesObjectTableSlotAndGameObjectIdentity()
    {
        var cache = new ActorNameCache(4);

        Assert.Equal(4, cache.Capacity);
        Assert.False(cache.TryGet(2, 100, out _));

        cache.Store(2, 100, "First");
        Assert.True(cache.TryGet(2, 100, out var cached));
        Assert.Equal("First", cached);

        Assert.False(cache.TryGet(2, 200, out _));
        cache.Store(2, 200, "Replacement");
        Assert.True(cache.TryGet(2, 200, out cached));
        Assert.Equal("Replacement", cached);
    }

    [Fact]
    public void ActorNameCacheSurvivesOutputDestinationCompaction()
    {
        var cache = new ActorNameCache(4);
        cache.Store(1, 101, "Front");
        cache.Store(3, 303, "Back");

        Assert.True(cache.TryGet(3, 303, out var cached));
        Assert.Equal("Back", cached);
        Assert.False(cache.TryGet(1, 303, out _));
    }
}
