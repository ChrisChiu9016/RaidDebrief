using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class DebriefAnalyzerTests
{
    [Fact]
    public void PullTimingUsesLatestRecordedOrWallClockTimestamp()
    {
        var record = CreateRecord(
            events:
            [
                CreateEvent(25_001, ObservedEventType.DutyCompleted),
            ],
            frames:
            [
                CreateFrame(30_002, CreateSample(1, 100, 100)),
            ],
            endedAfterMilliseconds: 20_003);

        Assert.Equal(30_002, PullTiming.CalculateDurationMilliseconds(record));
        Assert.Equal(30_002, new ReplaySession(record).DurationMilliseconds);
    }

    [Fact]
    public void BuildsObjectiveDeathSequenceBossHpAndSuggestedWindow()
    {
        var record = CreateRecord(
            events:
            [
                CreateEvent(10_000, ObservedEventType.Death, 1),
                CreateEvent(12_000, ObservedEventType.AliveTransition, 1),
                CreateEvent(15_000, ObservedEventType.Death, 2),
                CreateEvent(15_000, ObservedEventType.Death, 1),
                CreateEvent(18_000, ObservedEventType.DutyWiped),
            ],
            frames:
            [
                CreateFrame(
                    0,
                    CreateSample(1, 100, 100),
                    CreateSample(2, 100, 100),
                    CreateSample(10, 318, 1_000, targetable: true)),
                CreateFrame(
                    17_000,
                    CreateSample(1, 0, 100, dead: true),
                    CreateSample(2, 0, 100, dead: true),
                    CreateSample(10, 318, 1_000, targetable: true)),
            ],
            endedAfterMilliseconds: 20_000);

        var summary = new DebriefAnalyzer().Analyze(record, pullNumber: 18);

        Assert.Equal(record.CaptureId, summary.CaptureId);
        Assert.Equal(18, summary.PullNumber);
        Assert.Equal(20_000, summary.DurationMilliseconds);
        Assert.Equal(18_000, summary.WipeTimestampMilliseconds);
        Assert.Equal(1, summary.FirstDeath?.StableActorId);
        Assert.Equal([1, 2, 1], summary.DeathSequence.Select(value => value.StableActorId));
        Assert.Equal([10_000L, 15_000L, 15_000L], summary.DeathSequence.Select(value => value.TimestampMilliseconds));
        Assert.Equal(0, summary.UnresolvedDeathEventCount);
        Assert.Equal(new DebriefReplayWindow(2_000, 18_000), summary.SuggestedReplayWindow);
        var bossHp = Assert.IsType<DebriefBossHp>(summary.BossHpAtEnd);
        Assert.Equal(318u, bossHp.CurrentHp);
        Assert.Equal(1_000u, bossHp.MaxHp);
        Assert.Equal(31.8f, bossHp.Percentage, 3);
    }

    [Fact]
    public void MissingDeathFallsBackToTwentySecondsBeforeWipe()
    {
        var record = CreateRecord(
            events:
            [
                CreateEvent(30_000, ObservedEventType.DutyWiped),
            ],
            frames:
            [
                CreateFrame(0, CreateSample(1, 100, 100)),
            ],
            endedAfterMilliseconds: 30_000);

        var summary = new DebriefAnalyzer().Analyze(record);

        Assert.Null(summary.FirstDeath);
        Assert.Empty(summary.DeathSequence);
        Assert.Equal(new DebriefReplayWindow(10_000, 30_000), summary.SuggestedReplayWindow);
    }

    [Fact]
    public void MultipleBattleNpcCandidatesDoNotGuessBossHp()
    {
        var actors = CreateActors().Append(CreateActor(11, "BattleNpc", "Add")).ToArray();
        var record = CreateRecord(
            actors: actors,
            events:
            [
                CreateEvent(5_000, ObservedEventType.DutyWiped),
            ],
            frames:
            [
                CreateFrame(
                    0,
                    CreateSample(1, 100, 100),
                    CreateSample(10, 500, 1_000, targetable: true),
                    CreateSample(11, 100, 100, targetable: true)),
            ],
            endedAfterMilliseconds: 5_000);

        var summary = new DebriefAnalyzer().Analyze(record);

        Assert.Null(summary.BossHpAtEnd);
    }

    [Fact]
    public void IgnoresNpcDeathsAndReportsUnresolvedDeathEvents()
    {
        var record = CreateRecord(
            events:
            [
                CreateEvent(1_000, ObservedEventType.Death, 10),
                CreateEvent(2_000, ObservedEventType.Death),
                CreateEvent(3_000, ObservedEventType.Death, 999),
                CreateEvent(4_000, ObservedEventType.DutyWiped),
            ],
            frames:
            [
                CreateFrame(0, CreateSample(1, 100, 100)),
            ],
            endedAfterMilliseconds: 4_000);

        var summary = new DebriefAnalyzer().Analyze(record);

        Assert.Empty(summary.DeathSequence);
        Assert.Equal(2, summary.UnresolvedDeathEventCount);
    }

    [Fact]
    public void PullNumberMustBePositiveWhenProvided()
    {
        var record = CreateRecord();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DebriefAnalyzer().Analyze(record, 0));
    }

    private static PullRecord CreateRecord(
        ActorRecord[]? actors = null,
        ObservedEvent[]? events = null,
        PositionFrame[]? frames = null,
        long endedAfterMilliseconds = 1_000)
    {
        var startedAt = DateTimeOffset.Parse("2026-08-11T00:00:00Z");
        return new PullRecord
        {
            CaptureId = Guid.Parse("f28d6907-b9f6-497f-b3df-79a430036457"),
            StartedAtUtc = startedAt,
            EndedAtUtc = startedAt.AddMilliseconds(endedAfterMilliseconds),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors = actors ?? CreateActors(),
            Frames = frames ?? [],
            Events = events ?? [],
        };
    }

    private static ActorRecord[] CreateActors() =>
    [
        CreateActor(1, "Pc", "Player 1", classJobId: 19),
        CreateActor(2, "Pc", "Player 2", classJobId: 24),
        CreateActor(10, "BattleNpc", "Boss"),
    ];

    private static ActorRecord CreateActor(
        int stableActorId,
        string objectKind,
        string name,
        uint classJobId = 0) =>
        new()
        {
            StableActorId = stableActorId,
            Name = name,
            ObjectKind = objectKind,
            EntityId = (uint)(1_000 + stableActorId),
            GameObjectId = (ulong)(2_000 + stableActorId),
            BaseId = (uint)(3_000 + stableActorId),
            ClassJobId = classJobId,
            Level = 100,
        };

    private static ObservedEvent CreateEvent(
        long timestampMilliseconds,
        ObservedEventType type,
        int? stableActorId = null) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = type is ObservedEventType.DutyWiped or ObservedEventType.DutyCompleted
                ? ObservedEventSource.DutyState
                : ObservedEventSource.PolledActorState,
            StableActorId = stableActorId,
        };

    private static PositionFrame CreateFrame(
        long timestampMilliseconds,
        params ActorStateSample[] actors) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Actors = actors,
        };

    private static ActorStateSample CreateSample(
        int stableActorId,
        uint currentHp,
        uint maxHp,
        bool dead = false,
        bool targetable = false) =>
        new()
        {
            StableActorId = stableActorId,
            X = stableActorId,
            Y = 0,
            Z = stableActorId,
            Rotation = 0,
            CurrentHp = currentHp,
            MaxHp = maxHp,
            IsDead = dead,
            IsTargetable = targetable,
        };
}
