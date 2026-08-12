namespace RaidDebrief.Core;

public readonly record struct ArenaPoint(float X, float Y);

public readonly record struct ArenaVector(float X, float Y);

public readonly record struct ArenaBounds
{
    public ArenaBounds(float minX, float minZ, float maxX, float maxZ)
    {
        if (!float.IsFinite(minX)
            || !float.IsFinite(minZ)
            || !float.IsFinite(maxX)
            || !float.IsFinite(maxZ))
        {
            throw new ArgumentException("Arena bounds must be finite.");
        }

        if (maxX <= minX || maxZ <= minZ)
        {
            throw new ArgumentException("Arena bounds require positive X and Z extents.");
        }

        this.MinX = minX;
        this.MinZ = minZ;
        this.MaxX = maxX;
        this.MaxZ = maxZ;
    }

    public float MinX { get; }

    public float MinZ { get; }

    public float MaxX { get; }

    public float MaxZ { get; }

    public float Width => this.MaxX - this.MinX;

    public float Depth => this.MaxZ - this.MinZ;
}

public enum ArenaShape
{
    Square,
    Circle,
}

public enum ArenaBoundsKind
{
    Authoritative,
    GenericObservedField,
    MapSheet,
}



public sealed class ArenaProjection
{
    private const float MinimumExtent = 2;
    private const float MinimumGenericFieldExtent = 40;

    public ArenaProjection(ArenaBounds bounds)
        : this(bounds, bounds, ArenaShape.Square, ArenaBoundsKind.Authoritative, null)
    {
    }

    private ArenaProjection(
        ArenaBounds bounds,
        ArenaBounds observedBounds,
        ArenaShape shape,
        ArenaBoundsKind boundsKind,
        IReadOnlySet<int>? renderableActorIds)
    {
        this.Bounds = bounds;
        this.ObservedBounds = observedBounds;
        this.Shape = shape;
        this.BoundsKind = boundsKind;
        this.RenderableActorIds = renderableActorIds;
    }

    public ArenaBounds Bounds { get; }

    public ArenaBounds ObservedBounds { get; }

    public ArenaShape Shape { get; }

    public ArenaBoundsKind BoundsKind { get; }

    internal IReadOnlySet<int>? RenderableActorIds { get; }

    public static ArenaProjection FromPullRecord(
        PullRecord record,
        float padding = 0,
        ArenaShape? shape = null) =>
        Create(record, padding, shape ?? ArenaShape.Square, null);

    public static ArenaProjection FromMapBounds(PullRecord record, ArenaBounds mapBounds)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Create(record, 0, ArenaShape.Square, mapBounds);
    }

    private static ArenaProjection Create(
        PullRecord record,
        float padding,
        ArenaShape shape,
        ArenaBounds? mapBounds)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!float.IsFinite(padding) || padding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padding), padding, "Arena padding must be finite and non-negative.");
        }

        var renderableActorIds = ArenaActorVisibility.BuildRenderableActorIds(record);
        var hasPoint = false;
        var minX = float.PositiveInfinity;
        var minZ = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxZ = float.NegativeInfinity;
        foreach (var frame in record.Frames)
        {
            foreach (var actor in frame.Actors)
            {
                if (renderableActorIds.Contains(actor.StableActorId))
                {
                    Include(actor.X, actor.Z, ref hasPoint, ref minX, ref minZ, ref maxX, ref maxZ);
                }
            }
        }

        foreach (var frame in record.WaymarkFrames)
        {
            foreach (var waymark in frame.Waymarks)
            {
                if (waymark.Active)
                {
                    Include(waymark.X, waymark.Z, ref hasPoint, ref minX, ref minZ, ref maxX, ref maxZ);
                }
            }
        }

        ArenaBounds observedBounds;
        if (hasPoint)
        {
            ExpandMinimumExtent(ref minX, ref maxX);
            ExpandMinimumExtent(ref minZ, ref maxZ);
            observedBounds = new ArenaBounds(minX, minZ, maxX, maxZ);
        }
        else
        {
            observedBounds = mapBounds ?? new ArenaBounds(-1, -1, 1, 1);
        }

        if (mapBounds is { } resolvedMapBounds)
        {
            return new ArenaProjection(
                resolvedMapBounds,
                observedBounds,
                ArenaShape.Square,
                ArenaBoundsKind.MapSheet,
                renderableActorIds);
        }

        var bounds = shape == ArenaShape.Circle
            ? CreateCircularBounds(record, renderableActorIds, observedBounds, padding)
            : CreateGenericSquareBounds(observedBounds, padding);
        return new ArenaProjection(
            bounds,
            observedBounds,
            shape,
            ArenaBoundsKind.GenericObservedField,
            renderableActorIds);
    }

    public ArenaPoint Project(float worldX, float worldZ)
    {
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ))
        {
            throw new ArgumentException("Projected world coordinates must be finite.");
        }

        return new ArenaPoint(
            (worldX - this.Bounds.MinX) / this.Bounds.Width,
            (worldZ - this.Bounds.MinZ) / this.Bounds.Depth);
    }

    public static ArenaVector ProjectFacing(float rotation)
    {
        if (!float.IsFinite(rotation))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Rotation must be finite.");
        }

        return new ArenaVector(MathF.Sin(rotation), MathF.Cos(rotation));
    }

    private static ArenaBounds CreateCircularBounds(
        PullRecord record,
        IReadOnlySet<int> renderableActorIds,
        ArenaBounds observedBounds,
        float padding)
    {
        var centerX = observedBounds.MinX + (observedBounds.Width / 2);
        var centerZ = observedBounds.MinZ + (observedBounds.Depth / 2);
        var radiusSquared = MinimumExtent * MinimumExtent / 4;
        foreach (var frame in record.Frames)
        {
            foreach (var actor in frame.Actors)
            {
                if (renderableActorIds.Contains(actor.StableActorId))
                {
                    radiusSquared = MathF.Max(
                        radiusSquared,
                        DistanceSquared(actor.X, actor.Z, centerX, centerZ));
                }
            }
        }

        foreach (var frame in record.WaymarkFrames)
        {
            foreach (var waymark in frame.Waymarks)
            {
                if (waymark.Active)
                {
                    radiusSquared = MathF.Max(
                        radiusSquared,
                        DistanceSquared(waymark.X, waymark.Z, centerX, centerZ));
                }
            }
        }

        var radius = MathF.Max(
            MathF.Sqrt(radiusSquared),
            MinimumGenericFieldExtent / 2) + padding;
        return new ArenaBounds(
            centerX - radius,
            centerZ - radius,
            centerX + radius,
            centerZ + radius);
    }

    private static ArenaBounds CreateGenericSquareBounds(ArenaBounds observedBounds, float padding)
    {
        var centerX = observedBounds.MinX + (observedBounds.Width / 2);
        var centerZ = observedBounds.MinZ + (observedBounds.Depth / 2);
        var extent = MathF.Max(
            MinimumGenericFieldExtent,
            MathF.Max(observedBounds.Width, observedBounds.Depth)) + (padding * 2);
        var halfExtent = extent / 2;
        return new ArenaBounds(
            centerX - halfExtent,
            centerZ - halfExtent,
            centerX + halfExtent,
            centerZ + halfExtent);
    }


    private static float DistanceSquared(float x, float z, float centerX, float centerZ)
    {
        var deltaX = x - centerX;
        var deltaZ = z - centerZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private static void Include(
        float x,
        float z,
        ref bool hasPoint,
        ref float minX,
        ref float minZ,
        ref float maxX,
        ref float maxZ)
    {
        hasPoint = true;
        minX = MathF.Min(minX, x);
        minZ = MathF.Min(minZ, z);
        maxX = MathF.Max(maxX, x);
        maxZ = MathF.Max(maxZ, z);
    }

    private static void ExpandMinimumExtent(ref float minimum, ref float maximum)
    {
        var extent = maximum - minimum;
        if (extent >= MinimumExtent)
        {
            return;
        }

        var midpoint = minimum + (extent / 2);
        minimum = midpoint - (MinimumExtent / 2);
        maximum = midpoint + (MinimumExtent / 2);
    }
}
