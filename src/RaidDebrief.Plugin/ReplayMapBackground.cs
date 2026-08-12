using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class ReplayMapCanvasCatalog
{
    private readonly Dictionary<uint, ReplayMapCanvasDefinition> definitions = new();

    public ReplayMapCanvasCatalog(IDataManager dataManager, IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            foreach (var map in dataManager.GetExcelSheet<Map>())
            {
                if (ReplayMapCanvasDefinition.TryCreate(
                        map.RowId,
                        map.Id.ToString(),
                        map.SizeFactor,
                        map.OffsetX,
                        map.OffsetY,
                        out var definition))
                {
                    this.definitions.Add(map.RowId, definition);
                }
            }

            log.Information(
                "Replay Map canvas catalog loaded {MapCount} usable rows from Lumina Map sheet.",
                this.definitions.Count);
        }
        catch (Exception exception)
        {
            this.definitions.Clear();
            log.Error(exception, "Replay Map canvas catalog failed to read the Lumina Map sheet.");
        }
    }

    public bool TryGet(uint mapRowId, out ReplayMapCanvasDefinition definition) =>
        this.definitions.TryGetValue(mapRowId, out definition);

    public ArenaProjection CreateProjection(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return this.TryGet(record.MapId, out var definition)
            ? ArenaProjection.FromMapBounds(record, definition.WorldBounds)
            : ArenaProjection.FromPullRecord(record);
    }
}

internal sealed class ReplayMapBackgroundResolver
{
    private readonly ReplayMapCanvasCatalog catalog;
    private readonly ITextureProvider textureProvider;
    private readonly IPluginLog log;

    public ReplayMapBackgroundResolver(
        ReplayMapCanvasCatalog catalog,
        ITextureProvider textureProvider,
        IPluginLog log)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.textureProvider = textureProvider ?? throw new ArgumentNullException(nameof(textureProvider));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public ReplayMapBackground? Resolve(uint mapRowId)
    {
        if (!this.catalog.TryGet(mapRowId, out var definition))
        {
            this.log.Warning(
                "Replay Map canvas could not resolve Lumina Map row {MapRowId}; using the neutral background.",
                mapRowId);
            return null;
        }

        var texture = this.textureProvider.GetFromGame(definition.TexturePath);
        this.log.Information(
            "Replay Map canvas resolved Map {MapRowId} ({MapId}); SizeFactor {SizeFactor}, offsets ({OffsetX}, {OffsetY}), texture {TexturePath}.",
            mapRowId,
            definition.MapId,
            definition.SizeFactor,
            definition.OffsetX,
            definition.OffsetY,
            definition.TexturePath);
        return new ReplayMapBackground(definition, texture);
    }

    internal static string BuildTexturePath(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        var normalized = mapId.Trim('/');
        if (normalized.Length == 0
            || normalized.Contains('\\')
            || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Lumina Map ID is not a valid game texture path.", nameof(mapId));
        }

        var fileStem = normalized.Replace("/", string.Empty, StringComparison.Ordinal);
        return $"ui/map/{normalized}/{fileStem}_m.tex";
    }
}

internal readonly record struct ReplayMapCanvasDefinition(
    uint MapRowId,
    string MapId,
    float SizeFactor,
    float OffsetX,
    float OffsetY,
    string TexturePath,
    ArenaBounds WorldBounds,
    ReplayMapCanvasProjection Projection)
{
    private const float TextureContentMinimum = 0.25f;
    private const float TextureContentMaximum = 0.75f;

    public static bool TryCreate(
        uint mapRowId,
        string mapId,
        float sizeFactor,
        float offsetX,
        float offsetY,
        out ReplayMapCanvasDefinition definition)
    {
        try
        {
            var projection = new ReplayMapCanvasProjection(sizeFactor, offsetX, offsetY);
            var minimum = projection.UnprojectTexture(
                new Vector2(TextureContentMinimum, TextureContentMinimum));
            var maximum = projection.UnprojectTexture(
                new Vector2(TextureContentMaximum, TextureContentMaximum));
            definition = new ReplayMapCanvasDefinition(
                mapRowId,
                mapId,
                sizeFactor,
                offsetX,
                offsetY,
                ReplayMapBackgroundResolver.BuildTexturePath(mapId),
                new ArenaBounds(minimum.X, minimum.Y, maximum.X, maximum.Y),
                projection);
            return true;
        }
        catch (ArgumentException)
        {
            definition = default;
            return false;
        }
    }
}

internal sealed record ReplayMapBackground(
    ReplayMapCanvasDefinition Definition,
    ISharedImmediateTexture Texture)
{
    public uint MapRowId => this.Definition.MapRowId;

    public string MapId => this.Definition.MapId;

    public ReplayMapCanvasProjection Projection => this.Definition.Projection;
}

internal readonly record struct ReplayMapCanvasProjection
{
    private const float TextureCoordinateCenter = 1024;
    private const float TextureCoordinateExtent = 2048;
    private readonly float scale;
    private readonly float offsetX;
    private readonly float offsetY;

    public ReplayMapCanvasProjection(float sizeFactor, float offsetX, float offsetY)
    {
        if (!float.IsFinite(sizeFactor) || sizeFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sizeFactor),
                sizeFactor,
                "Lumina Map SizeFactor must be finite and positive.");
        }

        if (!float.IsFinite(offsetX) || !float.IsFinite(offsetY))
        {
            throw new ArgumentException("Lumina Map offsets must be finite.");
        }

        this.scale = sizeFactor / 100f;
        this.offsetX = offsetX;
        this.offsetY = offsetY;
    }

    public Vector2 ProjectWorld(float worldX, float worldZ)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ))
        {
            throw new ArgumentException("Map canvas world coordinates must be finite.");
        }

        return new Vector2(
            ProjectCoordinate(worldX, this.offsetX, this.scale),
            ProjectCoordinate(worldZ, this.offsetY, this.scale));
    }

    public Vector2 UnprojectTexture(Vector2 textureCoordinate)
    {
        if (!float.IsFinite(textureCoordinate.X) || !float.IsFinite(textureCoordinate.Y))
        {
            throw new ArgumentException("Map texture coordinates must be finite.");
        }

        return new Vector2(
            UnprojectCoordinate(textureCoordinate.X, this.offsetX, this.scale),
            UnprojectCoordinate(textureCoordinate.Y, this.offsetY, this.scale));
    }

    public bool TryCreateDrawRegion(ArenaBounds worldBounds, out ReplayMapDrawRegion region)
    {
        var textureMinimum = this.ProjectWorld(worldBounds.MinX, worldBounds.MinZ);
        var textureMaximum = this.ProjectWorld(worldBounds.MaxX, worldBounds.MaxZ);
        if (textureMaximum.X <= 0
            || textureMaximum.Y <= 0
            || textureMinimum.X >= 1
            || textureMinimum.Y >= 1)
        {
            region = default;
            return false;
        }

        var clippedTextureMinimum = Vector2.Clamp(textureMinimum, Vector2.Zero, Vector2.One);
        var clippedTextureMaximum = Vector2.Clamp(textureMaximum, Vector2.Zero, Vector2.One);
        var textureExtent = textureMaximum - textureMinimum;
        var fieldMinimum = (clippedTextureMinimum - textureMinimum) / textureExtent;
        var fieldMaximum = (clippedTextureMaximum - textureMinimum) / textureExtent;
        region = new ReplayMapDrawRegion(
            fieldMinimum,
            fieldMaximum,
            clippedTextureMinimum,
            clippedTextureMaximum);
        return true;
    }

    private static float ProjectCoordinate(float worldCoordinate, float offset, float scale) =>
        (((worldCoordinate + offset) * scale) + TextureCoordinateCenter)
        / TextureCoordinateExtent;

    private static float UnprojectCoordinate(float textureCoordinate, float offset, float scale) =>
        (((textureCoordinate * TextureCoordinateExtent) - TextureCoordinateCenter) / scale)
        - offset;
}

internal readonly record struct ReplayMapDrawRegion(
    Vector2 FieldMinimum,
    Vector2 FieldMaximum,
    Vector2 TextureMinimum,
    Vector2 TextureMaximum);
