using System;
using System.Numerics;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayMapBackgroundTests
{
    [Fact]
    public void BuildsGameTexturePathFromLuminaMapId()
    {
        Assert.Equal(
            "ui/map/n5ra/00/n5ra00_m.tex",
            ReplayMapBackgroundResolver.BuildTexturePath("n5ra/00"));
    }

    [Fact]
    public void P10SMapFieldsProjectWorldCoordinatesIntoTextureSpace()
    {
        var projection = new ReplayMapCanvasProjection(
            sizeFactor: 400,
            offsetX: -100,
            offsetY: -100);

        Assert.Equal(new Vector2(0.5f, 0.5f), projection.ProjectWorld(100, 100));
        Assert.Equal(
            new Vector2(924f / 2048f, 924f / 2048f),
            projection.ProjectWorld(75, 75));
        Assert.Equal(
            new Vector2(1124f / 2048f, 1124f / 2048f),
            projection.ProjectWorld(125, 125));
    }

    [Fact]
    public void MapDefinitionUsesTheCompleteVisibleTextureCanvasAsWorldBounds()
    {
        Assert.True(
            ReplayMapCanvasDefinition.TryCreate(
                mapRowId: 834,
                mapId: "n5ra/00",
                sizeFactor: 400,
                offsetX: -100,
                offsetY: -100,
                out var definition));

        Assert.Equal(new ArenaBounds(-28, -28, 228, 228), definition.WorldBounds);
        Assert.True(
            definition.Projection.TryCreateDrawRegion(
                definition.WorldBounds,
                out var region));
        Assert.Equal(Vector2.Zero, region.FieldMinimum);
        Assert.Equal(Vector2.One, region.FieldMaximum);
        Assert.Equal(new Vector2(0.25f, 0.25f), region.TextureMinimum);
        Assert.Equal(new Vector2(0.75f, 0.75f), region.TextureMaximum);
    }

    [Fact]
    public void DrawRegionCropsTheMapToTheSameWorldBoundsAsActorProjection()
    {
        var projection = new ReplayMapCanvasProjection(
            sizeFactor: 400,
            offsetX: -100,
            offsetY: -100);
        var worldBounds = new ArenaBounds(75, 75, 125, 125);

        Assert.True(projection.TryCreateDrawRegion(worldBounds, out var region));
        Assert.Equal(Vector2.Zero, region.FieldMinimum);
        Assert.Equal(Vector2.One, region.FieldMaximum);
        Assert.Equal(projection.ProjectWorld(75, 75), region.TextureMinimum);
        Assert.Equal(projection.ProjectWorld(125, 125), region.TextureMaximum);

        var actorWorld = projection.ProjectWorld(100, 100);
        var actorWithinTextureCrop =
            (actorWorld - region.TextureMinimum)
            / (region.TextureMaximum - region.TextureMinimum);
        Assert.Equal(new Vector2(0.5f, 0.5f), actorWithinTextureCrop);
    }

    [Fact]
    public void DrawRegionClipsWorldBoundsAtMapTextureEdgesWithoutStretching()
    {
        var projection = new ReplayMapCanvasProjection(
            sizeFactor: 100,
            offsetX: 0,
            offsetY: 0);

        Assert.True(
            projection.TryCreateDrawRegion(
                new ArenaBounds(-2048, -2048, 0, 0),
                out var region));
        Assert.Equal(new Vector2(0.5f, 0.5f), region.FieldMinimum);
        Assert.Equal(Vector2.One, region.FieldMaximum);
        Assert.Equal(Vector2.Zero, region.TextureMinimum);
        Assert.Equal(new Vector2(0.5f, 0.5f), region.TextureMaximum);
        Assert.False(
            projection.TryCreateDrawRegion(
                new ArenaBounds(2048, 2048, 4096, 4096),
                out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidLuminaSizeFactor(float sizeFactor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ReplayMapCanvasProjection(sizeFactor, 0, 0));
    }
}
