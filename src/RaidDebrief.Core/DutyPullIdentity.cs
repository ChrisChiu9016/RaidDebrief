namespace RaidDebrief.Core;

public enum CaptureMode
{
    Unknown,
    AutomaticPull,
    ManualDeveloper,
}

public sealed record DutyPullIdentity
{
    public required Guid DutyRunId { get; init; }

    public required uint ContentFinderConditionId { get; init; }

    public required string DutyName { get; init; }

    public required string DutyRunName { get; init; }

    public required DateTimeOffset DutyEnteredAtUtc { get; init; }

    public required int PullOrdinalWithinDutyRun { get; init; }
}
