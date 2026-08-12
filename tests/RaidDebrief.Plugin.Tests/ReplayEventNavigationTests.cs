using System.Numerics;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayEventNavigationTests
{
    [Fact]
    public void DeathNavigationIncludesOnlyRecordedPlayerDeaths()
    {
        var replay = new ReplaySession(CreateRecord());
        var entries = replay.Timeline.Events;

        Assert.True(ReplayWindow.IsPlayerDeath(replay, entries[0]));
        for (var index = 1; index < entries.Length; index++)
        {
            Assert.False(ReplayWindow.IsPlayerDeath(replay, entries[index]));
        }
    }

    [Fact]
    public void FocusLabelsOpacityAndMarkerLookupRemainStable()
    {
        var player = CreateActor(1, "Player 1", "Pc", 100, classJobId: 39);
        var otherPlayer = CreateActor(2, "Player 2", "Pc", 200, classJobId: 24);
        var boss = CreateActor(10, "Boss", "BattleNpc", 1_000);
        var selectedMarker = CreateMarker(player, ArenaActorMarkerKind.Player);
        var otherMarker = CreateMarker(otherPlayer, ArenaActorMarkerKind.Player);
        var bossMarker = CreateMarker(boss, ArenaActorMarkerKind.BattleNpc);
        ArenaActorMarker[] markers = [selectedMarker, otherMarker, bossMarker];

        Assert.Equal("RPR", ReplayWindow.ActorLabel(selectedMarker));
        Assert.Equal("Player 1 · RPR", ReplayWindow.ActorDisplayLabel(player));
        Assert.Equal(1, ReplayWindow.ResolveFocusedActorOpacity(1, selectedMarker));
        Assert.Equal(0.2f, ReplayWindow.ResolveFocusedActorOpacity(1, otherMarker));
        Assert.Equal(0.55f, ReplayWindow.ResolveFocusedActorOpacity(1, bossMarker));
        Assert.Equal(1, ReplayWindow.ResolveFocusedActorOpacity(null, otherMarker));
        Assert.True(ReplayWindow.TryFindActorMarker(markers, 2, out var found));
        Assert.Equal(2, found.Actor.StableActorId);
        Assert.False(ReplayWindow.TryFindActorMarker(markers, 999, out _));

        var viewport = ArenaViewport
            .FromMapSizeFactor(400)
            .ZoomAt(new Vector2(0.5f), 1)
            .CenterOn(new ArenaPoint(0.8f, 0.2f));
        var halfExtent = 1 / (viewport.Zoom * 2);
        Assert.Equal(0.625f - halfExtent, viewport.Center.X, 4);
        Assert.Equal(0.375f + halfExtent, viewport.Center.Y, 4);
    }

    [Fact]
    public void BossCastUsesRecordedWindowAndProgress()
    {
        var cast = CreateEvent(
            1_000,
            ObservedEventType.CastStarted,
            10,
            actionId: 777) with
        {
            CurrentCastTime = 0.25f,
            TotalCastTime = 2.25f,
        };
        var replay = new ReplaySession(CreateRecord() with
        {
            Features = CaptureFeatures.CastTiming,
            Events = [cast],
        });

        Assert.False(ReplayWindow.TryResolveActiveCast(replay, 10, 999, out _));
        Assert.True(ReplayWindow.TryResolveActiveCast(replay, 10, 3_150, out var active));
        Assert.True(ReplayWindow.TryResolveCastProgress(active, 2_000, out var current, out var total));
        Assert.Equal(1.25f, current);
        Assert.Equal(2.25f, total);
        Assert.False(ReplayWindow.TryResolveActiveCast(replay, 10, 3_151, out _));
    }

    [Fact]
    public void LegacyBossCastDerivesVisibleProgressFromTerminalEvent()
    {
        var cast = CreateEvent(
            1_000,
            ObservedEventType.CastStarted,
            10,
            actionId: 888);
        var replay = new ReplaySession(CreateRecord() with
        {
            Events =
            [
                cast,
                CreateEvent(
                    5_000,
                    ObservedEventType.CastEnded,
                    10,
                    actionId: 888),
            ],
        });

        Assert.True(ReplayWindow.TryResolveActiveCast(replay, 10, 2_500, out var active));
        Assert.True(ReplayWindow.TryResolveCastProgress(
            replay,
            10,
            active,
            2_500,
            out var current,
            out var total));
        Assert.Equal(1.5f, current);
        Assert.Equal(4, total);
        Assert.Equal(
            "1.5 / 4.0s",
            ReplayWindow.FormatCastTimeLabel(current, total));
    }

    [Fact]
    public void BuffDurationUsesRecordedRemainingTimeOrObservedLoss()
    {
        var recordedGain = CreateEvent(
            1_000,
            ObservedEventType.StatusGained,
            1,
            statusId: 1191) with
        {
            RelatedObjectId = 100,
            StatusRemainingTime = 10,
        };
        var recordedReplay = new ReplaySession(CreateRecord() with
        {
            Events = [recordedGain],
        });

        Assert.True(ReplayWindow.TryResolveStatusRemainingSeconds(
            recordedReplay.Timeline.Events,
            0,
            4_000,
            out var recordedRemaining));
        Assert.Equal(7, recordedRemaining);

        var legacyGain = CreateEvent(
            1_000,
            ObservedEventType.StatusGained,
            1,
            statusId: 1191) with
        {
            RelatedObjectId = 100,
        };
        var legacyLoss = CreateEvent(
            10_000,
            ObservedEventType.StatusLost,
            1,
            statusId: 1191) with
        {
            RelatedObjectId = 100,
        };
        var legacyReplay = new ReplaySession(CreateRecord() with
        {
            Events = [legacyGain, legacyLoss],
        });

        Assert.True(ReplayWindow.TryResolveStatusRemainingSeconds(
            legacyReplay.Timeline.Events,
            0,
            4_000,
            out var derivedRemaining));
        Assert.Equal(6, derivedRemaining);
    }

    [Fact]
    public void BuffDurationIsUnknownAcrossAnUnrecordedRefreshBoundary()
    {
        var gain = CreateEvent(
            1_000,
            ObservedEventType.StatusGained,
            1,
            statusId: 1191) with
        {
            RelatedObjectId = 100,
        };
        var refresh = CreateEvent(
            5_000,
            ObservedEventType.StatusRefreshed,
            1,
            statusId: 1191) with
        {
            RelatedObjectId = 100,
        };
        var loss = CreateEvent(
            10_000,
            ObservedEventType.StatusLost,
            1,
            statusId: 1191) with
        {
            RelatedObjectId = 100,
        };
        var replay = new ReplaySession(CreateRecord() with
        {
            Events = [gain, refresh, loss],
        });

        Assert.False(ReplayWindow.TryResolveStatusRemainingSeconds(
            replay.Timeline.Events,
            0,
            4_000,
            out _));
        Assert.True(ReplayWindow.TryResolveStatusRemainingSeconds(
            replay.Timeline.Events,
            1,
            7_000,
            out var remaining));
        Assert.Equal(3, remaining);
    }

    [Fact]
    public void PartyHpAndEnemyHudPercentageUseReplayState()
    {
        var boss = CreateMarker(
            CreateActor(11, "Boss", "BattleNpc", 1_100),
            ArenaActorMarkerKind.BattleNpc) with
        {
            CurrentHp = 97_580,
            MaxHp = 227_580,
        };
        var deadPlayer = CreateMarker(
            CreateActor(1, "Player", "Pc", 100, classJobId: 39),
            ArenaActorMarkerKind.Player) with
        {
            CurrentHp = 0,
            MaxHp = 227_580,
            IsDead = true,
        };

        Assert.Equal("97,580 / 227,580 · 42.9%", ReplayWindow.FormatPartyHp(boss));
        Assert.Equal("DEAD · 0 / 227,580 · 0.0%", ReplayWindow.FormatPartyHp(deadPlayer));
        Assert.Equal("42.9%", ReplayWindow.FormatEnemyHudHpPercentage(boss));
        var recordedEnemy = boss with
        {
            CurrentHp = 18_846_648,
            MaxHp = 37_552_669,
        };
        Assert.Equal("50.2%", ReplayWindow.FormatEnemyHudHpPercentage(recordedEnemy));
        Assert.Equal(0.42877f, ReplayWindow.ResolveHpFraction(boss), 5);
        Assert.Equal(0, ReplayWindow.ResolveHpFraction(deadPlayer));
    }

    [Fact]
    public void EnemyVitalsAreLimitedToTargetableBattleNpcs()
    {
        var enemy = CreateMarker(
            CreateActor(10, "Enemy", "BattleNpc", 1_000),
            ArenaActorMarkerKind.BattleNpc);
        var untargetableEnemy = enemy with { IsTargetable = false };
        var player = CreateMarker(
            CreateActor(1, "Player", "Pc", 100, classJobId: 39),
            ArenaActorMarkerKind.Player);

        Assert.True(ReplayWindow.ShouldDrawEnemyVitals(enemy));
        Assert.False(ReplayWindow.ShouldDrawEnemyVitals(untargetableEnemy));
        Assert.False(ReplayWindow.ShouldDrawEnemyVitals(player));
    }

    [Fact]
    public void ReservedActionPlaceholderIsNeverPresentedAsCastName()
    {
        Assert.True(ReplayGameDataCatalog.IsResolvedName("The Decisive Battle"));
        Assert.False(ReplayGameDataCatalog.IsResolvedName("_rsv_47881_-1_1_0_0"));
        Assert.False(ReplayGameDataCatalog.IsResolvedName(" "));
    }

    [Fact]
    public void ActionNameUsesEnglishWhenLocalizedRowIsReserved()
    {
        Assert.Equal(
            "サモン・イフリートII",
            ReplayGameDataCatalog.ResolveActionName(
                25838,
                "サモン・イフリートII",
                "Summon Ifrit II"));
        Assert.Equal(
            "Summon Ifrit II",
            ReplayGameDataCatalog.ResolveActionName(
                25838,
                "_rsv_25838_-1_1_0_0",
                "Summon Ifrit II"));
        Assert.Equal(
            "Action #49890",
            ReplayGameDataCatalog.ResolveActionName(
                49890,
                "_rsv_49890_-1_1_0_0",
                "_rsv_49890_-1_1_0_0"));
    }

    private static PullRecord CreateRecord()
    {
        var player = CreateActor(1, "Player 1", "Pc", 100, classJobId: 39);
        var otherPlayer = CreateActor(2, "Player 2", "Pc", 200, classJobId: 24);
        var boss = CreateActor(10, "Boss", "BattleNpc", 1_000);
        var pet = CreateActor(11, "Pet", "BattleNpc", 1_100, ownerId: player.GameObjectId);
        return new PullRecord
        {
            CaptureId = Guid.Parse("b0352259-85c0-4d88-a663-977467a0344b"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-11T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-11T00:00:10Z"),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors = [player, otherPlayer, boss, pet],
            Frames = [],
            Events =
            [
                CreateEvent(1_000, ObservedEventType.Death, 1),
                CreateEvent(2_000, ObservedEventType.AliveTransition, 1),
                CreateEvent(3_000, ObservedEventType.CastStarted, 10, actionId: 777),
                CreateEvent(3_200, ObservedEventType.CastStarted, 11, actionId: 888),
                CreateEvent(1_100, ObservedEventType.Death, 10),
                CreateEvent(1_200, ObservedEventType.Death, 11),
                CreateEvent(4_000, ObservedEventType.StatusGained, 1, statusId: 100),
                CreateEvent(4_100, ObservedEventType.StatusGained, 2, statusId: 200),
                CreateEvent(5_000, ObservedEventType.DutyWiped),
            ],
        };
    }

    private static ActorRecord CreateActor(
        int stableActorId,
        string name,
        string objectKind,
        ulong gameObjectId,
        ulong ownerId = 0,
        uint classJobId = 0) =>
        new()
        {
            StableActorId = stableActorId,
            Name = name,
            ObjectKind = objectKind,
            EntityId = (uint)gameObjectId,
            GameObjectId = gameObjectId,
            OwnerId = ownerId,
            BaseId = 0,
            ClassJobId = classJobId,
            Level = 100,
        };

    private static ArenaActorMarker CreateMarker(
        ActorRecord actor,
        ArenaActorMarkerKind kind) =>
        new(
            actor,
            kind,
            new ArenaPoint(0.5f, 0.5f),
            new ArenaVector(0, 1),
            100,
            0,
            100,
            1,
            100,
            100,
            false,
            true,
            false);

    private static ObservedEvent CreateEvent(
        long timestampMilliseconds,
        ObservedEventType type,
        int? stableActorId = null,
        uint? actionId = null,
        uint? statusId = null) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = type switch
            {
                ObservedEventType.Death or ObservedEventType.AliveTransition =>
                    ObservedEventSource.PolledActorState,
                ObservedEventType.CastStarted
                    or ObservedEventType.CastEnded
                    or ObservedEventType.CastInterrupted =>
                    ObservedEventSource.PolledCastState,
                ObservedEventType.StatusGained => ObservedEventSource.PolledStatusState,
                _ => ObservedEventSource.DutyState,
            },
            StableActorId = stableActorId,
            ActionId = actionId,
            StatusId = statusId,
        };
}
