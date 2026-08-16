namespace RaidDebrief.Core;

public static class CaptureSchema
{
    public const int CurrentVersion = 1;
}

[Flags]
public enum CaptureFeatures
{
    None = 0,
    ActorOwnerId = 1 << 0,
    HitboxRadius = 1 << 1,
    TargetMarkers = 1 << 2,
    // Kept at the legacy value because existing JSON serializes this flag name as "all".
    All = ActorOwnerId | HitboxRadius | TargetMarkers,
    TargetMarkerCanonicalOrder = 1 << 3,
    OmnidirectionalState = 1 << 4,
    PartyMembership = 1 << 5,
    CastTiming = 1 << 6,
    StatusTiming = 1 << 7,
    ActionEffectCapture = 1 << 8,
    ActionNameSnapshot = 1 << 9,
    BarrierState = 1 << 10,
    DutyRunIdentity = 1 << 11,
    // Preserve the legacy named value because existing JSON serializes this exact flag name.
    Current = All | TargetMarkerCanonicalOrder | OmnidirectionalState,
    ReplayPresentation = Current
        | PartyMembership
        | CastTiming
        | StatusTiming
        | ActionEffectCapture
        | ActionNameSnapshot
        | BarrierState,
}

public sealed record PullRecord
{
    public int SchemaVersion { get; init; } = CaptureSchema.CurrentVersion;

    public CaptureFeatures Features { get; init; }

    public required Guid CaptureId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required DateTimeOffset EndedAtUtc { get; init; }

    public required uint TerritoryType { get; init; }

    public required uint MapId { get; init; }

    public required uint Instance { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CaptureMode? CaptureMode { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DutyPullIdentity? DutyRun { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PullEndReason? EndReason { get; init; }

    public required ActorRecord[] Actors { get; init; }

    public required PositionFrame[] Frames { get; init; }

    public ObservedEvent[] Events { get; init; } = [];

    public WaymarkFrame[] WaymarkFrames { get; init; } = [];

    public ActionEffectRecord[] ActionEffects { get; init; } = [];
    public RecordedActionName[] ActionNames { get; init; } = [];

    public TargetMarkerFrame[] TargetMarkerFrames { get; init; } = [];
}
public enum ActionNameSource
{
    StaticExcel,
    RuntimeRsv,
    UiObserved,
}

public sealed record RecordedActionName
{
    public required uint ActionId { get; init; }

    public required string Name { get; init; }

    public required string Language { get; init; }

    public required ActionNameSource Source { get; init; }
}


public sealed record ActorRecord
{
    public required int StableActorId { get; init; }

    public required string Name { get; init; }

    public required string ObjectKind { get; init; }

    public required uint EntityId { get; init; }

    public required ulong GameObjectId { get; init; }

    public ulong OwnerId { get; init; }

    public required uint BaseId { get; init; }

    public required uint ClassJobId { get; init; }
    /// <summary>
    /// Zero-based party-list order recorded with the Pull, or null when the Actor was not a party member
    /// or the Capture predates party-membership recording.
    /// </summary>
    public int? PartyIndex { get; init; }

    public required byte Level { get; init; }
}

public sealed record PositionFrame
{
    public required long TimestampMilliseconds { get; init; }

    public required ActorStateSample[] Actors { get; init; }
}

public sealed record ActorStateSample
{
    public required int StableActorId { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Z { get; init; }

    public required float Rotation { get; init; }

    public float HitboxRadius { get; init; }

    public required uint CurrentHp { get; init; }

    public required uint MaxHp { get; init; }

    /// <summary>
    /// Recorded barrier strength as a percentage of maximum HP.
    /// Meaningful only when the Pull has the BarrierState capture feature.
    /// </summary>
    public byte BarrierPercentage { get; init; }

    public required bool IsDead { get; init; }

    public required bool IsTargetable { get; init; }

    public bool IsOmnidirectional { get; init; }
}
