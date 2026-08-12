using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ArenaSceneTests
{
    [Fact]
    public void ProjectionMapsWorldXZAndFacingWithoutChangingRecordedData()
    {
        var projection = new ArenaProjection(new ArenaBounds(90, 80, 110, 120));

        Assert.Equal(new ArenaPoint(0, 0), projection.Project(90, 80));
        Assert.Equal(new ArenaPoint(.5f, .5f), projection.Project(100, 100));
        Assert.Equal(new ArenaPoint(1, 1), projection.Project(110, 120));
        Assert.Equal(0, ArenaProjection.ProjectFacing(0).X, 5);
        Assert.Equal(1, ArenaProjection.ProjectFacing(0).Y, 5);
        Assert.Equal(1, ArenaProjection.ProjectFacing(MathF.PI / 2).X, 5);
        Assert.Equal(0, ArenaProjection.ProjectFacing(MathF.PI / 2).Y, 5);

        var sample = CreateSample(1, x: 10, z: 20);
        var original = sample with { };
        var record = CreateRecord(
            actors: [CreateActor(1, "Pc", 0x1001)],
            frames:
            [
                CreateFrame(0, sample),
                CreateFrame(100, CreateSample(1, x: 30, z: 40)),
            ],
            waymarkFrames: [CreateWaymarkFrame(0, (WaymarkId.A, true, 0, 50))]);

        var derived = ArenaProjection.FromPullRecord(record, padding: 0);

        Assert.Equal(new ArenaBounds(0, 20, 30, 50), derived.ObservedBounds);
        Assert.Equal(new ArenaBounds(-5, 15, 35, 55), derived.Bounds);
        Assert.Equal(ArenaBoundsKind.GenericObservedField, derived.BoundsKind);
        Assert.Equal(derived.Bounds.Width, derived.Bounds.Depth);
        Assert.Equal(original, sample);
    }

    [Fact]
    public void GenericProjectionFiltersHiddenMechanicsAndContainsObservedWorldBounds()
    {
        var player = CreateActor(1, "Pc", 0x1001, "Player 1");
        var boss = CreateActor(2, "BattleNpc", 0x2001, "Boss");
        var hiddenMechanic = CreateActor(3, "BattleNpc", 0x3001, "Hidden");
        var record = CreateRecord(
            territoryType: 1363,
            mapId: 79,
            actors: [player, boss, hiddenMechanic],
            frames:
            [
                CreateFrame(
                    0,
                    CreateSample(1, x: 82.17f, z: 100),
                    CreateSample(2, x: 100, z: 82.17f),
                    CreateSample(3, x: 100, z: 25, targetable: false)),
                CreateFrame(
                    100,
                    CreateSample(1, x: 117.83f, z: 100),
                    CreateSample(2, x: 100, z: 117.83f),
                    CreateSample(3, x: 126, z: 41, targetable: false)),
            ]);

        var projection = ArenaProjection.FromPullRecord(record);

        Assert.Equal(ArenaShape.Square, projection.Shape);
        Assert.Equal(new ArenaBounds(82.17f, 82.17f, 117.83f, 117.83f), projection.ObservedBounds);
        Assert.Equal(new ArenaBounds(80, 80, 120, 120), projection.Bounds);
        Assert.Equal(ArenaBoundsKind.GenericObservedField, projection.BoundsKind);
        Assert.Equal(projection.Bounds.Width, projection.Bounds.Depth);
        Assert.Equal(.05425f, projection.Project(82.17f, 100).X, 5);
        Assert.Equal(.05425f, projection.Project(100, 82.17f).Y, 5);
        Assert.Equal(.94575f, projection.Project(117.83f, 100).X, 5);
    }

    [Fact]
    public void MapBoundsDefineCanvasWithoutChangingObservedCoordinates()
    {
        var record = CreateRecord(
            actors: [CreateActor(1, "Pc", 0x1001)],
            frames:
            [
                CreateFrame(0, CreateSample(1, x: 100, z: 65)),
                CreateFrame(100, CreateSample(1, x: 136, z: 107)),
            ]);
        var mapBounds = new ArenaBounds(-28, -28, 228, 228);

        var projection = ArenaProjection.FromMapBounds(record, mapBounds);

        Assert.Equal(mapBounds, projection.Bounds);
        Assert.Equal(new ArenaBounds(100, 65, 136, 107), projection.ObservedBounds);
        Assert.Equal(ArenaBoundsKind.MapSheet, projection.BoundsKind);
        Assert.Equal(ArenaShape.Square, projection.Shape);
        Assert.Equal(new ArenaPoint(.5f, .36328125f), projection.Project(100, 65));
    }


    [Fact]
    public void EmptyPullUsesDeterministicSafeProjection()
    {
        var projection = ArenaProjection.FromPullRecord(CreateRecord());

        Assert.Equal(new ArenaBounds(-1, -1, 1, 1), projection.ObservedBounds);
        Assert.Equal(new ArenaBounds(-20, -20, 20, 20), projection.Bounds);
        Assert.Equal(ArenaBoundsKind.GenericObservedField, projection.BoundsKind);
        Assert.Equal(new ArenaPoint(.5f, .5f), projection.Project(0, 0));
    }

    [Fact]
    public void SceneExcludesCurrentlyUntargetableActorsAndKeepsTargetableEnemyState()
    {
        var player = CreateActor(1, "Pc", 0x1001, "Player 1");
        var boss = CreateActor(2, "BattleNpc", 0x2001, "Boss");
        var helper = CreateActor(3, "BattleNpc", 0x3001, "Helper");
        var record = CreateRecord(
            actors: [player, boss, helper],
            frames:
            [
                CreateFrame(
                    0,
                    CreateSample(1, x: 0, z: 0, rotation: 0, dead: false, targetable: true),
                    CreateSample(2, x: 5, z: 5, rotation: MathF.PI, targetable: false, hitboxRadius: 6, omnidirectional: true),
                    CreateSample(3, x: 9, z: 9, targetable: false)),
                CreateFrame(
                    100,
                    CreateSample(1, x: 10, z: 10, rotation: MathF.PI / 2, dead: true, targetable: true),
                    CreateSample(2, x: 5, z: 5, rotation: MathF.PI, targetable: true, hitboxRadius: 6),
                    CreateSample(3, x: 9, z: 9, targetable: false)),
            ],
            waymarkFrames:
            [
                CreateWaymarkFrame(0, (WaymarkId.A, true, 2, 2)),
                CreateWaymarkFrame(
                    100,
                    (WaymarkId.B, true, 8, 8),
                    (WaymarkId.A, true, 3, 3)),
            ]);
        var builder = new ArenaSceneBuilder(
            record,
            new ArenaProjection(new ArenaBounds(0, 0, 10, 10)));
        var scene = builder.CreateScene();

        builder.Build(50, scene);

        Assert.Equal(50, scene.TimestampMilliseconds);
        Assert.Equal(1, scene.Actors.Length);
        var playerMarker = scene.Actors[0];
        Assert.Same(player, playerMarker.Actor);
        Assert.Equal(ArenaActorMarkerKind.Player, playerMarker.Kind);
        Assert.Equal(new ArenaPoint(.5f, .5f), playerMarker.Position);
        Assert.Equal(MathF.Sqrt(.5f), playerMarker.Facing.X, 4);
        Assert.Equal(MathF.Sqrt(.5f), playerMarker.Facing.Y, 4);
        Assert.False(playerMarker.IsDead);

        Assert.DoesNotContain(scene.Actors.ToArray(), marker => marker.Actor.StableActorId == 3);
        Assert.DoesNotContain(scene.Actors.ToArray(), marker => marker.Actor.StableActorId == 2);
        Assert.Equal(WaymarkId.A, Assert.Single(scene.Waymarks.ToArray()).Id);

        var firstSnapshot = scene.Actors.ToArray();
        builder.Build(100, scene);
        Assert.True(scene.Actors[0].IsDead);
        var bossMarker = Assert.Single(
            scene.Actors.ToArray(),
            marker => marker.Actor.StableActorId == 2);
        Assert.Equal(ArenaActorMarkerKind.BattleNpc, bossMarker.Kind);
        Assert.True(bossMarker.IsTargetable);
        Assert.Equal(6, bossMarker.HitboxRadius);
        Assert.False(bossMarker.IsOmnidirectional);
        Assert.Equal([WaymarkId.A, WaymarkId.B], scene.Waymarks.ToArray().Select(marker => marker.Id));
        builder.Build(50, scene);
        Assert.Equal(firstSnapshot, scene.Actors.ToArray());
    }

    [Fact]
    public void SceneExcludesPlayerOwnedBattleNpcAndKeepsUnownedBoss()
    {
        var player = CreateActor(1, "Pc", 0x1001, "Player 1");
        var boss = CreateActor(2, "BattleNpc", 0x2001, "Boss");
        var summon = CreateActor(3, "BattleNpc", 0x3001, "Summon", ownerId: player.EntityId);
        var record = CreateRecord(
            actors: [player, boss, summon],
            frames:
            [
                CreateFrame(
                    0,
                    CreateSample(1, x: 0, z: 0),
                    CreateSample(2, x: 5, z: 5),
                    CreateSample(3, x: 6, z: 6)),
            ]);
        var builder = new ArenaSceneBuilder(
            record,
            new ArenaProjection(new ArenaBounds(0, 0, 10, 10)));
        var scene = builder.CreateScene();

        builder.Build(0, scene);

        Assert.Equal([player, boss], scene.Actors.ToArray().Select(marker => marker.Actor));
    }

    private static PullRecord CreateRecord(
        ActorRecord[]? actors = null,
        PositionFrame[]? frames = null,
        WaymarkFrame[]? waymarkFrames = null,
        uint territoryType = 1234,
        uint mapId = 5678) =>
        new()
        {
            CaptureId = Guid.Parse("8a4ca528-e494-4011-be23-800514185f0a"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:10:00Z"),
            TerritoryType = territoryType,
            MapId = mapId,
            Instance = 1,
            Actors = actors ?? [],
            Frames = frames ?? [],
            WaymarkFrames = waymarkFrames ?? [],
        };

    private static ActorRecord CreateActor(
        int stableActorId,
        string objectKind,
        ulong gameObjectId,
        string? name = null,
        ulong ownerId = 0) =>
        new()
        {
            StableActorId = stableActorId,
            Name = name ?? $"Actor {stableActorId}",
            ObjectKind = objectKind,
            EntityId = (uint)gameObjectId,
            GameObjectId = gameObjectId,
            OwnerId = ownerId,
            BaseId = 0,
            ClassJobId = objectKind == "Pc" ? 19u : 0u,
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
        float x,
        float z,
        float rotation = 0,
        bool dead = false,
        bool targetable = true,
        float hitboxRadius = 0,
        bool omnidirectional = false) =>
        new()
        {
            StableActorId = stableActorId,
            X = x,
            Y = 0,
            Z = z,
            Rotation = rotation,
            HitboxRadius = hitboxRadius,
            CurrentHp = dead ? 0u : 100u,
            MaxHp = 100,
            IsDead = dead,
            IsTargetable = targetable,
            IsOmnidirectional = omnidirectional,
        };

    private static WaymarkFrame CreateWaymarkFrame(
        long timestampMilliseconds,
        params (WaymarkId Id, bool Active, float X, float Z)[] overrides)
    {
        var states = Enum.GetValues<WaymarkId>()
            .Select(id => new WaymarkState
            {
                Id = id,
                Active = false,
                X = 0,
                Y = 0,
                Z = 0,
            })
            .ToArray();
        foreach (var item in overrides)
        {
            states[(int)item.Id] = states[(int)item.Id] with
            {
                Active = item.Active,
                X = item.X,
                Z = item.Z,
            };
        }

        Array.Reverse(states);
        return new WaymarkFrame
        {
            TimestampMilliseconds = timestampMilliseconds,
            Waymarks = states,
        };
    }
}
