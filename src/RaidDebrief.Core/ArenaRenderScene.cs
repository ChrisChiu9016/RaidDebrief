namespace RaidDebrief.Core;

public enum ArenaActorMarkerKind
{
    Player,
    BattleNpc,
}

public readonly record struct ArenaActorMarker(
    ActorRecord Actor,
    ArenaActorMarkerKind Kind,
    ArenaPoint Position,
    ArenaVector Facing,
    float WorldX,
    float WorldY,
    float WorldZ,
    float HitboxRadius,
    uint CurrentHp,
    uint MaxHp,
    bool IsDead,
    bool IsTargetable,
    bool IsOmnidirectional);

public readonly record struct ArenaWaymarkMarker(
    WaymarkId Id,
    ArenaPoint Position,
    float WorldX,
    float WorldY,
    float WorldZ);

public readonly record struct ArenaTargetMarker(
    TargetMarkerId Id,
    int StableActorId,
    ArenaPoint Position);

public sealed class ArenaRenderScene
{
    private readonly ArenaActorMarker[] actors;
    private readonly ArenaWaymarkMarker[] waymarks;
    private readonly ArenaTargetMarker[] targetMarkers;
    private int actorCount;
    private int waymarkCount;
    private int targetMarkerCount;

    internal ArenaRenderScene(int actorCapacity, ArenaProjection projection)
    {
        this.actors = new ArenaActorMarker[actorCapacity];
        this.waymarks = new ArenaWaymarkMarker[8];
        this.targetMarkers = new ArenaTargetMarker[TargetMarkerTimelineBuilder.MarkerCount];
        this.WorldBounds = projection.Bounds;
        this.ObservedWorldBounds = projection.ObservedBounds;
        this.Shape = projection.Shape;
        this.BoundsKind = projection.BoundsKind;
    }

    public long TimestampMilliseconds { get; private set; }

    public ArenaBounds WorldBounds { get; }

    public ArenaBounds ObservedWorldBounds { get; }

    public ArenaShape Shape { get; }

    public ArenaBoundsKind BoundsKind { get; }

    public ReadOnlySpan<ArenaActorMarker> Actors => this.actors.AsSpan(0, this.actorCount);

    public ReadOnlySpan<ArenaWaymarkMarker> Waymarks => this.waymarks.AsSpan(0, this.waymarkCount);

    public ReadOnlySpan<ArenaTargetMarker> TargetMarkers =>
        this.targetMarkers.AsSpan(0, this.targetMarkerCount);

    internal int ActorCapacity => this.actors.Length;

    internal void Begin(long timestampMilliseconds)
    {
        this.TimestampMilliseconds = timestampMilliseconds;
        this.actorCount = 0;
        this.waymarkCount = 0;
        this.targetMarkerCount = 0;
    }

    internal void AddActor(in ArenaActorMarker marker)
    {
        if (this.actorCount >= this.actors.Length)
        {
            throw new ArgumentException("Arena scene actor capacity is insufficient.");
        }

        this.actors[this.actorCount++] = marker;
    }

    internal void AddWaymark(in ArenaWaymarkMarker marker)
    {
        if (this.waymarkCount >= this.waymarks.Length)
        {
            throw new InvalidOperationException("Arena scene cannot contain more than eight Waymarks.");
        }

        this.waymarks[this.waymarkCount++] = marker;
    }

    internal void AddTargetMarker(in ArenaTargetMarker marker)
    {
        if (this.targetMarkerCount >= this.targetMarkers.Length)
        {
            throw new InvalidOperationException(
                $"Arena scene cannot contain more than {TargetMarkerTimelineBuilder.MarkerCount} Target Markers.");
        }

        this.targetMarkers[this.targetMarkerCount++] = marker;
    }
}

public sealed class ArenaSceneBuilder
{
    private readonly ActorStateResolver actorStates;
    private readonly WaymarkStateResolver waymarkStates;
    private readonly TargetMarkerStateResolver targetMarkerStates;
    private readonly ArenaProjection projection;
    private readonly IReadOnlySet<int> renderableActorIds;
    private readonly ResolvedActorState[] actorStateBuffer;

    public ArenaSceneBuilder(PullRecord record, ArenaProjection projection)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(projection);

        this.actorStates = new ActorStateResolver(record);
        this.waymarkStates = new WaymarkStateResolver(record);
        this.targetMarkerStates = new TargetMarkerStateResolver(record);
        this.projection = projection;
        this.actorStateBuffer = new ResolvedActorState[this.actorStates.ActorCount];
        this.renderableActorIds = projection.RenderableActorIds
            ?? ArenaActorVisibility.BuildRenderableActorIds(record);
    }

    public ArenaProjection Projection => this.projection;

    public ArenaRenderScene CreateScene() =>
        new(this.actorStates.ActorCount, this.projection);

    public void Build(long timestampMilliseconds, ArenaRenderScene destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.ActorCapacity < this.actorStates.ActorCount)
        {
            throw new ArgumentException("Arena scene was created with insufficient actor capacity.", nameof(destination));
        }

        destination.Begin(timestampMilliseconds);
        var actorCount = this.actorStates.ResolveAll(timestampMilliseconds, this.actorStateBuffer);
        for (var index = 0; index < actorCount; index++)
        {
            ref readonly var state = ref this.actorStateBuffer[index];
            if (!state.IsTargetable
                || !ArenaActorVisibility.TryGetMarkerKind(
                    state.Actor,
                    this.renderableActorIds,
                    out var kind))
            {
                continue;
            }

            destination.AddActor(new ArenaActorMarker(
                state.Actor,
                kind,
                this.projection.Project(state.X, state.Z),
                ArenaProjection.ProjectFacing(state.Rotation),
                state.X,
                state.Y,
                state.Z,
                state.HitboxRadius,
                state.CurrentHp,
                state.MaxHp,
                state.IsDead,
                state.IsTargetable,
                state.IsOmnidirectional));
        }

        var waymarks = this.waymarkStates.Resolve(timestampMilliseconds);
        for (var waymarkIndex = 0; waymarkIndex < 8; waymarkIndex++)
        {
            var id = (WaymarkId)waymarkIndex;
            for (var stateIndex = 0; stateIndex < waymarks.Length; stateIndex++)
            {
                ref readonly var waymark = ref waymarks[stateIndex];
                if (waymark.Id != id || !waymark.Active)
                {
                    continue;
                }

                destination.AddWaymark(new ArenaWaymarkMarker(
                    waymark.Id,
                    this.projection.Project(waymark.X, waymark.Z),
                    waymark.X,
                    waymark.Y,
                    waymark.Z));
                break;
            }
        }

        var targetMarkers = this.targetMarkerStates.Resolve(timestampMilliseconds);
        foreach (ref readonly var targetMarker in targetMarkers)
        {
            if (targetMarker.TargetStableActorId is not { } stableActorId)
            {
                continue;
            }

            for (var stateIndex = 0; stateIndex < actorCount; stateIndex++)
            {
                ref readonly var state = ref this.actorStateBuffer[stateIndex];
                if (state.Actor.StableActorId != stableActorId
                    || !state.IsTargetable
                    || !ArenaActorVisibility.TryGetMarkerKind(
                        state.Actor,
                        this.renderableActorIds,
                        out _))
                {
                    continue;
                }

                destination.AddTargetMarker(new ArenaTargetMarker(
                    targetMarker.Id,
                    stableActorId,
                    this.projection.Project(state.X, state.Z)));
                break;
            }
        }
    }
}

internal static class ArenaActorVisibility
{
    public static HashSet<int> BuildRenderableActorIds(PullRecord record)
    {
        var actorsById = new Dictionary<int, ActorRecord>(record.Actors.Length);
        var playerOwnerIds = new HashSet<ulong>();
        var result = new HashSet<int>();
        foreach (var actor in record.Actors)
        {
            actorsById.Add(actor.StableActorId, actor);
            if (actor.ObjectKind == "Pc")
            {
                result.Add(actor.StableActorId);
                playerOwnerIds.Add(actor.EntityId);
                playerOwnerIds.Add(actor.GameObjectId);
            }
        }

        foreach (var frame in record.Frames)
        {
            foreach (var actorState in frame.Actors)
            {
                if (actorState.IsTargetable
                    && actorsById.TryGetValue(actorState.StableActorId, out var actor)
                    && actor.ObjectKind == "BattleNpc"
                    && (actor.OwnerId == 0 || !playerOwnerIds.Contains(actor.OwnerId)))
                {
                    result.Add(actor.StableActorId);
                }
            }
        }

        return result;
    }

    public static bool TryGetMarkerKind(
        ActorRecord actor,
        IReadOnlySet<int> renderableActorIds,
        out ArenaActorMarkerKind kind)
    {
        if (actor.ObjectKind == "Pc")
        {
            kind = ArenaActorMarkerKind.Player;
            return true;
        }

        if (actor.ObjectKind == "BattleNpc" && renderableActorIds.Contains(actor.StableActorId))
        {
            kind = ArenaActorMarkerKind.BattleNpc;
            return true;
        }

        kind = default;
        return false;
    }
}
