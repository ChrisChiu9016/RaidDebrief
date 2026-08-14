namespace RaidDebrief.Plugin;

internal enum ReplayCombatAction
{
    None,
    Pause,
    HideAndPause,
}

internal readonly record struct ReplayCombatDecision(
    ReplayCombatAction Action);
internal readonly record struct ReplayFramePolicy(
    bool ShouldDraw,
    bool ShouldAdvance)
{
    public static ReplayFramePolicy Resolve(
        bool isOpen,
        bool inCombat,
        bool isPlaying) =>
        new(
            ShouldDraw: isOpen,
            ShouldAdvance: isOpen && !inCombat && isPlaying);
}


internal sealed class ReplayCombatGate
{
    private bool inCombat;

    public bool InCombat => this.inCombat;


    public ReplayCombatDecision Observe(
        bool inCombat,
        bool isReplayOpen,
        bool isPlaying,
        bool hasPendingLoad,
        bool closeReplayOnCombatStart)
    {
        var enteredCombat = !this.inCombat && inCombat;
        this.inCombat = inCombat;
        var action = enteredCombat && (isReplayOpen || isPlaying || hasPendingLoad)
            ? closeReplayOnCombatStart
                ? ReplayCombatAction.HideAndPause
                : isPlaying || hasPendingLoad
                    ? ReplayCombatAction.Pause
                    : ReplayCombatAction.None
            : ReplayCombatAction.None;
        return new ReplayCombatDecision(action);
    }
}
