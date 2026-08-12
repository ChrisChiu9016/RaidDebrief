namespace RaidDebrief.Plugin;

internal enum ReplayCombatAction
{
    None,
    Pause,
    HideAndPause,
}

internal readonly record struct ReplayCombatDecision(
    bool CanOpen,
    ReplayCombatAction Action);
internal readonly record struct ReplayFramePolicy(
    bool ShouldDraw,
    bool ShouldAdvance)
{
    public static ReplayFramePolicy Resolve(
        bool isOpen,
        bool inCombat,
        bool isPlaying,
        bool closeReplayOnCombatStart)
    {
        var shouldDraw = isOpen && (!inCombat || !closeReplayOnCombatStart);
        return new ReplayFramePolicy(
            shouldDraw,
            shouldDraw && !inCombat && isPlaying);
    }
}


internal sealed class ReplayCombatGate
{
    private bool inCombat;

    public bool InCombat => this.inCombat;

    public bool CanOpen => !this.inCombat;

    public ReplayCombatDecision Observe(
        bool inCombat,
        bool isReplayOpen,
        bool isPlaying,
        bool hasPendingLoad,
        bool closeReplayOnCombatStart)
    {
        this.inCombat = inCombat;
        var action = inCombat && (isReplayOpen || isPlaying || hasPendingLoad)
            ? closeReplayOnCombatStart
                ? ReplayCombatAction.HideAndPause
                : isPlaying || hasPendingLoad
                    ? ReplayCombatAction.Pause
                    : ReplayCombatAction.None
            : ReplayCombatAction.None;
        return new ReplayCombatDecision(!inCombat, action);
    }
}
