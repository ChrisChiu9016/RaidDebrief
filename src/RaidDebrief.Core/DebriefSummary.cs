namespace RaidDebrief.Core;

public readonly record struct DebriefReplayWindow(
    long StartTimestampMilliseconds,
    long EndTimestampMilliseconds)
{
    public long DurationMilliseconds => this.EndTimestampMilliseconds - this.StartTimestampMilliseconds;
}

public readonly record struct DebriefBossHp(
    int StableActorId,
    string ActorName,
    uint CurrentHp,
    uint MaxHp)
{
    public float Percentage => this.MaxHp == 0
        ? 0
        : this.CurrentHp * 100f / this.MaxHp;
}

public readonly record struct DebriefDeathEntry(
    long TimestampMilliseconds,
    int OriginalRecordedIndex,
    int StableActorId,
    string ActorName,
    uint ClassJobId);

public sealed record DebriefSummary
{
    public required Guid CaptureId { get; init; }

    public long? PullNumber { get; init; }

    public required long DurationMilliseconds { get; init; }

    public long? WipeTimestampMilliseconds { get; init; }

    public DebriefBossHp? BossHpAtEnd { get; init; }

    public DebriefDeathEntry? FirstDeath { get; init; }

    public required DebriefDeathEntry[] DeathSequence { get; init; }

    public required int UnresolvedDeathEventCount { get; init; }

    public DebriefReplayWindow? SuggestedReplayWindow { get; init; }
}
