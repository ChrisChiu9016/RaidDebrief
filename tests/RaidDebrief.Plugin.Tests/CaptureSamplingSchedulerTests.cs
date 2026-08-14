using System;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class CaptureSamplingSchedulerTests
{
    [Fact]
    public void FirstSampleIsImmediateAndCadenceUsesAbsoluteGrid()
    {
        var scheduler = new CaptureSamplingScheduler(100);

        CommitExpectedSample(scheduler, timestampMilliseconds: 0, expectedGaps: 0);
        Assert.False(scheduler.Prepare(1).ShouldSample);
        Assert.False(scheduler.Prepare(99).ShouldSample);
        CommitExpectedSample(scheduler, timestampMilliseconds: 100, expectedGaps: 0);
        Assert.False(scheduler.Prepare(199).ShouldSample);
        CommitExpectedSample(scheduler, timestampMilliseconds: 200, expectedGaps: 0);
    }

    [Fact]
    public void LowFrameRateRecordsOneRealSampleAndReportsMissingIntervals()
    {
        var scheduler = new CaptureSamplingScheduler(100);

        CommitExpectedSample(scheduler, timestampMilliseconds: 100, expectedGaps: 0);
        CommitExpectedSample(scheduler, timestampMilliseconds: 550, expectedGaps: 4);
        Assert.False(scheduler.Prepare(599).ShouldSample);
        CommitExpectedSample(scheduler, timestampMilliseconds: 600, expectedGaps: 0);
    }

    [Fact]
    public void CancelledPreparationDoesNotAdvanceCadenceOrGapBaseline()
    {
        var scheduler = new CaptureSamplingScheduler(100);
        CommitExpectedSample(scheduler, timestampMilliseconds: 0, expectedGaps: 0);

        var cancelled = scheduler.Prepare(100);
        Assert.True(cancelled.ShouldSample);
        scheduler.Cancel(cancelled);

        CommitExpectedSample(scheduler, timestampMilliseconds: 101, expectedGaps: 0);
    }

    [Fact]
    public void SchedulerRejectsNonMonotonicTimeAndOverlappingPreparation()
    {
        var scheduler = new CaptureSamplingScheduler(100);
        var pending = scheduler.Prepare(10);

        Assert.Throws<InvalidOperationException>(() => scheduler.Prepare(10));
        scheduler.Commit(pending);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Prepare(9));
    }

    [Fact]
    public void ClosedProbeAndIdleCaptureNeverRequestFullScan()
    {
        var coordinator = new FrameworkScanCoordinator(100);

        for (var timestamp = 0; timestamp < 1_000; timestamp += 10)
        {
            var decision = coordinator.Decide(
                captureSampleRequested: false,
                captureIsRecording: false,
                probeRefreshEnabled: false,
                timestamp);
            Assert.False(decision.ShouldScan);
        }
    }

    [Fact]
    public void ProbeOnlyRefreshesAtTenHertzWithoutCreatingCaptureSamples()
    {
        var coordinator = new FrameworkScanCoordinator(100);

        var first = coordinator.Decide(false, false, true, 0);
        Assert.True(first.ShouldScan);
        Assert.True(first.ProbePreparation.ShouldSample);
        coordinator.CompleteProbeScan(first);

        Assert.False(coordinator.Decide(false, false, true, 99).ShouldScan);
        var second = coordinator.Decide(false, false, true, 100);
        Assert.True(second.ShouldScan);
        Assert.True(second.ProbePreparation.ShouldSample);
        coordinator.CompleteProbeScan(second);
    }

    [Fact]
    public void CaptureSampleOwnsScanAndRecordingDoesNotAddProbeScans()
    {
        var coordinator = new FrameworkScanCoordinator(100);

        var captureSample = coordinator.Decide(true, true, true, 0);
        Assert.True(captureSample.ShouldScan);
        Assert.False(captureSample.ProbePreparation.ShouldSample);

        var betweenSamples = coordinator.Decide(false, true, true, 50);
        Assert.False(betweenSamples.ShouldScan);

        var afterCapture = coordinator.Decide(false, false, true, 60);
        Assert.True(afterCapture.ShouldScan);
        coordinator.CompleteProbeScan(afterCapture);
    }

    [Fact]
    public void FailedProbeScanRetriesWithoutAdvancingCadence()
    {
        var coordinator = new FrameworkScanCoordinator(100);
        var failed = coordinator.Decide(false, false, true, 0);
        Assert.True(failed.ShouldScan);
        coordinator.CancelProbeScan(failed);

        var retry = coordinator.Decide(false, false, true, 1);
        Assert.True(retry.ShouldScan);
        coordinator.CompleteProbeScan(retry);
    }

    [Fact]
    public void ClosedProbeCoordinatorPathAllocatesNothingAfterWarmup()
    {
        var coordinator = new FrameworkScanCoordinator(100);
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            _ = coordinator.Decide(false, false, false, iteration);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            _ = coordinator.Decide(false, false, false, iteration);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void CommitExpectedSample(
        CaptureSamplingScheduler scheduler,
        long timestampMilliseconds,
        long expectedGaps)
    {
        var preparation = scheduler.Prepare(timestampMilliseconds);
        Assert.True(preparation.ShouldSample);
        Assert.Equal(timestampMilliseconds, preparation.TimestampMilliseconds);
        Assert.Equal(expectedGaps, preparation.Gaps);
        scheduler.Commit(preparation);
    }
}
