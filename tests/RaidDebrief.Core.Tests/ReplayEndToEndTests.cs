using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ReplayEndToEndTests
{
    private const string RecordedCaptureId = "6fe1b80f-567a-41a3-8912-6d013c137aa7";

    private static readonly string RecordedFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "testdata",
        "recorded",
        $"{RecordedCaptureId}.json");

    [Fact]
    public void RecordedPullReplaysEveryFrameAndReverseScrubsDeterministically()
    {
        var record = CaptureJson.Load(RecordedFixturePath);
        var replay = new ReplaySession(record);
        var forwardHashes = new ulong[record.Frames.Length];

        Assert.Equal(223_226, replay.DurationMilliseconds);
        Assert.Equal(0, replay.Scene.Actors.Length);
        Assert.Equal(0, replay.EventsThroughCurrentTime.Length);

        for (var index = 0; index < record.Frames.Length; index++)
        {
            var timestamp = record.Frames[index].TimestampMilliseconds;
            replay.Seek(timestamp);
            Assert.Equal(timestamp, replay.Scene.TimestampMilliseconds);
            forwardHashes[index] = ComputeSceneHash(replay);
        }

        replay.Seek(1);
        Assert.Equal(10, replay.Scene.Actors.Length);
        Assert.Equal(8, CountActors(replay.Scene.Actors, ArenaActorMarkerKind.Player));

        replay.Seek(10_602);
        Assert.False(TryFindActor(replay.Scene.Actors, stableActorId: 59, out _));

        replay.Seek(32_300);
        Assert.True(TryFindActor(replay.Scene.Actors, stableActorId: 59, out var deadAdd));
        Assert.True(deadAdd.IsDead);

        replay.Seek(36_303);
        Assert.False(TryFindActor(replay.Scene.Actors, stableActorId: 59, out _));

        replay.Seek(216_403);
        Assert.False(TryFindActor(replay.Scene.Actors, stableActorId: 14, out _));

        replay.Seek(replay.DurationMilliseconds);
        Assert.Equal(1_495, replay.EventsThroughCurrentTime.Length);
        Assert.Contains(
            replay.EventsThroughCurrentTime.ToArray(),
            entry => entry.ObservedEvent.Type == ObservedEventType.DutyCompleted);

        for (var index = record.Frames.Length - 1; index >= 0; index--)
        {
            replay.Seek(record.Frames[index].TimestampMilliseconds);
            Assert.Equal(forwardHashes[index], ComputeSceneHash(replay));
        }
    }

    [Fact]
    public void CompositeReplaySynchronizesDeathRevivalWaymarksAndBossLifecycle()
    {
        var replay = new ReplaySession(CreateCompositePull());

        replay.Seek(1_000);
        Assert.True(TryFindActor(replay.Scene.Actors, stableActorId: 1, out var deadPlayer));
        Assert.True(deadPlayer.IsDead);
        Assert.Equal(2, replay.EventsThroughCurrentTime.Length);

        replay.Seek(1_500);
        Assert.Equal(1, replay.Scene.Waymarks.Length);
        Assert.Equal(WaymarkId.A, replay.Scene.Waymarks[0].Id);
        Assert.True(TryFindActor(replay.Scene.Actors, stableActorId: 1, out deadPlayer));
        Assert.True(deadPlayer.IsDead);

        replay.Seek(2_000);
        Assert.True(TryFindActor(replay.Scene.Actors, stableActorId: 1, out var revivedPlayer));
        Assert.False(revivedPlayer.IsDead);
        Assert.Equal(3, replay.EventsThroughCurrentTime.Length);

        replay.Seek(2_500);
        Assert.False(TryFindActor(replay.Scene.Actors, stableActorId: 2, out _));
        Assert.Equal(4, replay.EventsThroughCurrentTime.Length);

        replay.Seek(3_000);
        Assert.True(TryFindActor(replay.Scene.Actors, stableActorId: 2, out var respawnedBoss));
        Assert.False(respawnedBoss.IsDead);
        Assert.Equal(5, replay.EventsThroughCurrentTime.Length);

        replay.Seek(3_500);
        Assert.Equal(0, replay.Scene.Waymarks.Length);

        replay.Play();
        replay.Advance(1_000);
        Assert.Equal(4_000, replay.CurrentTimeMilliseconds);
        Assert.False(replay.IsPlaying);
        Assert.Equal(6, replay.EventsThroughCurrentTime.Length);
    }

    private static PullRecord CreateCompositePull()
    {
        var startedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        return new PullRecord
        {
            CaptureId = Guid.Parse("8648911e-aeaf-4545-b050-b0803a1d93e9"),
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = startedAtUtc.AddSeconds(4),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors =
            [
                CreateActor(1, "Player 1", "Pc", 0x10000001),
                CreateActor(2, "Boss", "BattleNpc", 0x20000001),
            ],
            Frames =
            [
                CreateFrame(0, playerDead: false, bossX: 10),
                CreateFrame(1_000, playerDead: true, bossX: 11),
                CreateFrame(2_000, playerDead: false, bossX: 12),
                CreateFrame(3_000, playerDead: false, bossX: 20),
                CreateFrame(4_000, playerDead: false, bossX: 21),
            ],
            Events =
            [
                CreateEvent(0, ObservedEventType.DutyStarted, ObservedEventSource.DutyState),
                CreateEvent(1_000, ObservedEventType.Death, ObservedEventSource.PolledActorState, stableActorId: 1),
                CreateEvent(2_000, ObservedEventType.AliveTransition, ObservedEventSource.PolledActorState, stableActorId: 1),
                CreateEvent(2_500, ObservedEventType.ActorDespawned, ObservedEventSource.PolledActorState, stableActorId: 2),
                CreateEvent(3_000, ObservedEventType.ActorSpawned, ObservedEventSource.PolledActorState, stableActorId: 2),
                CreateEvent(4_000, ObservedEventType.DutyCompleted, ObservedEventSource.DutyState),
            ],
            WaymarkFrames =
            [
                CreateWaymarkFrame(1_500, active: true),
                CreateWaymarkFrame(3_500, active: false),
            ],
        };
    }

    private static ActorRecord CreateActor(int stableActorId, string name, string objectKind, ulong gameObjectId) =>
        new()
        {
            StableActorId = stableActorId,
            Name = name,
            ObjectKind = objectKind,
            EntityId = (uint)gameObjectId,
            GameObjectId = gameObjectId,
            BaseId = objectKind == "BattleNpc" ? 1u : 0u,
            ClassJobId = objectKind == "Pc" ? 19u : 0u,
            Level = 100,
        };

    private static PositionFrame CreateFrame(long timestampMilliseconds, bool playerDead, float bossX) => new()
    {
        TimestampMilliseconds = timestampMilliseconds,
        Actors =
        [
            new ActorStateSample
            {
                StableActorId = 1,
                X = timestampMilliseconds / 1_000f,
                Y = 0,
                Z = 0,
                Rotation = 0,
                CurrentHp = playerDead ? 0u : 100u,
                MaxHp = 100,
                IsDead = playerDead,
                IsTargetable = true,
            },
            new ActorStateSample
            {
                StableActorId = 2,
                X = bossX,
                Y = 0,
                Z = 10,
                Rotation = MathF.PI,
                CurrentHp = 1_000,
                MaxHp = 1_000,
                IsDead = false,
                IsTargetable = true,
            },
        ],
    };

    private static ObservedEvent CreateEvent(
        long timestampMilliseconds,
        ObservedEventType type,
        ObservedEventSource source,
        int? stableActorId = null) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = source,
            StableActorId = stableActorId,
        };

    private static WaymarkFrame CreateWaymarkFrame(long timestampMilliseconds, bool active) => new()
    {
        TimestampMilliseconds = timestampMilliseconds,
        Waymarks = Enumerable.Range(0, 8)
            .Select(index => new WaymarkState
            {
                Id = (WaymarkId)index,
                Active = active && index == 0,
                X = index == 0 ? 5 : 0,
                Y = 0,
                Z = index == 0 ? 5 : 0,
            })
            .ToArray(),
    };

    private static int CountActors(ReadOnlySpan<ArenaActorMarker> actors, ArenaActorMarkerKind kind)
    {
        var count = 0;
        foreach (ref readonly var actor in actors)
        {
            if (actor.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryFindActor(
        ReadOnlySpan<ArenaActorMarker> actors,
        int stableActorId,
        out ArenaActorMarker result)
    {
        foreach (ref readonly var actor in actors)
        {
            if (actor.Actor.StableActorId == stableActorId)
            {
                result = actor;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static ulong ComputeSceneHash(ReplaySession replay)
    {
        var hash = Add(14_695_981_039_346_656_037, replay.CurrentTimeMilliseconds);
        foreach (ref readonly var actor in replay.Scene.Actors)
        {
            hash = Add(hash, actor.Actor.StableActorId);
            hash = Add(hash, (int)actor.Kind);
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.Position.X));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.Position.Y));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.Facing.X));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.Facing.Y));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.HitboxRadius));
            hash = Add(hash, actor.IsDead ? 1 : 0);
            hash = Add(hash, actor.IsTargetable ? 1 : 0);
        }

        foreach (ref readonly var waymark in replay.Scene.Waymarks)
        {
            hash = Add(hash, (int)waymark.Id);
            hash = Add(hash, BitConverter.SingleToInt32Bits(waymark.Position.X));
            hash = Add(hash, BitConverter.SingleToInt32Bits(waymark.Position.Y));
        }

        return Add(hash, replay.EventsThroughCurrentTime.Length);
    }

    private static ulong Add(ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        return hash * 1_099_511_628_211;
    }
}
