using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class TargetMarkerTimelineTests
{
    [Fact]
    public void BuilderRecordsOnlyStateChangesAndResolverSupportsBackwardSeeks()
    {
        var builder = new TargetMarkerTimelineBuilder();
        var inactive = CreateObservations();
        var attacked = CreateObservations(
            (TargetMarkerId.Attack1, 0x1001, 1),
            (TargetMarkerId.Bind2, 0x1002, 2));

        Assert.True(builder.Observe(0, inactive));
        Assert.False(builder.Observe(25, inactive));
        Assert.True(builder.Observe(50, attacked));
        Assert.False(builder.Observe(75, attacked));
        Assert.Equal(2, builder.Frames.Count);

        var record = CreateRecord(builder.ToArray());
        var resolver = new TargetMarkerStateResolver(record);

        Assert.All(resolver.Resolve(25).ToArray(), marker => Assert.Equal(0UL, marker.TargetObjectId));
        Assert.Equal(0x1001UL, resolver.Resolve(50)[(int)TargetMarkerId.Attack1].TargetObjectId);
        Assert.Equal(0x1002UL, resolver.Resolve(75)[(int)TargetMarkerId.Bind2].TargetObjectId);
        Assert.All(resolver.Resolve(25).ToArray(), marker => Assert.Equal(0UL, marker.TargetObjectId));
    }

    [Fact]
    public void JsonValidationAndArenaScenePreserveTargetMarkerOnMovingActor()
    {
        var markerFrames = new TargetMarkerTimelineBuilder();
        markerFrames.Observe(
            0,
            CreateObservations((TargetMarkerId.Attack1, 0x1001, 1)));
        markerFrames.Observe(100, CreateObservations());
        var record = CreateRecord(markerFrames.ToArray());

        var loaded = CaptureJson.Deserialize(CaptureJson.Serialize(record));
        PullRecordValidator.Validate(loaded);
        var sceneBuilder = new ArenaSceneBuilder(
            loaded,
            new ArenaProjection(new ArenaBounds(0, 0, 10, 10)));
        var scene = sceneBuilder.CreateScene();

        sceneBuilder.Build(50, scene);
        var marker = Assert.Single(scene.TargetMarkers.ToArray());
        Assert.Equal(TargetMarkerId.Attack1, marker.Id);
        Assert.Equal(1, marker.StableActorId);
        Assert.Equal(new ArenaPoint(.5f, .5f), marker.Position);

        sceneBuilder.Build(100, scene);
        Assert.Empty(scene.TargetMarkers.ToArray());
        sceneBuilder.Build(50, scene);
        Assert.Equal(marker, Assert.Single(scene.TargetMarkers.ToArray()));
    }

    [Fact]
    public void JsonLoadUpgradesBuggyNativeSlotIdsWithoutChangingTargets()
    {
        var legacyMarkers = Enum.GetValues<TargetMarkerId>()
            .Select(id => new TargetMarkerState
            {
                Id = id,
                TargetObjectId = 0,
                TargetStableActorId = null,
            })
            .ToArray();
        legacyMarkers[(int)TargetMarkerId.Attack6] = legacyMarkers[(int)TargetMarkerId.Attack6] with
        {
            TargetObjectId = 0x1001,
            TargetStableActorId = 1,
        };
        legacyMarkers[(int)TargetMarkerId.Bind1] = legacyMarkers[(int)TargetMarkerId.Bind1] with
        {
            TargetObjectId = 0x1002,
            TargetStableActorId = 2,
        };
        var legacyRecord = CreateRecord(
            [
                new TargetMarkerFrame
                {
                    TimestampMilliseconds = 0,
                    Markers = legacyMarkers,
                },
            ]) with
        {
            Features = CaptureFeatures.TargetMarkers,
        };

        var loaded = CaptureJson.Deserialize(CaptureJson.Serialize(legacyRecord));

        Assert.Equal(
            CaptureFeatures.TargetMarkers | CaptureFeatures.TargetMarkerCanonicalOrder,
            loaded.Features);
        var markers = Assert.Single(loaded.TargetMarkerFrames).Markers;
        Assert.Equal(0x1001UL, markers[(int)TargetMarkerId.Bind1].TargetObjectId);
        Assert.Equal(1, markers[(int)TargetMarkerId.Bind1].TargetStableActorId);
        Assert.Equal(0x1002UL, markers[(int)TargetMarkerId.Stop1].TargetObjectId);
        Assert.Equal(2, markers[(int)TargetMarkerId.Stop1].TargetStableActorId);
        Assert.Equal(0UL, markers[(int)TargetMarkerId.Attack6].TargetObjectId);
    }

    private static TargetMarkerObservation[] CreateObservations(
        params (TargetMarkerId Id, ulong ObjectId, int StableActorId)[] active)
    {
        var observations = Enum.GetValues<TargetMarkerId>()
            .Select(id => new TargetMarkerObservation(id, 0, null))
            .ToArray();
        foreach (var marker in active)
        {
            observations[(int)marker.Id] = new TargetMarkerObservation(
                marker.Id,
                marker.ObjectId,
                marker.StableActorId);
        }

        return observations;
    }

    private static PullRecord CreateRecord(TargetMarkerFrame[] targetMarkerFrames) =>
        new()
        {
            Features = CaptureFeatures.Current,
            CaptureId = Guid.Parse("a805814f-fc15-4d20-9f65-d510aca25403"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-10T00:00:01Z"),
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
                    EntityId = 0x1001,
                    GameObjectId = 0x1001,
                    OwnerId = 0,
                    BaseId = 0,
                    ClassJobId = 19,
                    Level = 100,
                },
                new ActorRecord
                {
                    StableActorId = 2,
                    Name = "Player 2",
                    ObjectKind = "Pc",
                    EntityId = 0x1002,
                    GameObjectId = 0x1002,
                    OwnerId = 0,
                    BaseId = 0,
                    ClassJobId = 21,
                    Level = 100,
                },
            ],
            Frames =
            [
                CreateFrame(0, 0),
                CreateFrame(100, 10),
            ],
            TargetMarkerFrames = targetMarkerFrames,
        };

    private static PositionFrame CreateFrame(long timestampMilliseconds, float position) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            Actors =
            [
                CreateSample(1, position),
                CreateSample(2, position),
            ],
        };

    private static ActorStateSample CreateSample(int stableActorId, float position) =>
        new()
        {
            StableActorId = stableActorId,
            X = position,
            Y = 0,
            Z = position,
            Rotation = 0,
            HitboxRadius = 1,
            CurrentHp = 100,
            MaxHp = 100,
            IsDead = false,
            IsTargetable = true,
        };
}
