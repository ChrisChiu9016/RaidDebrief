using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class AutomaticPullLifecycleTests
{
    [Fact]
    public void StartsOnceAndIgnoresRepeatedCombatObservations()
    {
        var lifecycle = new AutomaticPullLifecycle();

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(0, false));
        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(100, true));
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(200, true));
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(3_000, true));
        Assert.Equal(AutomaticPullState.Recording, lifecycle.State);
        Assert.Null(lifecycle.LastEndReason);
    }

    [Fact]
    public void CombatEndRequiresFullDebounceAndReentryCancelsPendingEnd()
    {
        var lifecycle = new AutomaticPullLifecycle();
        lifecycle.Observe(0, false);
        lifecycle.Observe(1, true);

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(1_000, false));
        Assert.Equal(4_000, lifecycle.CombatEndDeadlineMilliseconds);
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(2_500, true));
        Assert.Null(lifecycle.CombatEndDeadlineMilliseconds);

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(3_000, false));
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(5_999, false));
        Assert.Equal(AutomaticPullCommand.Finalize, lifecycle.Observe(6_000, false));
        Assert.Equal(AutomaticPullState.Finalizing, lifecycle.State);
        Assert.Equal(PullEndReason.CombatEnded, lifecycle.LastEndReason);
    }

    [Theory]
    [InlineData(PullEndReason.DutyWiped)]
    [InlineData(PullEndReason.DutyCompleted)]
    [InlineData(PullEndReason.PluginReload)]
    [InlineData(PullEndReason.InstanceExited)]
    public void ExplicitEndFinalizesImmediatelyAndOnlyOnce(PullEndReason reason)
    {
        var lifecycle = new AutomaticPullLifecycle();
        lifecycle.Observe(0, false);
        lifecycle.Observe(1, true);

        Assert.Equal(AutomaticPullCommand.Finalize, lifecycle.EndImmediately(100, reason));
        Assert.Equal(AutomaticPullState.Finalizing, lifecycle.State);
        Assert.Equal(reason, lifecycle.LastEndReason);
        Assert.Equal(AutomaticPullCommand.None, lifecycle.EndImmediately(101, reason));
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(102, false));
    }

    [Fact]
    public void CompletedPullReturnsToIdleAndStartsNextCombatCleanly()
    {
        var lifecycle = new AutomaticPullLifecycle(combatEndDebounceMilliseconds: 100);
        lifecycle.Observe(0, false);
        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(1, true));
        lifecycle.Observe(10, false);
        Assert.Equal(AutomaticPullCommand.Finalize, lifecycle.Observe(110, false));

        lifecycle.MarkCompleted();
        Assert.Equal(AutomaticPullState.Completed, lifecycle.State);
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(120, false));
        Assert.Equal(AutomaticPullState.Idle, lifecycle.State);

        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(200, true));
        Assert.Equal(AutomaticPullState.Recording, lifecycle.State);
        Assert.Null(lifecycle.LastEndReason);
        Assert.Null(lifecycle.CombatEndDeadlineMilliseconds);
    }

    [Fact]
    public void CompletedPullCanStartNextCombatOnFirstObservation()
    {
        var lifecycle = new AutomaticPullLifecycle(combatEndDebounceMilliseconds: 0);
        lifecycle.Observe(0, false);
        lifecycle.Observe(1, true);
        Assert.Equal(AutomaticPullCommand.Finalize, lifecycle.Observe(10, false));
        lifecycle.MarkCompleted();

        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(20, true));
        Assert.Equal(AutomaticPullState.Recording, lifecycle.State);
    }

    [Fact]
    public void DoesNotStartMidCombatUntilOutOfCombatHasBeenObserved()
    {
        var lifecycle = new AutomaticPullLifecycle();

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(0, true));
        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(100, true));
        Assert.Equal(AutomaticPullState.Idle, lifecycle.State);
        Assert.False(lifecycle.IsArmedForCombatStart);

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(200, false));
        Assert.True(lifecycle.IsArmedForCombatStart);
        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(300, true));
    }

    [Theory]
    [InlineData(PullEndReason.DutyWiped)]
    [InlineData(PullEndReason.DutyCompleted)]
    [InlineData(PullEndReason.InstanceExited)]
    public void ExplicitDutyEndRequiresCombatClearBeforeNextPull(PullEndReason reason)
    {
        var lifecycle = new AutomaticPullLifecycle();
        lifecycle.Observe(0, false);
        lifecycle.Observe(1, true);
        lifecycle.EndImmediately(100, reason);
        lifecycle.MarkCompleted();

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(101, true));
        Assert.Equal(AutomaticPullState.Completed, lifecycle.State);
        Assert.False(lifecycle.IsArmedForCombatStart);

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(102, false));
        Assert.Equal(AutomaticPullState.Idle, lifecycle.State);
        Assert.True(lifecycle.IsArmedForCombatStart);
        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(103, true));
    }

    [Fact]
    public void CombatClearDuringFinalizationRearmsNextPull()
    {
        var lifecycle = new AutomaticPullLifecycle();
        lifecycle.Observe(0, false);
        lifecycle.Observe(1, true);
        lifecycle.EndImmediately(100, PullEndReason.DutyWiped);

        Assert.Equal(AutomaticPullCommand.None, lifecycle.Observe(101, false));
        Assert.Equal(AutomaticPullState.Finalizing, lifecycle.State);
        Assert.True(lifecycle.IsArmedForCombatStart);

        lifecycle.MarkCompleted();
        Assert.Equal(AutomaticPullCommand.StartRecording, lifecycle.Observe(102, true));
    }

    [Fact]
    public void RejectsBackwardTimestampsAndCombatEndedAsImmediateReason()
    {
        var lifecycle = new AutomaticPullLifecycle();
        lifecycle.Observe(0, false);
        lifecycle.Observe(100, true);

        Assert.Throws<ArgumentOutOfRangeException>(() => lifecycle.Observe(99, true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => lifecycle.EndImmediately(100, PullEndReason.CombatEnded));
    }
}
