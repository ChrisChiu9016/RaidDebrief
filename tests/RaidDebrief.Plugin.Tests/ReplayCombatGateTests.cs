using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayCombatGateTests
{
    [Fact]
    public void OpeningDuringCombatDoesNotCreateAnotherCloseAction()
    {
        var gate = new ReplayCombatGate();
        gate.Observe(
            inCombat: false,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        var combatStarted = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);
        var reopenedDuringCombat = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: true,
            closeReplayOnCombatStart: true);

        Assert.Equal(ReplayCombatAction.HideAndPause, combatStarted.Action);
        Assert.Equal(ReplayCombatAction.None, reopenedDuringCombat.Action);
        Assert.True(gate.InCombat);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CombatEntryHidesVisiblePlayingOrLoadingReplayWhenEnabled(
        bool isReplayOpen,
        bool isPlaying,
        bool hasPendingLoad)
    {
        var gate = new ReplayCombatGate();
        gate.Observe(
            inCombat: false,
            isReplayOpen,
            isPlaying,
            hasPendingLoad,
            closeReplayOnCombatStart: true);

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen,
            isPlaying,
            hasPendingLoad,
            closeReplayOnCombatStart: true);

        Assert.Equal(ReplayCombatAction.HideAndPause, decision.Action);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DisabledAutoCloseKeepsWindowButSuspendsWorkOnCombatEntry(
        bool isPlaying,
        bool hasPendingLoad)
    {
        var gate = new ReplayCombatGate();
        gate.Observe(
            inCombat: false,
            isReplayOpen: true,
            isPlaying,
            hasPendingLoad,
            closeReplayOnCombatStart: false);

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying,
            hasPendingLoad,
            closeReplayOnCombatStart: false);

        Assert.Equal(ReplayCombatAction.Pause, decision.Action);
    }

    [Fact]
    public void EnablingAutoCloseMidCombatDoesNotHideWindow()
    {
        var gate = new ReplayCombatGate();
        gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: false);

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        Assert.Equal(ReplayCombatAction.None, decision.Action);
    }

    [Fact]
    public void LeavingAndReenteringCombatCreatesOneNewCloseAction()
    {
        var gate = new ReplayCombatGate();
        gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);
        var combatEnded = gate.Observe(
            inCombat: false,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);
        var combatRestarted = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        Assert.Equal(ReplayCombatAction.None, combatEnded.Action);
        Assert.Equal(ReplayCombatAction.HideAndPause, combatRestarted.Action);
    }
}
