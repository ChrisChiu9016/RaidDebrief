using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayCombatGateTests
{
    [Fact]
    public void CombatBlocksOpenWithoutCreatingAHideTransition()
    {
        var gate = new ReplayCombatGate();

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen: false,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        Assert.False(decision.CanOpen);
        Assert.Equal(ReplayCombatAction.None, decision.Action);
        Assert.True(gate.InCombat);
        Assert.False(gate.CanOpen);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CombatHidesAnyVisiblePlayingOrLoadingReplayWhenEnabled(
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

        Assert.False(decision.CanOpen);
        Assert.Equal(ReplayCombatAction.HideAndPause, decision.Action);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DisabledAutoCloseKeepsWindowButSuspendsCombatWork(
        bool isPlaying,
        bool hasPendingLoad)
    {
        var gate = new ReplayCombatGate();

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying,
            hasPendingLoad,
            closeReplayOnCombatStart: false);

        Assert.False(decision.CanOpen);
        Assert.Equal(ReplayCombatAction.Pause, decision.Action);
    }

    [Fact]
    public void DisabledAutoCloseLeavesAnAlreadyPausedWindowVisible()
    {
        var gate = new ReplayCombatGate();

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: false);

        Assert.False(decision.CanOpen);
        Assert.Equal(ReplayCombatAction.None, decision.Action);
    }

    [Fact]
    public void EnablingAutoCloseDuringCombatHidesTheVisibleWindow()
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

        Assert.Equal(ReplayCombatAction.HideAndPause, decision.Action);
    }

    [Fact]
    public void CombatEndAllowsExplicitOpenWithoutRequestingAutomaticReopen()
    {
        var gate = new ReplayCombatGate();
        var hidden = gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: true,
            hasPendingLoad: true,
            closeReplayOnCombatStart: true);

        var combatEnded = gate.Observe(
            inCombat: false,
            isReplayOpen: false,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        Assert.Equal(ReplayCombatAction.HideAndPause, hidden.Action);
        Assert.True(combatEnded.CanOpen);
        Assert.Equal(ReplayCombatAction.None, combatEnded.Action);
        Assert.False(gate.InCombat);
        Assert.True(gate.CanOpen);
    }

    [Fact]
    public void RepeatedCombatObservationDoesNotCreateWorkAfterReplayIsHidden()
    {
        var gate = new ReplayCombatGate();
        gate.Observe(
            inCombat: true,
            isReplayOpen: true,
            isPlaying: true,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        var decision = gate.Observe(
            inCombat: true,
            isReplayOpen: false,
            isPlaying: false,
            hasPendingLoad: false,
            closeReplayOnCombatStart: true);

        Assert.False(decision.CanOpen);
        Assert.Equal(ReplayCombatAction.None, decision.Action);
    }
}
