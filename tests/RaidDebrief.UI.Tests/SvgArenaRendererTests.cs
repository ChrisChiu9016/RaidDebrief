using System.Xml.Linq;
using RaidDebrief.Core;
using RaidDebrief.UI;
using Xunit;

namespace RaidDebrief.UI.Tests;

public sealed class SvgArenaRendererTests
{
#if RECORDED_FIXTURES
    private static readonly string P10SFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "testdata",
        "recorded",
        "P10S.json");
#endif

    [Fact]
    public void RendersDeterministicValidSvgWithoutUntargetableActorsOrMarkers()
    {
        var record = CreateRecord();
        var sceneBuilder = new ArenaSceneBuilder(
            record,
            new ArenaProjection(new ArenaBounds(0, 0, 10, 10)));
        var scene = sceneBuilder.CreateScene();
        var renderer = new SvgArenaRenderer(width: 640, height: 480, padding: 32);
        sceneBuilder.Build(0, scene);

        var first = renderer.Render(scene);

        var document = XDocument.Parse(first);
        XNamespace svg = "http://www.w3.org/2000/svg";
        Assert.Equal("0 0 640 480", document.Root!.Attribute("viewBox")!.Value);
        Assert.Equal(1, document.Descendants(svg + "g").Count(element => element.Attribute("data-stable-actor-id") is not null));
        var targetCircle = Assert.Single(document.Descendants(svg + "circle")
, element => element.Attribute("class")?.Value.Contains("target-circle", StringComparison.Ordinal) == true);
        Assert.Equal("61.4", targetCircle.Attribute("r")!.Value);
        Assert.Equal("2", targetCircle.Attribute("stroke-width")!.Value);
        Assert.DoesNotContain("data-stable-actor-id=\"1\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Player &lt;1&gt;", first, StringComparison.Ordinal);
        Assert.DoesNotContain("data-target-marker-id=\"Attack1\"", first, StringComparison.Ordinal);
        Assert.Contains("class=\"battle-npc\"", first, StringComparison.Ordinal);
        Assert.Contains("Boss &amp; Add", first, StringComparison.Ordinal);
        Assert.Contains("data-waymark-id=\"A\"", first, StringComparison.Ordinal);
        Assert.Contains("00:00.000", first, StringComparison.Ordinal);

        sceneBuilder.Build(100, scene);
        renderer.Render(scene);
        sceneBuilder.Build(0, scene);
        Assert.Equal(first, renderer.Render(scene));
    }

    [Fact]
    public void TerritorySpecificIdsStillUseNeutralGenericSquareBackground()
    {
        var record = CreateRecord() with
        {
            TerritoryType = 1363,
            MapId = 79,
        };
        var projection = ArenaProjection.FromPullRecord(record);
        var sceneBuilder = new ArenaSceneBuilder(record, projection);
        var scene = sceneBuilder.CreateScene();
        sceneBuilder.Build(0, scene);

        var svg = new SvgArenaRenderer(width: 640, height: 480, padding: 32).Render(scene);

        Assert.Equal(ArenaShape.Square, scene.Shape);
        Assert.Equal(ArenaBoundsKind.GenericObservedField, scene.BoundsKind);
        Assert.Equal(scene.WorldBounds.Width, scene.WorldBounds.Depth);
        Assert.True(scene.WorldBounds.MinX <= scene.ObservedWorldBounds.MinX);
        Assert.True(scene.WorldBounds.MinZ <= scene.ObservedWorldBounds.MinZ);
        Assert.True(scene.WorldBounds.MaxX >= scene.ObservedWorldBounds.MaxX);
        Assert.True(scene.WorldBounds.MaxZ >= scene.ObservedWorldBounds.MaxZ);
        Assert.Contains("<rect class=\"arena arena-generic\"", svg, StringComparison.Ordinal);
        Assert.Contains(".arena{fill:#172638", svg, StringComparison.Ordinal);
    }

#if RECORDED_FIXTURES
    [Fact]
    public void P10SObservedArenaUsesNeutralGenericSquareBackground()
    {
        var record = CaptureJson.Load(P10SFixturePath);
        var projection = ArenaProjection.FromPullRecord(record);
        var sceneBuilder = new ArenaSceneBuilder(record, projection);
        var scene = sceneBuilder.CreateScene();
        sceneBuilder.Build(0, scene);

        var svg = new SvgArenaRenderer(width: 640, height: 480, padding: 32).Render(scene);

        Assert.Equal(ArenaShape.Square, scene.Shape);
        Assert.Equal(ArenaBoundsKind.GenericObservedField, scene.BoundsKind);
        Assert.Equal(scene.WorldBounds.Width, scene.WorldBounds.Depth);
        const float tolerance = .001f;
        Assert.True(scene.WorldBounds.MinX <= scene.ObservedWorldBounds.MinX + tolerance);
        Assert.True(scene.WorldBounds.MinZ <= scene.ObservedWorldBounds.MinZ + tolerance);
        Assert.True(scene.WorldBounds.MaxX + tolerance >= scene.ObservedWorldBounds.MaxX);
        Assert.True(scene.WorldBounds.MaxZ + tolerance >= scene.ObservedWorldBounds.MaxZ);
        Assert.Contains("<rect class=\"arena arena-generic\"", svg, StringComparison.Ordinal);
        Assert.Contains(".arena{fill:#172638", svg, StringComparison.Ordinal);
    }
#endif


    [Fact]
    public void RejectsInvalidViewportConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgArenaRenderer(width: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgArenaRenderer(height: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgArenaRenderer(width: 100, height: 100, padding: 50));
    }

    private static PullRecord CreateRecord() =>
        new()
        {
            CaptureId = Guid.Parse("7022b39a-7c5e-4082-aa23-66e61e5bef1f"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:01Z"),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors =
            [
                CreateActor(1, "Player <1>", "Pc", 0x1001),
                CreateActor(2, "Boss & Add", "BattleNpc", 0x2001),
            ],
            Frames =
            [
                new PositionFrame
                {
                    TimestampMilliseconds = 0,
                    Actors =
                    [
                        CreateSample(1, 2, 3, rotation: 0, dead: true, targetable: false, hitboxRadius: 0.1f),
                        CreateSample(2, 7, 6, rotation: MathF.PI / 2, dead: false, targetable: true, hitboxRadius: 1.5f),
                    ],
                },
                new PositionFrame
                {
                    TimestampMilliseconds = 100,
                    Actors =
                    [
                        CreateSample(1, 3, 4, rotation: MathF.PI / 2, dead: false, targetable: true, hitboxRadius: 0.1f),
                        CreateSample(2, 6, 5, rotation: MathF.PI, dead: false, targetable: true, hitboxRadius: 1.5f),
                    ],
                },
            ],
            TargetMarkerFrames =
            [
                new TargetMarkerFrame
                {
                    TimestampMilliseconds = 0,
                    Markers = Enum.GetValues<TargetMarkerId>()
                        .Select(id => new TargetMarkerState
                        {
                            Id = id,
                            TargetObjectId = id == TargetMarkerId.Attack1 ? 0x1001UL : 0,
                            TargetStableActorId = id == TargetMarkerId.Attack1 ? 1 : null,
                        })
                        .ToArray(),
                },
            ],
            WaymarkFrames =
            [
                new WaymarkFrame
                {
                    TimestampMilliseconds = 0,
                    Waymarks = Enum.GetValues<WaymarkId>()
                        .Select(id => new WaymarkState
                        {
                            Id = id,
                            Active = id == WaymarkId.A,
                            X = id == WaymarkId.A ? 5 : 0,
                            Y = 0,
                            Z = id == WaymarkId.A ? 5 : 0,
                        })
                        .ToArray(),
                },
            ],
        };

    private static ActorRecord CreateActor(
        int stableActorId,
        string name,
        string objectKind,
        ulong gameObjectId) =>
        new()
        {
            StableActorId = stableActorId,
            Name = name,
            ObjectKind = objectKind,
            EntityId = (uint)gameObjectId,
            GameObjectId = gameObjectId,
            BaseId = 0,
            ClassJobId = objectKind == "Pc" ? 19u : 0u,
            Level = 100,
        };

    private static ActorStateSample CreateSample(
        int stableActorId,
        float x,
        float z,
        float rotation,
        bool dead,
        bool targetable,
        float hitboxRadius) =>
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
        };
}
