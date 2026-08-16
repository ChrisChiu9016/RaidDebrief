using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class DutyRunTrackerTests
{
    [Fact]
    public void RecommencedPullsShareRunAndNewEntryStartsAnotherRun()
    {
        var times = new Queue<DateTimeOffset>(
        [
            DateTimeOffset.Parse("2026-08-16T07:43:26Z"),
            DateTimeOffset.Parse("2026-08-16T08:15:00Z"),
        ]);
        var tracker = new DutyRunTracker(
            () => times.Dequeue(),
            TimeZoneInfo.Utc);

        tracker.ObserveZoneInitialized(1_229, 1_003, "AAC Heavyweight M4");
        tracker.ObserveBoundState(true, 1_229, 1_003, "AAC Heavyweight M4");
        var first = Assert.IsType<DutyPullIdentity>(tracker.BeginPull());
        var second = Assert.IsType<DutyPullIdentity>(tracker.BeginPull());

        Assert.Equal(first.DutyRunId, second.DutyRunId);
        Assert.Equal(1, first.PullOrdinalWithinDutyRun);
        Assert.Equal(2, second.PullOrdinalWithinDutyRun);
        Assert.Equal(
            "AAC Heavyweight M4 · 2026-08-16 07:43:26",
            first.DutyRunName);

        tracker.ObserveBoundState(false, 129, 0, null);
        tracker.ObserveZoneInitialized(1_229, 1_003, "AAC Heavyweight M4");
        tracker.ObserveBoundState(true, 1_229, 1_003, "AAC Heavyweight M4");
        var nextEntry = Assert.IsType<DutyPullIdentity>(tracker.BeginPull());

        Assert.NotEqual(first.DutyRunId, nextEntry.DutyRunId);
        Assert.Equal(first.ContentFinderConditionId, nextEntry.ContentFinderConditionId);
        Assert.Equal(1, nextEntry.PullOrdinalWithinDutyRun);
        Assert.Equal(
            "AAC Heavyweight M4 · 2026-08-16 08:15:00",
            nextEntry.DutyRunName);
    }

    [Fact]
    public void ReloadInsideDutyConservativelyCreatesNewRun()
    {
        var firstTracker = new DutyRunTracker(
            () => DateTimeOffset.Parse("2026-08-16T07:43:26Z"),
            TimeZoneInfo.Utc);
        firstTracker.ObserveBoundState(true, 1_229, 1_003, "AAC Heavyweight M4");
        var beforeReload = Assert.IsType<DutyPullIdentity>(firstTracker.BeginPull());

        var reloadedTracker = new DutyRunTracker(
            () => DateTimeOffset.Parse("2026-08-16T07:50:00Z"),
            TimeZoneInfo.Utc);
        reloadedTracker.ObserveBoundState(true, 1_229, 1_003, "AAC Heavyweight M4");
        var afterReload = Assert.IsType<DutyPullIdentity>(reloadedTracker.BeginPull());

        Assert.NotEqual(beforeReload.DutyRunId, afterReload.DutyRunId);
        Assert.Equal(1, afterReload.PullOrdinalWithinDutyRun);
    }

    [Fact]
    public void DisconnectBoundaryConservativelyCreatesNewRun()
    {
        var times = new Queue<DateTimeOffset>(
        [
            DateTimeOffset.Parse("2026-08-16T07:43:26Z"),
            DateTimeOffset.Parse("2026-08-16T07:44:00Z"),
        ]);
        var tracker = new DutyRunTracker(
            () => times.Dequeue(),
            TimeZoneInfo.Utc);
        tracker.ObserveBoundState(true, 1_229, 1_003, "AAC Heavyweight M4");
        var beforeDisconnect = Assert.IsType<DutyPullIdentity>(tracker.BeginPull());

        tracker.ObserveBoundState(false, 1_229, 1_003, "AAC Heavyweight M4");
        tracker.ObserveBoundState(true, 1_229, 1_003, "AAC Heavyweight M4");
        var afterReconnect = Assert.IsType<DutyPullIdentity>(tracker.BeginPull());

        Assert.NotEqual(beforeDisconnect.DutyRunId, afterReconnect.DutyRunId);
        Assert.Equal(1, afterReconnect.PullOrdinalWithinDutyRun);
    }
}
