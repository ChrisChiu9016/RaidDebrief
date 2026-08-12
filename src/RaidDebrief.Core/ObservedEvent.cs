namespace RaidDebrief.Core;

public enum ObservedEventType
{
    CastStarted,
    CastEnded,
    CastInterrupted,
    Death,
    AliveTransition,
    StatusGained,
    StatusRefreshed,
    StatusLost,
    InCombatChanged,
    DutyStarted,
    DutyWiped,
    DutyRecommenced,
    DutyCompleted,
    ActorSpawned,
    ActorDespawned,
}

public enum ObservedEventSource
{
    PolledCastState,
    PolledActorState,
    PolledStatusState,
    PolledConditionState,
    DutyState,
}

public sealed record ObservedEvent
{
    public required long TimestampMilliseconds { get; init; }

    public required ObservedEventType Type { get; init; }

    public required ObservedEventSource Source { get; init; }

    public int? StableActorId { get; init; }

    public uint? ActionId { get; init; }

    public uint? StatusId { get; init; }

    public ulong? RelatedObjectId { get; init; }

    public bool? State { get; init; }
    /// <summary>
    /// Current cast progress observed when a CastStarted event was recorded.
    /// </summary>
    public float? CurrentCastTime { get; init; }

    /// <summary>
    /// Total adjusted cast duration observed when a CastStarted event was recorded.
    /// </summary>
    public float? TotalCastTime { get; init; }

    /// <summary>
    /// Remaining status duration observed for StatusGained or StatusRefreshed.
    /// </summary>
    public float? StatusRemainingTime { get; init; }

    public ushort? StatusParam { get; init; }
}

public readonly record struct PolledStatusObservation(
    uint StatusId,
    ulong SourceObjectId,
    float RemainingTime = 0,
    ushort Param = 0)
{
    public bool HasSameIdentity(in PolledStatusObservation other) =>
        this.StatusId == other.StatusId
        && this.SourceObjectId == other.SourceObjectId;
}

public readonly record struct PolledActorObservation(
    int StableActorId,
    ulong GameObjectId,
    bool IsDead,
    bool IsCasting,
    uint CastActionId,
    ulong CastTargetGameObjectId,
    float CurrentCastTime,
    float TotalCastTime,
    ReadOnlyMemory<PolledStatusObservation> Statuses);
