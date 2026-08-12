using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class PolledEventDetectorTests
{
    [Fact]
    public void EmitsOrderedTransitionsOnceWithObservedSources()
    {
        var detector = new PolledEventDetector();
        var events = new List<ObservedEvent>();

        detector.ObserveFrame(0, [Actor()], false, events);
        detector.ObserveFrame(
            100,
            [Actor(isCasting: true, castActionId: 123, totalCastTime: 1, statuses: [new(10, 0x200)])],
            true,
            events);
        detector.ObserveFrame(
            200,
            [Actor(isCasting: true, castActionId: 123, currentCastTime: 0.1f, totalCastTime: 1, statuses: [new(10, 0x200)])],
            true,
            events);
        detector.ObserveFrame(300, [Actor(isDead: true)], true, events);
        detector.ObserveFrame(400, [Actor()], false, events);

        Assert.Collection(
            events,
            value => AssertEvent(value, 100, ObservedEventType.CastStarted, ObservedEventSource.PolledCastState),
            value => AssertEvent(value, 100, ObservedEventType.StatusGained, ObservedEventSource.PolledStatusState),
            value => AssertEvent(value, 100, ObservedEventType.InCombatChanged, ObservedEventSource.PolledConditionState),
            value => AssertEvent(value, 300, ObservedEventType.CastInterrupted, ObservedEventSource.PolledCastState),
            value => AssertEvent(value, 300, ObservedEventType.Death, ObservedEventSource.PolledActorState),
            value => AssertEvent(value, 300, ObservedEventType.StatusLost, ObservedEventSource.PolledStatusState),
            value => AssertEvent(value, 400, ObservedEventType.AliveTransition, ObservedEventSource.PolledActorState),
            value => AssertEvent(value, 400, ObservedEventType.InCombatChanged, ObservedEventSource.PolledConditionState));

        Assert.Equal(123u, events[0].ActionId);
        Assert.Equal(10u, events[1].StatusId);
        Assert.Equal(0x200ul, events[1].RelatedObjectId);
        Assert.True(events[2].State);
        Assert.False(events[^1].State);
        Assert.All(events.Where(value => value.StableActorId is not null), value => Assert.Equal(1, value.StableActorId));
    }

    [Fact]
    public void DistinguishesCompletedAndInterruptedCasts()
    {
        var detector = new PolledEventDetector();
        var events = new List<ObservedEvent>();

        detector.ObserveFrame(0, [Actor()], false, events);
        detector.ObserveFrame(100, [Actor(isCasting: true, castActionId: 100, totalCastTime: 1)], false, events);
        detector.ObserveFrame(
            900,
            [Actor(isCasting: true, castActionId: 100, currentCastTime: 0.9f, totalCastTime: 1)],
            false,
            events);
        detector.ObserveFrame(1000, [Actor()], false, events);
        detector.ObserveFrame(1100, [Actor(isCasting: true, castActionId: 200, totalCastTime: 2)], false, events);
        detector.ObserveFrame(
            1200,
            [Actor(isCasting: true, castActionId: 200, currentCastTime: 0.3f, totalCastTime: 2)],
            false,
            events);
        detector.ObserveFrame(1300, [Actor()], false, events);

        Assert.Equal(
            [
                ObservedEventType.CastStarted,
                ObservedEventType.CastEnded,
                ObservedEventType.CastStarted,
                ObservedEventType.CastInterrupted,
            ],
            events.Select(value => value.Type));
    }

    [Fact]
    public void EmitsSpawnAndDespawnOnceAndKeepsStableIdentity()
    {
        var detector = new PolledEventDetector();
        var events = new List<ObservedEvent>();
        var first = Actor();
        var second = Actor(stableActorId: 2, gameObjectId: 0x200);

        detector.ObserveFrame(0, [first], false, events);
        detector.ObserveFrame(100, [first, second], false, events);
        detector.ObserveFrame(200, [first, second], false, events);
        detector.ObserveFrame(300, [first], false, events);
        detector.ObserveFrame(400, [first], false, events);
        detector.ObserveFrame(500, [first, second], false, events);

        Assert.Equal(
            [ObservedEventType.ActorSpawned, ObservedEventType.ActorDespawned, ObservedEventType.ActorSpawned],
            events.Select(value => value.Type));
        Assert.All(events, value => Assert.Equal(2, value.StableActorId));
    }

    [Fact]
    public void DeathAndAliveTransitionUseTheSameStableActorId()
    {
        var detector = new PolledEventDetector();
        var events = new List<ObservedEvent>();

        detector.ObserveFrame(0, [Actor()], false, events);
        detector.ObserveFrame(100, [Actor(isDead: true)], false, events);
        detector.ObserveFrame(200, [Actor()], false, events);

        Assert.Equal([ObservedEventType.Death, ObservedEventType.AliveTransition], events.Select(value => value.Type));
        Assert.All(events, value => Assert.Equal(1, value.StableActorId));
        Assert.DoesNotContain(events, value => value.Type.ToString().Contains("Raise", StringComparison.Ordinal));
    }
    [Fact]
    public void RecordsInitialCastTimingAndStatusRefreshWithoutCountdownChurn()
    {
        var detector = new PolledEventDetector();
        var events = new List<ObservedEvent>();

        detector.ObserveFrame(
            0,
            [Actor(
                isCasting: true,
                castActionId: 321,
                currentCastTime: 0.4f,
                totalCastTime: 2.5f,
                statuses: [new(1191, 0x200, 19.8f)])],
            false,
            events);
        detector.ObserveFrame(
            100,
            [Actor(
                isCasting: true,
                castActionId: 321,
                currentCastTime: 0.5f,
                totalCastTime: 2.5f,
                statuses: [new(1191, 0x200, 19.7f)])],
            false,
            events);
        detector.ObserveFrame(
            200,
            [Actor(
                isCasting: true,
                castActionId: 321,
                currentCastTime: 0.6f,
                totalCastTime: 2.5f,
                statuses: [new(1191, 0x200, 20f)])],
            false,
            events);

        Assert.Collection(
            events,
            cast =>
            {
                Assert.Equal(ObservedEventType.CastStarted, cast.Type);
                Assert.Equal(0.4f, cast.CurrentCastTime);
                Assert.Equal(2.5f, cast.TotalCastTime);
            },
            gained =>
            {
                Assert.Equal(ObservedEventType.StatusGained, gained.Type);
                Assert.Equal(19.8f, gained.StatusRemainingTime);
            });

        detector.ObserveFrame(
            300,
            [Actor(
                isCasting: true,
                castActionId: 321,
                currentCastTime: 0.7f,
                totalCastTime: 2.5f,
                statuses: [new(1191, 0x200, 20f, 1)])],
            false,
            events);

        var refreshed = Assert.Single(
            events,
            value => value.Type == ObservedEventType.StatusRefreshed);
        Assert.Equal(20f, refreshed.StatusRemainingTime);
        Assert.Equal((ushort)1, refreshed.StatusParam);
    }


    private static PolledActorObservation Actor(
        int stableActorId = 1,
        ulong gameObjectId = 0x100,
        bool isDead = false,
        bool isCasting = false,
        uint castActionId = 0,
        float currentCastTime = 0,
        float totalCastTime = 0,
        PolledStatusObservation[]? statuses = null) => new(
            stableActorId,
            gameObjectId,
            isDead,
            isCasting,
            castActionId,
            0,
            currentCastTime,
            totalCastTime,
            statuses ?? []);

    private static void AssertEvent(
        ObservedEvent value,
        long timestampMilliseconds,
        ObservedEventType type,
        ObservedEventSource source)
    {
        Assert.Equal(timestampMilliseconds, value.TimestampMilliseconds);
        Assert.Equal(type, value.Type);
        Assert.Equal(source, value.Source);
    }
}
