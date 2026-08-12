using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ReplayTimelineTests
{
    [Fact]
    public void EqualTimestampEventsKeepOriginalRecordedOrderWithoutTypePriority()
    {
        var statusLost = CreateEvent(100, ObservedEventType.StatusLost, stableActorId: 1);
        var death = CreateEvent(100, ObservedEventType.Death, stableActorId: 1);
        var castEnded = CreateEvent(100, ObservedEventType.CastEnded, stableActorId: 1);
        var timeline = new ReplayTimeline(CreateRecord(
            events: [statusLost, death, castEnded, CreateEvent(200, ObservedEventType.DutyCompleted)]));

        var eventsAtTimestamp = timeline.GetEventsAt(100);

        Assert.Equal(3, eventsAtTimestamp.Length);
        Assert.Equal(
            [ObservedEventType.StatusLost, ObservedEventType.Death, ObservedEventType.CastEnded],
            eventsAtTimestamp.ToArray().Select(entry => entry.ObservedEvent.Type));
        Assert.Equal([0, 1, 2], eventsAtTimestamp.ToArray().Select(entry => entry.OriginalRecordedIndex));
    }

    [Fact]
    public void TimelineQueriesUseInclusiveDeterministicTimestampBounds()
    {
        var timeline = new ReplayTimeline(CreateRecord(
            events:
            [
                CreateEvent(100, ObservedEventType.CastStarted, stableActorId: 1),
                CreateEvent(100, ObservedEventType.StatusGained, stableActorId: 1),
                CreateEvent(200, ObservedEventType.Death, stableActorId: 1),
                CreateEvent(300, ObservedEventType.AliveTransition, stableActorId: 1),
            ]));

        Assert.Equal(0, timeline.GetEventsThrough(99).Length);
        Assert.Equal(2, timeline.GetEventsThrough(100).Length);
        Assert.Equal(3, timeline.GetEventsThrough(200).Length);
        Assert.Equal(2, timeline.GetEventsInRange(100, 100).Length);
        Assert.Equal(2, timeline.GetEventsInRange(200, 300).Length);
        Assert.Equal(0, timeline.GetEventsAt(250).Length);

        var firstPass = timeline.GetEventsThrough(200).ToArray();
        timeline.GetEventsThrough(300);
        Assert.Equal(firstPass, timeline.GetEventsThrough(200).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.GetEventsThrough(-1));
        Assert.Throws<ArgumentException>(() => timeline.GetEventsInRange(200, 100));
    }

    [Fact]
    public void DeathAndAliveTimelineMarkersMatchRecordedActorState()
    {
        var actor = CreateActor();
        var record = CreateRecord(
            actors: [actor],
            frames:
            [
                CreateFrame(0, dead: false),
                CreateFrame(100, dead: true),
                CreateFrame(200, dead: false),
            ],
            events:
            [
                CreateEvent(100, ObservedEventType.Death, stableActorId: 1),
                CreateEvent(200, ObservedEventType.AliveTransition, stableActorId: 1),
            ]);
        var timeline = new ReplayTimeline(record);
        var actorStates = new ActorStateResolver(record);

        Assert.True(actorStates.TryResolveActor(1, 99, out var beforeDeath));
        Assert.False(beforeDeath.IsDead);
        Assert.True(actorStates.TryResolveActor(1, 100, out var atDeath));
        Assert.True(atDeath.IsDead);
        Assert.Equal(ObservedEventType.Death, Assert.Single(timeline.GetEventsAt(100).ToArray()).ObservedEvent.Type);

        Assert.True(actorStates.TryResolveActor(1, 199, out var beforeAlive));
        Assert.True(beforeAlive.IsDead);
        Assert.True(actorStates.TryResolveActor(1, 200, out var atAlive));
        Assert.False(atAlive.IsDead);
        var aliveEvent = Assert.Single(timeline.GetEventsAt(200).ToArray()).ObservedEvent;
        Assert.Equal(ObservedEventType.AliveTransition, aliveEvent.Type);
        Assert.Null(aliveEvent.ActionId);
        Assert.Null(aliveEvent.StatusId);
    }

    [Fact]
    public void WaymarkResolverUsesLatestFrameAtOrBeforeTimestamp()
    {
        var first = CreateWaymarkFrame(100, active: true, x: 1);
        var second = CreateWaymarkFrame(200, active: true, x: 2);
        var cleared = CreateWaymarkFrame(300, active: false, x: 2);
        var resolver = new WaymarkStateResolver(CreateRecord(waymarkFrames: [first, second, cleared]));

        Assert.Equal(0, resolver.Resolve(99).Length);
        Assert.False(resolver.TryResolveFrame(99, out _));
        Assert.Equal(first.Waymarks, resolver.Resolve(100).ToArray());
        Assert.Equal(first.Waymarks, resolver.Resolve(199).ToArray());
        Assert.Equal(second.Waymarks, resolver.Resolve(200).ToArray());
        Assert.Equal(cleared.Waymarks, resolver.Resolve(1_000).ToArray());

        resolver.Resolve(300);
        Assert.Equal(second.Waymarks, resolver.Resolve(200).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.Resolve(-1));
    }

    private static PullRecord CreateRecord(
        ActorRecord[]? actors = null,
        PositionFrame[]? frames = null,
        ObservedEvent[]? events = null,
        WaymarkFrame[]? waymarkFrames = null) =>
        new()
        {
            CaptureId = Guid.Parse("a5ac96ad-4f19-4512-a254-500501740064"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:10:00Z"),
            TerritoryType = 1234,
            MapId = 5678,
            Instance = 1,
            Actors = actors ?? [],
            Frames = frames ?? [],
            Events = events ?? [],
            WaymarkFrames = waymarkFrames ?? [],
        };

    private static ActorRecord CreateActor() =>
        new()
        {
            StableActorId = 1,
            Name = "Player 1",
            ObjectKind = "Pc",
            EntityId = 0x10000001,
            GameObjectId = 0x10000001,
            BaseId = 0,
            ClassJobId = 19,
            Level = 100,
        };

    private static PositionFrame CreateFrame(long timestampMilliseconds, bool dead) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Actors =
            [
                new ActorStateSample
                {
                    StableActorId = 1,
                    X = 0,
                    Y = 0,
                    Z = 0,
                    Rotation = 0,
                    CurrentHp = dead ? 0u : 100u,
                    MaxHp = 100,
                    IsDead = dead,
                    IsTargetable = true,
                },
            ],
        };

    private static ObservedEvent CreateEvent(
        long timestampMilliseconds,
        ObservedEventType type,
        int? stableActorId = null) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = type switch
            {
                ObservedEventType.Death or ObservedEventType.AliveTransition =>
                    ObservedEventSource.PolledActorState,
                ObservedEventType.StatusGained or ObservedEventType.StatusLost =>
                    ObservedEventSource.PolledStatusState,
                ObservedEventType.CastStarted or ObservedEventType.CastEnded =>
                    ObservedEventSource.PolledCastState,
                _ => ObservedEventSource.DutyState,
            },
            StableActorId = stableActorId,
        };

    private static WaymarkFrame CreateWaymarkFrame(long timestampMilliseconds, bool active, float x) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Waymarks = Enum.GetValues<WaymarkId>()
                .Select(id => new WaymarkState
                {
                    Id = id,
                    Active = id == WaymarkId.A && active,
                    X = id == WaymarkId.A ? x : 0,
                    Y = 0,
                    Z = 0,
                })
                .ToArray(),
        };
}
