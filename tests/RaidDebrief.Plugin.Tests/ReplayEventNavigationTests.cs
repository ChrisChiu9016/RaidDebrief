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
    public void ActiveMitigationIncludesPlayerBuffsAndBossDamageDownDebuffs()
    {
        var statusEffects = new ReplayStatusEffectDatabase(
        [
            new(1191, "Damage taken is reduced."),
            new(1193, "Damage dealt is reduced."),
            new(1195, "Physical damage dealt is reduced while magic damage dealt is reduced by a lesser amount."),
            new(860, "Damage dealt is reduced."),
        ]);
        var replay = new ReplaySession(CreateRecord() with
        {
            Events =
            [
                CreateEvent(
                    1_000,
                    ObservedEventType.StatusGained,
                    1,
                    statusId: 1191) with { RelatedObjectId = 101 },
                CreateEvent(
                    1_500,
                    ObservedEventType.StatusGained,
                    10,
                    statusId: 1193) with { RelatedObjectId = 101 },
                CreateEvent(
                    2_000,
                    ObservedEventType.StatusLost,
                    10,
                    statusId: 1193) with { RelatedObjectId = 101 },
                CreateEvent(
                    2_500,
                    ObservedEventType.StatusGained,
                    10,
                    statusId: 1195) with { RelatedObjectId = 102 },
                CreateEvent(
                    3_000,
                    ObservedEventType.StatusGained,
                    10,
                    statusId: 860) with { RelatedObjectId = 103 },
                CreateEvent(
                    3_500,
                    ObservedEventType.StatusGained,
                    10,
                    statusId: 9999) with { RelatedObjectId = 104 },
            ],
        });
        Span<ActiveMitigationStatus> active = stackalloc ActiveMitigationStatus[8];

        var count = ReplayWindow.CollectActiveMitigations(
            replay.Timeline.Events,
            playerActorId: 1,
            bossActorId: 10,
            timestampMilliseconds: 4_000,
            statusEffects,
            showHealingOverTime: false,
            active);

        Assert.Equal(3, count);
        Assert.Equal(
            new ActiveMitigationStatus(1191, 0, MitigationTargetKind.Player),
            active[0]);
        Assert.Equal(
            new ActiveMitigationStatus(860, 4, MitigationTargetKind.Boss),
            active[1]);
        Assert.Equal(
            new ActiveMitigationStatus(1195, 3, MitigationTargetKind.Boss),
            active[2]);

        Assert.Equal(
            1,
            ReplayWindow.CollectActiveMitigations(
                replay.Timeline.Events,
                playerActorId: 1,
                bossActorId: null,
                timestampMilliseconds: 4_000,
                statusEffects,
                showHealingOverTime: false,
                active));
    }

    [Fact]
    public void ActiveMitigationIncludesFeintAddleFeyIlluminationAndTankInvulnerability()
    {
        var statusEffects = new ReplayStatusEffectDatabase(
        [
            new(317, "Magic defense and healing magic potency are increased."),
            new(82, "Impervious to most attacks."),
            new(409, "Most attacks cannot reduce your HP to less than 1."),
            new(810, "Unable to be KO'd by most attacks."),
            new(811, "Most attacks will not reduce HP below 1."),
            new(1836, "Impervious to most attacks."),
            new(3255, "Most attacks cannot reduce your HP to less than 1."),
            new(1195, "Physical and magic damage are reduced."),
            new(1203, "Physical and magic damage are reduced."),
        ]);
        var playerStatuses = new uint[] { 317, 82, 409, 810, 811, 1836, 3255 };
        var events = new ObservedEvent[playerStatuses.Length + 2];
        for (var index = 0; index < playerStatuses.Length; index++)
        {
            events[index] = CreateEvent(
                1_000 + (index * 100),
                ObservedEventType.StatusGained,
                1,
                statusId: playerStatuses[index]) with
            {
                RelatedObjectId = 101,
            };
        }

        events[^2] = CreateEvent(
            2_000,
            ObservedEventType.StatusGained,
            10,
            statusId: 1195) with
        {
            RelatedObjectId = 101,
        };
        events[^1] = CreateEvent(
            2_100,
            ObservedEventType.StatusGained,
            10,
            statusId: 1203) with
        {
            RelatedObjectId = 102,
        };
        var replay = new ReplaySession(CreateRecord() with { Events = events });
        Span<ActiveMitigationStatus> active = stackalloc ActiveMitigationStatus[16];

        var count = ReplayWindow.CollectActiveMitigations(
            replay.Timeline.Events,
            playerActorId: 1,
            bossActorId: 10,
            timestampMilliseconds: 3_000,
            statusEffects,
            showHealingOverTime: false,
            active);

        Assert.Equal(
            [3255u, 1836u, 811u, 810u, 409u, 82u, 317u, 1203u, 1195u],
            active[..count].ToArray().Select(status => status.StatusId));
    }

    [Fact]
    public void PhaseTransitionBossDebuffsFollowCurrentTargetableBoss()
    {
        var recordedEndBoss = CreateActor(68, "Kefka", "BattleNpc", 6_800);
        var activePhaseBoss = CreateActor(48, "Kefka", "BattleNpc", 4_800);
        ArenaActorMarker[] actors =
        [
            CreateMarker(recordedEndBoss, ArenaActorMarkerKind.BattleNpc) with
            {
                MaxHp = 44_109_275,
                IsTargetable = false,
            },
            CreateMarker(activePhaseBoss, ArenaActorMarkerKind.BattleNpc) with
            {
                MaxHp = 56_331_828,
            },
            CreateMarker(
                CreateActor(52, "Add", "BattleNpc", 5_200),
                ArenaActorMarkerKind.BattleNpc) with
            {
                MaxHp = 429_326,
            },
        ];
        var activeBossActorId = ReplayWindow.ResolveActiveBossActorId(
            actors,
            recordedEndBoss);
        var statusEffects = new ReplayStatusEffectDatabase(
        [
            new(1193, "Damage dealt is reduced."),
            new(1195, "Physical and magic damage are reduced."),
            new(1203, "Physical and magic damage are reduced."),
        ]);
        var replay = new ReplaySession(CreateRecord() with
        {
            Events =
            [
                CreateEvent(
                    61_504,
                    ObservedEventType.StatusGained,
                    48,
                    statusId: 1193) with { RelatedObjectId = 268_877_883 },
                CreateEvent(
                    66_800,
                    ObservedEventType.StatusGained,
                    48,
                    statusId: 1203) with { RelatedObjectId = 268_557_431 },
                CreateEvent(
                    66_901,
                    ObservedEventType.StatusGained,
                    48,
                    statusId: 1195) with { RelatedObjectId = 269_057_521 },
            ],
        });
        Span<ActiveMitigationStatus> active = stackalloc ActiveMitigationStatus[8];

        Assert.Equal(48, activeBossActorId);
        var count = ReplayWindow.CollectActiveMitigations(
            replay.Timeline.Events,
            playerActorId: 1,
            activeBossActorId,
            timestampMilliseconds: 70_000,
            statusEffects,
            showHealingOverTime: false,
            active);

        Assert.Equal(
            [1195u, 1203u, 1193u],
            active[..count].ToArray().Select(status => status.StatusId));
    }

    [Fact]
    public void ActiveMitigationIncludesHotOnlyWhenEnabled()
    {
        var statusEffects = new ReplayStatusEffectDatabase(
        [
            new(3365, "A magicked barrier is nullifying damage."),
            new(3003, "Damage taken is reduced."),
            new(3899, "Additional HP is recovered when the sage who granted this effect lands any spell."),
            new(3898, "HP restoration via healing magic is increased."),
            new(2938, "Regenerating HP over time."),
            new(2618, "Damage taken is reduced."),
            new(2620, "Regenerating HP over time."),
            new(2621, "HP recovery via healing actions is increased."),
            new(2609, "A magicked barrier is nullifying damage."),
            new(2643, "When the barrier is completely absorbed, a new barrier is created."),
            new(2642, "When the barrier is completely absorbed, a new barrier is created."),
            new(2613, "A magicked barrier is nullifying damage."),
            new(2612, "A magicked barrier is nullifying damage."),
        ]);
        var events = new (uint StatusId, ushort StatusParam)[]
        {
            (3365, 0),
            (3003, 0),
            (3899, 0),
            (3898, 0),
            (2938, 0),
            (2618, 0),
            (2620, 0),
            (2621, 0),
            (2609, 0),
            (2643, 5),
            (2642, 5),
            (2613, 0),
            (2612, 0),
            (2642, 4),
        };
        var recordedEvents = new ObservedEvent[events.Length];
        for (var index = 0; index < events.Length; index++)
        {
            recordedEvents[index] = CreateEvent(
                1_000 + (index * 100),
                index == events.Length - 1
                    ? ObservedEventType.StatusRefreshed
                    : ObservedEventType.StatusGained,
                1,
                statusId: events[index].StatusId) with
            {
                RelatedObjectId = 101,
                StatusParam = events[index].StatusParam,
                StatusRemainingTime = 30,
            };
        }

        var replay = new ReplaySession(CreateRecord() with
        {
            Events = recordedEvents,
        });
        Span<ActiveMitigationStatus> active = stackalloc ActiveMitigationStatus[64];

        var mitigationCount = ReplayWindow.CollectActiveMitigations(
            replay.Timeline.Events,
            playerActorId: 1,
            bossActorId: null,
            timestampMilliseconds: 4_000,
            statusEffects,
            showHealingOverTime: false,
            active);

        Assert.Equal(
            [2642u, 2643u, 2609u, 2618u, 3003u, 3365u],
            active[..mitigationCount].ToArray().Select(status => status.StatusId));

        var countWithHot = ReplayWindow.CollectActiveMitigations(
            replay.Timeline.Events,
            playerActorId: 1,
            bossActorId: null,
            timestampMilliseconds: 4_000,
            statusEffects,
            showHealingOverTime: true,
            active);

        Assert.Equal(
            [2642u, 2643u, 2609u, 2620u, 2618u, 2938u, 3003u, 3365u],
            active[..countWithHot].ToArray().Select(status => status.StatusId));
        Assert.Equal(events.Length - 1, active[0].ActiveEventIndex);
        Assert.Equal(
            (ushort)4,
            replay.Timeline.Events[active[0].ActiveEventIndex]
                .ObservedEvent.StatusParam);
        Assert.Equal(7, ReplayWindow.ResolveMitigationGridColumnCount(302));
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
    public void BarrierFormattingUsesRecordedPercentageAndMarksAmountApproximate()
    {
        var marker = CreateMarker(
            CreateActor(1, "Player", "Pc", 100, classJobId: 39),
            ArenaActorMarkerKind.Player) with
        {
            MaxHp = 227_580,
            BarrierPercentage = 17,
        };

        Assert.Equal("~38,688  (17%)", ReplayWindow.FormatBarrierAmount(227_580, 17));
        Assert.Equal(
            "  ·  Barrier ~38,688  (17%)",
            ReplayWindow.FormatPartyBarrierSuffix(marker));
        Assert.Equal(
            string.Empty,
            ReplayWindow.FormatPartyBarrierSuffix(marker with { BarrierPercentage = 0 }));
        Assert.Equal(
            string.Empty,
            ReplayWindow.FormatPartyBarrierSuffix(marker with { BarrierPercentage = null }));
        Assert.Equal("17%", ReplayWindow.FormatBarrierAmount(0, 17));

        // A stacked shield may exceed maximum health; report it as recorded.
        Assert.Equal("~409,644  (180%)", ReplayWindow.FormatBarrierAmount(227_580, 180));
    }

    [Fact]
    public void PartyBarrierOverlaysFromTheLeftAndStaysVisibleAtFullHealth()
    {
        // Both fractions are measured from the left edge, so the barrier does not
        // depend on the remaining health.
        var (hp, barrier) = ReplayWindow.ResolvePartyBarFractions(50, 100, 17);
        Assert.Equal(0.5f, hp, 5);
        Assert.Equal(0.17f, barrier, 5);

        // The regression this guards: at full health an offset barrier would have
        // zero width and disappear.
        (hp, barrier) = ReplayWindow.ResolvePartyBarFractions(100, 100, 35);
        Assert.Equal(1f, hp, 5);
        Assert.Equal(0.35f, barrier, 5);

        // A barrier is still clamped to the bar width, including a stacked shield that
        // exceeds maximum health.
        (hp, barrier) = ReplayWindow.ResolvePartyBarFractions(100, 100, 100);
        Assert.Equal(1f, barrier, 5);
        (hp, barrier) = ReplayWindow.ResolvePartyBarFractions(100, 100, 180);
        Assert.Equal(1f, barrier, 5);

        // Absent barrier state (legacy captures) collapses the overlay.
        (hp, barrier) = ReplayWindow.ResolvePartyBarFractions(40, 100, null);
        Assert.Equal(0.4f, hp, 5);
        Assert.Equal(0f, barrier, 5);
        Assert.Equal((0.4f, 0f), ReplayWindow.ResolvePartyBarFractions(40, 100, 0));

        // A dead actor keeps its barrier readable; an unknown maximum empties the bar.
        Assert.Equal((0f, 0.1f), ReplayWindow.ResolvePartyBarFractions(0, 100, 10));
        Assert.Equal((0f, 0f), ReplayWindow.ResolvePartyBarFractions(0, 0, 17));
    }

    [Fact]
    public void PartyHealthColorIsFixedAndOnlyDeathOverridesIt()
    {
        // Health no longer drives the colour: mid-fight health sits in the 25-50%
        // band constantly, so a threshold gradient signalled nothing.
        var alive = ReplayWindow.ResolvePartyHpColor(false);
        var dead = ReplayWindow.ResolvePartyHpColor(true);
        Assert.NotEqual(alive, dead);
        Assert.Equal(alive, ReplayWindow.ResolvePartyHpColor(false));
    }

    [Fact]
    public void DeathContextFollowsThePlayheadUntilTheWindowExpires()
    {
        ReplayDeathItem[] deaths =
        [
            CreateDeathItem(stableActorId: 1, recordedIndex: 10, timestampMilliseconds: 5_000),
            CreateDeathItem(stableActorId: 2, recordedIndex: 11, timestampMilliseconds: 20_000),
            CreateDeathItem(stableActorId: 3, recordedIndex: 12, timestampMilliseconds: 20_500),
        ];

        // Before the first death nothing is in scope.
        Assert.Null(ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 4_999, 8_000));

        // The death is in scope from its own timestamp until the window expires.
        Assert.Equal(10, ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 5_000, 8_000));
        Assert.Equal(10, ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 13_000, 8_000));
        Assert.Null(ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 13_001, 8_000));

        // Inside a wipe cluster the most recent death wins.
        Assert.Equal(11, ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 20_400, 8_000));
        Assert.Equal(12, ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 20_500, 8_000));
    }

    [Fact]
    public void FocusedActorOnlyResolvesItsOwnDeath()
    {
        ReplayDeathItem[] deaths =
        [
            CreateDeathItem(stableActorId: 1, recordedIndex: 10, timestampMilliseconds: 20_000),
            CreateDeathItem(stableActorId: 2, recordedIndex: 11, timestampMilliseconds: 20_500),
        ];

        // A focused party member never shows somebody else's death.
        Assert.Equal(10, ReplayWindow.ResolveDeathAtTimestamp(deaths, 1, 21_000, 8_000));
        Assert.Equal(11, ReplayWindow.ResolveDeathAtTimestamp(deaths, 2, 21_000, 8_000));
        Assert.Null(ReplayWindow.ResolveDeathAtTimestamp(deaths, 3, 21_000, 8_000));

        // Unfocused resolution still reports the latest death in the cluster.
        Assert.Equal(11, ReplayWindow.ResolveDeathAtTimestamp(deaths, null, 21_000, 8_000));
    }

    private static ReplayDeathItem CreateDeathItem(
        int stableActorId,
        int recordedIndex,
        long timestampMilliseconds) =>
        new(
            CreateActor(stableActorId, $"Player {stableActorId}", "Pc", (ulong)stableActorId, classJobId: 39),
            new DeathEventCorrelation
            {
                DeadActorStableId = stableActorId,
                DeathOriginalRecordedIndex = recordedIndex,
                DeathTimestampMilliseconds = timestampMilliseconds,
                Confidence = CorrelationConfidence.Unavailable,
                LastHits = [],
                Evidence = DeathCorrelationEvidence.None,
                Limitations = DeathCorrelationLimitations.None,
            });

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
    [Fact]
    public void RecordedActionNameTakesPriorityOverCurrentGameData()
    {
        Assert.Equal(
            "Recorded Boss Cast",
            ReplayGameDataCatalog.ResolveActionName(
                49_890,
                "Recorded Boss Cast",
                "Localized Current Name",
                "English Current Name"));
    }

    [Fact]
    public void UnresolvedActionFallbackIsNotCacheable()
    {
        Assert.False(
            ReplayGameDataCatalog.ShouldCacheActionName(
                "_rsv_49890_-1_1_0_0",
                "_rsv_49890_-1_1_0_0"));
        Assert.True(
            ReplayGameDataCatalog.ShouldCacheActionName(
                "Resolved Name",
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
