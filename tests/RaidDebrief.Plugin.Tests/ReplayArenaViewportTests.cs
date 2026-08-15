using System.Numerics;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayArenaViewportTests
{
    [Fact]
    public void FitProjectsWholeArenaWithoutClamping()
    {
        var viewport = ArenaViewport.Fit;

        Assert.Equal(Vector2.Zero, viewport.Project(new ArenaPoint(0, 0)));
        Assert.Equal(new Vector2(0.5f, 0.5f), viewport.Project(new ArenaPoint(0.5f, 0.5f)));
        Assert.Equal(Vector2.One, viewport.Project(new ArenaPoint(1, 1)));
    }

    [Fact]
    public void MapSizeFactorCentersAndLocksInitialView()
    {
        var viewport = ArenaViewport.FromMapSizeFactor(400);

        Assert.Equal(4, viewport.Zoom);
        Assert.Equal(4, viewport.MinimumZoom);
        Assert.Equal(new Vector2(0.5f, 0.5f), viewport.Center);
        Assert.Equal(Vector2.Zero, viewport.Project(new ArenaPoint(0.375f, 0.375f)));
        Assert.Equal(Vector2.One, viewport.Project(new ArenaPoint(0.625f, 0.625f)));
        Assert.Equal(
            viewport,
            viewport.ZoomAt(new Vector2(0.2f, 0.8f), wheelDelta: -100));
        Assert.Equal(viewport, viewport.PanBy(new Vector2(1, -1)));
    }

    [Fact]
    public void MapSizeFactorPanCannotLeaveTheInitialLockedArea()
    {
        var focused = ArenaViewport.FromMapSizeFactor(400);
        var zoomed = focused.ZoomAt(new Vector2(0.5f, 0.5f), 100);
        var panned = zoomed.PanBy(new Vector2(100, -100));
        var maximumCenterOffset =
            (0.5f / focused.MinimumZoom)
            - (0.5f / zoomed.Zoom);

        Assert.Equal(20, zoomed.Zoom);
        Assert.Equal(focused.Center.X - maximumCenterOffset, panned.Center.X, 5);
        Assert.Equal(focused.Center.Y + maximumCenterOffset, panned.Center.Y, 5);
    }

    [Theory]
    [InlineData(95, 1)]
    [InlineData(100, 1)]
    [InlineData(180, 1.8f)]
    [InlineData(200, 2)]
    [InlineData(300, 3)]
    [InlineData(400, 4)]
    [InlineData(800, 8)]
    public void MapSizeFactorSelectsItsOwnInitialZoom(float sizeFactor, float expectedZoom)
    {
        var viewport = ArenaViewport.FromMapSizeFactor(sizeFactor);

        Assert.Equal(expectedZoom, viewport.Zoom, 5);
        Assert.Equal(expectedZoom, viewport.MinimumZoom, 5);
        Assert.Equal(new Vector2(0.5f, 0.5f), viewport.Center);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MapSizeFactorRejectsNonPositiveValues(float sizeFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArenaViewport.FromMapSizeFactor(sizeFactor));
    }

    [Fact]
    public void MissingMapSizeFactorFallsBackToCompleteFieldFit()
    {
        Assert.Equal(
            ArenaViewport.Fit,
            ArenaViewport.FromMapSizeFactorOrFit(null));
    }



    [Fact]
    public void ZoomKeepsCursorWorldPointAnchored()
    {
        var cursor = new Vector2(0.25f, 0.75f);

        var viewport = ArenaViewport.Fit.ZoomAt(cursor, 1);
        var projectedAnchor = viewport.Project(new ArenaPoint(cursor.X, cursor.Y));

        Assert.True(viewport.Zoom > 1);
        Assert.Equal(cursor.X, projectedAnchor.X, 5);
        Assert.Equal(cursor.Y, projectedAnchor.Y, 5);
    }

    [Fact]
    public void ZoomAndPanRemainInsideArena()
    {
        var viewport = ArenaViewport.Fit.ZoomAt(new Vector2(0.5f, 0.5f), 100);
        var panned = viewport.PanBy(new Vector2(100, -100));
        var halfExtent = 0.5f / panned.Zoom;

        Assert.Equal(20, panned.Zoom);
        Assert.InRange(panned.Center.X, halfExtent, 1 - halfExtent);
        Assert.InRange(panned.Center.Y, halfExtent, 1 - halfExtent);
        Assert.Equal(ArenaViewport.Fit, ArenaViewport.Fit.PanBy(new Vector2(1, 1)));
    }

    [Fact]
    public void NativeWaymarkIconIdsMatchTheGameResources()
    {
        (WaymarkId Id, uint IconId)[] expected =
        [
            (WaymarkId.A, 61241),
            (WaymarkId.B, 61242),
            (WaymarkId.C, 61243),
            (WaymarkId.D, 61247),
            (WaymarkId.One, 61244),
            (WaymarkId.Two, 61245),
            (WaymarkId.Three, 61246),
            (WaymarkId.Four, 61248),
        ];

        foreach (var (id, iconId) in expected)
        {
            Assert.Equal(iconId, ReplayWindow.ResolveWaymarkIconId(id));
        }
    }

    [Fact]
    public void WaymarkTexturesCompensateInsetsWhilePreservingWorldDimensions()
    {
        var bounds = new ArenaBounds(0, 0, 100, 100);
        var letterHalfSize = ReplayWindow.ResolveWaymarkHalfSize(
            WaymarkId.A,
            bounds,
            arenaSize: 400,
            ArenaViewport.Fit);
        var numberHalfSize = ReplayWindow.ResolveWaymarkHalfSize(
            WaymarkId.One,
            bounds,
            arenaSize: 400,
            ArenaViewport.Fit);
        var imageBounds = ReplayWindow.ResolveCenteredWaymarkBounds(
            new Vector2(100, 200),
            letterHalfSize * ReplayWindow.WaymarkTextureScale);

        Assert.Equal(5, letterHalfSize);
        Assert.Equal(4.6f, numberHalfSize, 5);
        Assert.Equal(new Vector2(92.5f, 192.5f), imageBounds.Minimum);
        Assert.Equal(new Vector2(107.5f, 207.5f), imageBounds.Maximum);
        Assert.Equal(
            letterHalfSize * 1.2f,
            ReplayWindow.ResolveWaymarkHalfSize(
                WaymarkId.D,
                bounds,
                arenaSize: 400,
                ArenaViewport.Fit.ZoomAt(new Vector2(0.5f), 1)),
            5);
    }

    [Fact]
    public void PlayerTargetCircleIsFixedWhileBossUsesWorldScale()
    {
        var bounds = new ArenaBounds(80, 80, 120, 120);
        var zoomed = ArenaViewport.Fit.ZoomAt(new Vector2(0.5f, 0.5f), 1);

        var bossRadius = ReplayWindow.ResolveTargetCircleRadius(
            ArenaActorMarkerKind.BattleNpc,
            6,
            bounds,
            400,
            ArenaViewport.Fit);
        var zoomedBossRadius = ReplayWindow.ResolveTargetCircleRadius(
            ArenaActorMarkerKind.BattleNpc,
            6,
            bounds,
            400,
            zoomed);
        var playerRadius = ReplayWindow.ResolveTargetCircleRadius(
            ArenaActorMarkerKind.Player,
            0,
            bounds,
            400,
            ArenaViewport.Fit);
        var zoomedPlayerRadius = ReplayWindow.ResolveTargetCircleRadius(
            ArenaActorMarkerKind.Player,
            99,
            bounds,
            400,
            zoomed);
        var playerQuad = ReplayWindow.ResolveTargetCircleQuad(
            new Vector2(100, 100),
            new ArenaVector(0, -1),
            playerRadius,
            682f / 480);

        Assert.Equal(60, bossRadius);
        Assert.Equal(72, zoomedBossRadius, 4);
        Assert.Equal(
            ReplayWindow.PlayerTargetCircleHalfWidth * ReplayWindow.TargetCircleOuterRingRadiusRatio,
            playerRadius);
        Assert.Equal(playerRadius, zoomedPlayerRadius);
        Assert.Equal(
            ReplayWindow.PlayerTargetCircleHalfWidth,
            Vector2.Distance(playerQuad.TopLeft, playerQuad.TopRight) / 2,
            4);
        Assert.Equal(
            0,
            ReplayWindow.ResolveTargetCircleRadius(
                ArenaActorMarkerKind.BattleNpc,
                0,
                bounds,
                400,
                ArenaViewport.Fit));
    }

    [Fact]
    public void BattleNpcTargetCircleSelectsRecordedStateAndLegacyStaticFallback()
    {
        Assert.Equal(
            TargetCircleVariant.Omnidirectional,
            ReplayWindow.ResolveTargetCircleVariant(
                ArenaActorMarkerKind.BattleNpc,
                recordedIsOmnidirectional: true,
                hasRecordedOmnidirectionality: true,
                baseIsOmnidirectional: false));
        Assert.Equal(
            TargetCircleVariant.Directional,
            ReplayWindow.ResolveTargetCircleVariant(
                ArenaActorMarkerKind.BattleNpc,
                recordedIsOmnidirectional: false,
                hasRecordedOmnidirectionality: true,
                baseIsOmnidirectional: true));
        Assert.Equal(
            TargetCircleVariant.Omnidirectional,
            ReplayWindow.ResolveTargetCircleVariant(
                ArenaActorMarkerKind.BattleNpc,
                recordedIsOmnidirectional: false,
                hasRecordedOmnidirectionality: false,
                baseIsOmnidirectional: true));
        Assert.Equal(
            TargetCircleVariant.Directional,
            ReplayWindow.ResolveTargetCircleVariant(
                ArenaActorMarkerKind.Player,
                recordedIsOmnidirectional: true,
                hasRecordedOmnidirectionality: true,
                baseIsOmnidirectional: true));
    }

    [Fact]
    public void OmnidirectionalTargetRingProminentCircleMatchesRecordedHitbox()
    {
        const float radius = 60;
        var quad = ReplayWindow.ResolveTargetCircleQuad(
            new Vector2(100, 100),
            new ArenaVector(0, -1),
            radius,
            textureAspectRatio: 1,
            ReplayWindow.OmnidirectionalTargetRingOuterRadiusRatio);
        var textureHalfWidth = Vector2.Distance(quad.TopLeft, quad.TopRight) / 2;

        Assert.Equal(radius / 0.78f, textureHalfWidth, 4);
    }

    [Fact]
    public void BossProminentCircleMatchesHitboxToArenaRatio()
    {
        var bounds = new ArenaBounds(82.17f, 82.17f, 117.83f, 117.83f);
        const float arenaSize = 544;

        var projectedHitboxRadius = ReplayWindow.ResolveTargetCircleRadius(
            ArenaActorMarkerKind.BattleNpc,
            6,
            bounds,
            arenaSize,
            ArenaViewport.Fit);
        var quad = ReplayWindow.ResolveTargetCircleQuad(
            new Vector2(100, 100),
            new ArenaVector(0, -1),
            projectedHitboxRadius,
            682f / 480);
        var textureHalfWidth = Vector2.Distance(quad.TopLeft, quad.TopRight) / 2;
        var renderedProminentCircleRadius =
            textureHalfWidth * ReplayWindow.TargetCircleOuterRingRadiusRatio;

        Assert.Equal(17.83f / 6, (arenaSize / 2) / projectedHitboxRadius, 4);
        Assert.Equal(projectedHitboxRadius / 0.78f, textureHalfWidth, 4);
        Assert.Equal(projectedHitboxRadius, renderedProminentCircleRadius, 4);
        Assert.Equal(14, ReplayWindow.PlayerIconHalfSize);
    }

    [Fact]
    public void TargetImageTopArrowRotatesToFacingDirection()
    {
        var position = new Vector2(100, 200);
        var quad = ReplayWindow.ResolveTargetCircleQuad(
            position,
            new ArenaVector(1, 0),
            28,
            682f / 480);

        var topCenter = (quad.TopLeft + quad.TopRight) / 2;
        var bottomCenter = (quad.BottomLeft + quad.BottomRight) / 2;
        Assert.True(topCenter.X > position.X);
        Assert.Equal(position.Y, topCenter.Y, 4);
        Assert.True(bottomCenter.X < position.X);
        Assert.Equal(position.Y, bottomCenter.Y, 4);
    }

    [Fact]
    public void EnemyHudIsFixedToArenaTopLeft()
    {
        var layout = ReplayWindow.ResolveEnemyHudLayout(
            new Vector2(100, 50),
            new Vector2(800, 450),
            ReplayWindow.EnemyHudTopInset,
            hasActiveCast: true);

        Assert.Equal(new Vector2(112, 78), layout.HeaderPosition);
        Assert.Equal(new Vector2(112, 100), layout.HealthBarMinimum);
        Assert.Equal(new Vector2(392, 110), layout.HealthBarMaximum);
        Assert.Equal(new Vector2(112, 114), layout.CastHeaderPosition);
        Assert.Equal(new Vector2(112, 136), layout.CastBarMinimum);
        Assert.Equal(new Vector2(392, 146), layout.CastBarMaximum);
    }

    [Fact]
    public void EnemyHudGroupsStackAndInactiveCastConsumesNoSpace()
    {
        var arenaMinimum = new Vector2(100, 50);
        var arenaMaximum = new Vector2(800, 450);
        var casting = ReplayWindow.ResolveEnemyHudLayout(
            arenaMinimum,
            arenaMaximum,
            ReplayWindow.EnemyHudTopInset,
            hasActiveCast: true);
        var following = ReplayWindow.ResolveEnemyHudLayout(
            arenaMinimum,
            arenaMaximum,
            casting.NextTopOffset,
            hasActiveCast: false);
        var idle = ReplayWindow.ResolveEnemyHudLayout(
            arenaMinimum,
            arenaMaximum,
            ReplayWindow.EnemyHudTopInset,
            hasActiveCast: false);

        Assert.Equal(casting.CastBarMaximum.Y + 4, casting.PanelMaximum.Y);
        Assert.True(casting.PanelMaximum.Y < following.PanelMinimum.Y);
        Assert.Equal(idle.HealthBarMaximum.Y + 4, idle.PanelMaximum.Y);
        Assert.True(idle.NextTopOffset < casting.NextTopOffset);
    }

    [Fact]
    public void SmallArenaUsesCompactEnemyHud()
    {
        var metrics = ReplayWindow.ResolveEnemyHudMetrics(240);
        var layout = ReplayWindow.ResolveEnemyHudLayout(
            new Vector2(40, 20),
            new Vector2(280, 300),
            metrics.TopInset,
            hasActiveCast: false);

        Assert.True(metrics.IsCompact);
        Assert.True(layout.IsCompact);
        Assert.Equal(220, layout.HealthBarMaximum.X - layout.HealthBarMinimum.X);
        Assert.Equal(layout.HealthBarMinimum.X, layout.CastBarMinimum.X);
        Assert.Equal(layout.HealthBarMaximum.X, layout.CastBarMaximum.X);
        Assert.False(
            ReplayWindow.ResolveEnemyHudMetrics(
                ReplayWindow.EnemyHudCompactArenaWidthThreshold).IsCompact);
    }

    [Fact]
    public void ArenaCanvasExpandsPastLegacyCapAndCentersOnShortAxis()
    {
        var large = ReplayWindow.ResolveArenaCanvasLayout(new Vector2(1_000, 760));
        var wide = ReplayWindow.ResolveArenaCanvasLayout(new Vector2(1_000, 400));
        var tall = ReplayWindow.ResolveArenaCanvasLayout(new Vector2(400, 1_000));

        Assert.Equal(760, large.Size);
        Assert.Equal(new Vector2(120, 0), large.Offset);
        Assert.Equal(400, wide.Size);
        Assert.Equal(new Vector2(300, 0), wide.Offset);
        Assert.Equal(400, tall.Size);
        Assert.Equal(new Vector2(0, 300), tall.Offset);
    }

    [Fact]
    public void OverlappingActorLabelsRequireSelectionOrHover()
    {
        ArenaActorMarker[] actors =
        [
            CreatePlayerMarker(1, new ArenaPoint(0.50f, 0.50f)),
            CreatePlayerMarker(2, new ArenaPoint(0.52f, 0.50f)),
            CreatePlayerMarker(3, new ArenaPoint(0.80f, 0.50f)),
        ];
        var arenaMinimum = Vector2.Zero;
        const float arenaSize = 500;

        Assert.False(ReplayWindow.ShouldDrawArenaActorLabel(
            actors, 0, arenaMinimum, arenaSize, ArenaViewport.Fit, null, null));
        Assert.False(ReplayWindow.ShouldDrawArenaActorLabel(
            actors, 1, arenaMinimum, arenaSize, ArenaViewport.Fit, null, null));
        Assert.True(ReplayWindow.ShouldDrawArenaActorLabel(
            actors, 2, arenaMinimum, arenaSize, ArenaViewport.Fit, null, null));
        Assert.True(ReplayWindow.ShouldDrawArenaActorLabel(
            actors, 0, arenaMinimum, arenaSize, ArenaViewport.Fit, 1, null));
        Assert.True(ReplayWindow.ShouldDrawArenaActorLabel(
            actors, 1, arenaMinimum, arenaSize, ArenaViewport.Fit, null, 2));

        Assert.Equal(
            2,
            ReplayWindow.ResolveHoveredArenaActorStableId(
                actors,
                new Vector2(260, 250),
                arenaMinimum,
                arenaSize,
                ArenaViewport.Fit));
        Assert.Equal(2, ReplayWindow.ResolveArenaActorDrawPriority(1, 1, 2));
        Assert.Equal(1, ReplayWindow.ResolveArenaActorDrawPriority(2, 1, 2));
        Assert.Equal(0, ReplayWindow.ResolveArenaActorDrawPriority(3, 1, 2));
    }

    [Fact]
    public void ArenaBackgroundClickExcludesActorAndFixedHud()
    {
        var actor = new ActorRecord
        {
            StableActorId = 1,
            Name = "Player",
            ObjectKind = "Pc",
            EntityId = 100,
            GameObjectId = 100,
            OwnerId = 0,
            BaseId = 0,
            ClassJobId = 21,
            Level = 100,
        };
        ArenaActorMarker[] actors =
        [
            new(
                actor,
                ArenaActorMarkerKind.Player,
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
                false),
        ];
        var arenaMinimum = new Vector2(100, 50);
        var arenaMaximum = new Vector2(600, 550);
        var hud = ReplayWindow.ResolveEnemyHudLayout(
            arenaMinimum,
            arenaMaximum,
            ReplayWindow.EnemyHudTopInset,
            hasActiveCast: false);

        Assert.True(ReplayWindow.IsArenaBackgroundPoint(
            new Vector2(550, 500),
            arenaMinimum,
            arenaMaximum,
            500,
            ArenaViewport.Fit,
            actors,
            enemyHudCount: 1,
            hud.NextTopOffset));
        Assert.False(ReplayWindow.IsArenaBackgroundPoint(
            new Vector2(350, 300),
            arenaMinimum,
            arenaMaximum,
            500,
            ArenaViewport.Fit,
            actors,
            enemyHudCount: 1,
            hud.NextTopOffset));
        Assert.False(ReplayWindow.IsArenaBackgroundPoint(
            hud.HeaderPosition,
            arenaMinimum,
            arenaMaximum,
            500,
            ArenaViewport.Fit,
            actors,
            enemyHudCount: 1,
            hud.NextTopOffset));
    }

    private static ArenaActorMarker CreatePlayerMarker(
        int stableActorId,
        ArenaPoint position) =>
        new(
            new ActorRecord
            {
                StableActorId = stableActorId,
                Name = $"Player {stableActorId}",
                ObjectKind = "Pc",
                EntityId = (uint)stableActorId,
                GameObjectId = (ulong)stableActorId,
                OwnerId = 0,
                BaseId = 0,
                ClassJobId = 21,
                Level = 100,
            },
            ArenaActorMarkerKind.Player,
            position,
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
}
