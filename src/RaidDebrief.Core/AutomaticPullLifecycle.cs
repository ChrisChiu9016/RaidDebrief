namespace RaidDebrief.Core;

public enum AutomaticPullState
{
    Idle,
    Recording,
    Finalizing,
    Completed,
}

public enum AutomaticPullCommand
{
    None,
    StartRecording,
    Finalize,
}

public enum PullEndReason
{
    CombatEnded,
    DutyWiped,
    DutyCompleted,
    PluginReload,
    InstanceExited,
}

public sealed class AutomaticPullLifecycle
{
    public const long DefaultCombatEndDebounceMilliseconds = 3_000;

    private readonly long combatEndDebounceMilliseconds;
    private long lastTimestampMilliseconds = -1;
    private long? combatEndedAtMilliseconds;
    private bool isArmedForCombatStart;

    public AutomaticPullLifecycle(
        long combatEndDebounceMilliseconds = DefaultCombatEndDebounceMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(combatEndDebounceMilliseconds);
        this.combatEndDebounceMilliseconds = combatEndDebounceMilliseconds;
    }

    public AutomaticPullState State { get; private set; }

    public PullEndReason? LastEndReason { get; private set; }
    public bool IsArmedForCombatStart => this.isArmedForCombatStart;

    public long? CombatEndDeadlineMilliseconds => this.combatEndedAtMilliseconds is { } endedAt
        ? endedAt + this.combatEndDebounceMilliseconds
        : null;

    public AutomaticPullCommand Observe(long timestampMilliseconds, bool inCombat)
    {
        this.ValidateTimestamp(timestampMilliseconds);
        if (!inCombat)
        {
            this.isArmedForCombatStart = true;
        }


        if (this.State == AutomaticPullState.Completed && this.isArmedForCombatStart)
        {
            this.State = AutomaticPullState.Idle;
        }

        switch (this.State)
        {
            case AutomaticPullState.Idle when inCombat && this.isArmedForCombatStart:
                this.State = AutomaticPullState.Recording;
                this.LastEndReason = null;
                this.combatEndedAtMilliseconds = null;
                return AutomaticPullCommand.StartRecording;

            case AutomaticPullState.Recording when inCombat:
                this.combatEndedAtMilliseconds = null;
                return AutomaticPullCommand.None;

            case AutomaticPullState.Recording when this.combatEndedAtMilliseconds is null:
                this.combatEndedAtMilliseconds = timestampMilliseconds;
                return this.combatEndDebounceMilliseconds == 0
                    ? this.Finalize(PullEndReason.CombatEnded)
                    : AutomaticPullCommand.None;

            case AutomaticPullState.Recording
                when timestampMilliseconds - this.combatEndedAtMilliseconds
                    >= this.combatEndDebounceMilliseconds:
                return this.Finalize(PullEndReason.CombatEnded);

            default:
                return AutomaticPullCommand.None;
        }
    }

    public AutomaticPullCommand EndImmediately(
        long timestampMilliseconds,
        PullEndReason reason)
    {
        this.ValidateTimestamp(timestampMilliseconds);
        if (reason == PullEndReason.CombatEnded)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "CombatEnded must use the debounced observation path.");
        }

        return this.State == AutomaticPullState.Recording
            ? this.Finalize(reason)
            : AutomaticPullCommand.None;
    }

    public void MarkCompleted()
    {
        if (this.State != AutomaticPullState.Finalizing)
        {
            throw new InvalidOperationException(
                $"Cannot mark an automatic pull completed while lifecycle state is {this.State}.");
        }

        this.State = AutomaticPullState.Completed;
    }

    public void Reset()
    {
        this.State = AutomaticPullState.Idle;
        this.LastEndReason = null;
        this.combatEndedAtMilliseconds = null;
        this.isArmedForCombatStart = false;
    }

    private AutomaticPullCommand Finalize(PullEndReason reason)
    {
        this.State = AutomaticPullState.Finalizing;
        this.LastEndReason = reason;
        this.combatEndedAtMilliseconds = null;
        if (reason != PullEndReason.CombatEnded)
        {
            this.isArmedForCombatStart = false;
        }
        return AutomaticPullCommand.Finalize;
    }

    private void ValidateTimestamp(long timestampMilliseconds)
    {
        if (timestampMilliseconds < 0 || timestampMilliseconds < this.lastTimestampMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampMilliseconds),
                timestampMilliseconds,
                "Lifecycle timestamps must be non-negative and monotonic.");
        }

        this.lastTimestampMilliseconds = timestampMilliseconds;
    }
}
