using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ReplaySessionTests
{
    [Fact]
    public void PlayPauseAdvanceAndSeekKeepSceneTimelineAndWaymarksSynchronized()
    {
        var record = CreateRecord();
        var session = new ReplaySession(record);

        Assert.Equal(1_200, session.DurationMilliseconds);
        Assert.Equal(0, session.CurrentTimeMilliseconds);
        Assert.Equal(1, session.Scene.Actors.Length);
        Assert.Equal(0, session.EventsThroughCurrentTime.Length);
        Assert.Equal(0, session.Scene.Waymarks.Length);

        session.Play();
        session.Advance(500);

        Assert.True(session.IsPlaying);
        Assert.Equal(500, session.Scene.TimestampMilliseconds);
        Assert.Equal(5, session.Scene.Actors[0].WorldX);
        Assert.True(session.Scene.Actors[0].IsDead);
        Assert.Equal(1, session.EventsThroughCurrentTime.Length);
        Assert.Equal(0, session.Scene.Waymarks.Length);

        session.Pause();
        session.Advance(100);
        Assert.Equal(500, session.CurrentTimeMilliseconds);

        session.Seek(750);
        Assert.False(session.IsPlaying);
        Assert.Equal(7.5f, session.Scene.Actors[0].WorldX);
        Assert.Equal(1, session.Scene.Waymarks.Length);
        Assert.Equal(WaymarkId.A, session.Scene.Waymarks[0].Id);

        var firstSeekActors = session.Scene.Actors.ToArray();
        var firstSeekWaymarks = session.Scene.Waymarks.ToArray();
        var firstSeekEvents = session.EventsThroughCurrentTime.ToArray();
        session.Seek(1_000);
        session.Seek(750);

        Assert.Equal(firstSeekActors, session.Scene.Actors.ToArray());
        Assert.Equal(firstSeekWaymarks, session.Scene.Waymarks.ToArray());
        Assert.Equal(firstSeekEvents, session.EventsThroughCurrentTime.ToArray());
    }

    [Fact]
    public void PlaybackStopsAtCompletePullDurationIncludingSilentTail()
    {
        var session = new ReplaySession(CreateRecord());

        session.Seek(900);
        session.Play();
        session.Advance(500);

        Assert.Equal(1_200, session.CurrentTimeMilliseconds);
        Assert.Equal(1_200, session.Scene.TimestampMilliseconds);
        Assert.False(session.IsPlaying);
        Assert.Equal(2, session.EventsThroughCurrentTime.Length);
        Assert.Equal(1, session.Scene.Waymarks.Length);
    }

    private static PullRecord CreateRecord()
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        return new PullRecord
        {
            CaptureId = Guid.Parse("3f6fb889-740a-4d7d-8ca1-6b367eb49cf7"),
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = startedAtUtc.AddMilliseconds(1_200),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors =
            [
                new ActorRecord
                {
                    StableActorId = 1,
                    Name = "Player 1",
                    ObjectKind = "Pc",
                    EntityId = 0x10000001,
                    GameObjectId = 0x10000001,
                    BaseId = 0,
                    ClassJobId = 19,
                    Level = 100,
                },
            ],
            Frames =
            [
                CreateFrame(0, 0, isDead: false),
                CreateFrame(500, 5, isDead: true),
                CreateFrame(1_000, 10, isDead: true),
            ],
            Events =
            [
                new ObservedEvent
                {
                    TimestampMilliseconds = 500,
                    Type = ObservedEventType.Death,
                    Source = ObservedEventSource.PolledActorState,
                    StableActorId = 1,
                },
                new ObservedEvent
                {
                    TimestampMilliseconds = 1_000,
                    Type = ObservedEventType.DutyCompleted,
                    Source = ObservedEventSource.DutyState,
                },
            ],
            WaymarkFrames =
            [
                new WaymarkFrame
                {
                    TimestampMilliseconds = 750,
                    Waymarks =
                    [
                        new WaymarkState { Id = WaymarkId.A, Active = true, X = 5, Y = 0, Z = 5 },
                        new WaymarkState { Id = WaymarkId.B, Active = false, X = 0, Y = 0, Z = 0 },
                        new WaymarkState { Id = WaymarkId.C, Active = false, X = 0, Y = 0, Z = 0 },
                        new WaymarkState { Id = WaymarkId.D, Active = false, X = 0, Y = 0, Z = 0 },
                        new WaymarkState { Id = WaymarkId.One, Active = false, X = 0, Y = 0, Z = 0 },
                        new WaymarkState { Id = WaymarkId.Two, Active = false, X = 0, Y = 0, Z = 0 },
                        new WaymarkState { Id = WaymarkId.Three, Active = false, X = 0, Y = 0, Z = 0 },
                        new WaymarkState { Id = WaymarkId.Four, Active = false, X = 0, Y = 0, Z = 0 },
                    ],
                },
            ],
        };
    }

    private static PositionFrame CreateFrame(long timestampMilliseconds, float x, bool isDead) => new()
    {
        TimestampMilliseconds = timestampMilliseconds,
        Actors =
        [
            new ActorStateSample
            {
                StableActorId = 1,
                X = x,
                Y = 0,
                Z = x,
                Rotation = 0,
                CurrentHp = isDead ? 0u : 100u,
                MaxHp = 100,
                IsDead = isDead,
                IsTargetable = true,
            },
        ],
    };
}
