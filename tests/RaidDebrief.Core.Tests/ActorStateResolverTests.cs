using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ActorStateResolverTests
{
    [Fact]
    public void InterpolatesPositionAndFacingWithoutReadingFutureDiscreteState()
    {
        var actor = CreateActor(stableActorId: 1, gameObjectId: 0x10000001);
        var record = CreateRecord(
            [actor],
            [
                CreateFrame(
                    1_000,
                    CreateSample(1, x: 0, rotationDegrees: 350, hp: 100, dead: false, targetable: true, hitboxRadius: 1)),
                CreateFrame(
                    1_100,
                    CreateSample(1, x: 10, rotationDegrees: 10, hp: 0, dead: true, targetable: false, hitboxRadius: 2)),
            ]);
        var resolver = new ActorStateResolver(record);

        var found = resolver.TryResolveActor(1, 1_050, out var state);

        Assert.True(found);
        Assert.Equal(5, state.X, 4);
        Assert.Equal(0, state.Y, 4);
        Assert.Equal(0, state.Z, 4);
        Assert.Equal(0, state.Rotation, 4);
        Assert.Equal(1, state.HitboxRadius);
        Assert.Equal(100u, state.CurrentHp);
        Assert.Equal(100u, state.MaxHp);
        Assert.False(state.IsDead);
        Assert.True(state.IsTargetable);
    }

    [Fact]
    public void OmnidirectionalityChangesOnlyAtRecordedSampleBoundary()
    {
        var actor = CreateActor(stableActorId: 1, gameObjectId: 0x10000001);
        var resolver = new ActorStateResolver(CreateRecord(
            [actor],
            [
                CreateFrame(0, CreateSample(1, omnidirectional: false)),
                CreateFrame(100, CreateSample(1, omnidirectional: true)),
            ]));

        Assert.True(resolver.TryResolveActor(1, 99, out var before));
        Assert.False(before.IsOmnidirectional);
        Assert.True(resolver.TryResolveActor(1, 100, out var atBoundary));
        Assert.True(atBoundary.IsOmnidirectional);
        Assert.True(resolver.TryResolveActor(1, 0, out var afterBackwardSeek));
        Assert.False(afterBackwardSeek.IsOmnidirectional);
    }

    [Theory]
    [InlineData(350, 10, 0)]
    [InlineData(10, 350, 0)]
    [InlineData(0, 180, -90)]
    [InlineData(710, 730, 0)]
    public void FacingUsesDeterministicShortestAngle(
        float startDegrees,
        float endDegrees,
        float expectedMidpointDegrees)
    {
        var actor = CreateActor(stableActorId: 1, gameObjectId: 0x10000001);
        var resolver = new ActorStateResolver(CreateRecord(
            [actor],
            [
                CreateFrame(0, CreateSample(1, rotationDegrees: startDegrees)),
                CreateFrame(100, CreateSample(1, rotationDegrees: endDegrees)),
            ]));

        Assert.True(resolver.TryResolveActor(1, 50, out var state));

        Assert.Equal(DegreesToRadians(expectedMidpointDegrees), state.Rotation, 4);
    }

    [Fact]
    public void DefinesBeforeExactAndAfterSampleBoundariesWithoutExtrapolation()
    {
        var actor = CreateActor(stableActorId: 1, gameObjectId: 0x10000001);
        var resolver = new ActorStateResolver(CreateRecord(
            [actor],
            [
                CreateFrame(100, CreateSample(1, x: 5, hp: 100)),
                CreateFrame(200, CreateSample(1, x: 15, hp: 80)),
            ]));

        Assert.False(resolver.TryResolveActor(1, 99, out _));
        Assert.True(resolver.TryResolveActor(1, 100, out var exact));
        Assert.Equal(5, exact.X);
        Assert.Equal(100u, exact.CurrentHp);

        Assert.True(resolver.TryResolveActor(1, 250, out var after));
        Assert.Equal(15, after.X);
        Assert.Equal(80u, after.CurrentHp);
        Assert.False(resolver.TryResolveActor(999, 100, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.TryResolveActor(1, -1, out _));
    }

    [Fact]
    public void DoesNotBridgeRecordedDespawnSpawnInterval()
    {
        var actor = CreateActor(stableActorId: 1, gameObjectId: 0x10000001);
        var resolver = new ActorStateResolver(CreateRecord(
            [actor],
            [
                CreateFrame(0, CreateSample(1, x: 0)),
                CreateFrame(100, CreateSample(1, x: 100)),
            ],
            [
                CreateLifecycleEvent(40, ObservedEventType.ActorDespawned, 1),
                CreateLifecycleEvent(80, ObservedEventType.ActorSpawned, 1),
            ]));

        Assert.True(resolver.TryResolveActor(1, 20, out var beforeDespawn));
        Assert.Equal(0, beforeDespawn.X);
        Assert.False(resolver.TryResolveActor(1, 40, out _));
        Assert.False(resolver.TryResolveActor(1, 50, out _));
        Assert.False(resolver.TryResolveActor(1, 80, out _));
        Assert.False(resolver.TryResolveActor(1, 99, out _));
        Assert.True(resolver.TryResolveActor(1, 100, out var afterSpawn));
        Assert.Equal(100, afterSpawn.X);
    }

    [Fact]
    public void ResolveAllUsesCallerBufferAndActorRegistryOrder()
    {
        var first = CreateActor(stableActorId: 10, gameObjectId: 0x10000010);
        var second = CreateActor(stableActorId: 5, gameObjectId: 0x10000005);
        var resolver = new ActorStateResolver(CreateRecord(
            [first, second],
            [CreateFrame(0, CreateSample(5), CreateSample(10))]));
        var destination = new ResolvedActorState[resolver.ActorCount];

        var count = resolver.ResolveAll(0, destination);

        Assert.Equal(2, count);
        Assert.Equal(10, destination[0].Actor.StableActorId);
        Assert.Equal(5, destination[1].Actor.StableActorId);
        Assert.Throws<ArgumentException>(() => resolver.ResolveAll(0, destination.AsSpan(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => resolver.ResolveAll(-1, destination));
    }

    private static PullRecord CreateRecord(
        ActorRecord[] actors,
        PositionFrame[] frames,
        ObservedEvent[]? events = null) =>
        new()
        {
            CaptureId = Guid.Parse("af3c47b3-288e-4bbc-a09e-b75e838b77b7"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:10:00Z"),
            TerritoryType = 1234,
            MapId = 5678,
            Instance = 1,
            Actors = actors,
            Frames = frames,
            Events = events ?? [],
        };

    private static ActorRecord CreateActor(int stableActorId, ulong gameObjectId) =>
        new()
        {
            StableActorId = stableActorId,
            Name = $"Actor {stableActorId}",
            ObjectKind = "Pc",
            EntityId = (uint)gameObjectId,
            GameObjectId = gameObjectId,
            BaseId = 0,
            ClassJobId = 19,
            Level = 100,
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
        float x = 0,
        float rotationDegrees = 0,
        uint hp = 100,
        bool dead = false,
        bool targetable = true,
        float hitboxRadius = 0,
        bool omnidirectional = false) =>
        new()
        {
            StableActorId = stableActorId,
            X = x,
            Y = 0,
            Z = 0,
            Rotation = DegreesToRadians(rotationDegrees),
            HitboxRadius = hitboxRadius,
            CurrentHp = hp,
            MaxHp = 100,
            IsDead = dead,
            IsTargetable = targetable,
            IsOmnidirectional = omnidirectional,
        };

    private static ObservedEvent CreateLifecycleEvent(
        long timestampMilliseconds,
        ObservedEventType type,
        int stableActorId) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = ObservedEventSource.PolledActorState,
            StableActorId = stableActorId,
        };

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180;
}
