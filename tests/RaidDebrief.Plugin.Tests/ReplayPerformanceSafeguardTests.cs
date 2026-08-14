using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayPerformanceSafeguardTests
{
    [Theory]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(true, false, false, true, false)]
    [InlineData(true, false, true, true, true)]
    public void FramePolicyKeepsCombatWindowVisibleWithoutAdvancing(
        bool isOpen,
        bool inCombat,
        bool isPlaying,
        bool expectedDraw,
        bool expectedAdvance)
    {
        var policy = ReplayFramePolicy.Resolve(
            isOpen,
            inCombat,
            isPlaying);

        Assert.Equal(expectedDraw, policy.ShouldDraw);
        Assert.Equal(expectedAdvance, policy.ShouldAdvance);
    }


    [Fact]
    public void PerFramePolicyCalculationAllocatesNothing()
    {
        _ = ReplayFramePolicy.Resolve(
            isOpen: true,
            inCombat: false,
            isPlaying: true);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var checksum = 0;
        for (var index = 0; index < 100_000; index++)
        {
            var policy = ReplayFramePolicy.Resolve(
                isOpen: (index & 1) == 0,
                inCombat: (index & 2) != 0,
                isPlaying: (index & 4) != 0);
            checksum += policy.ShouldDraw ? 1 : 0;
            checksum += policy.ShouldAdvance ? 1 : 0;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(checksum > 0);
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void NewLoadCancelsSupersededSessionConstruction()
    {
        var first = CreateRecord(Guid.Parse("a05260d5-b53f-4aca-9778-9b0b1b98636e"));
        var second = CreateRecord(Guid.Parse("35651950-1275-4fa2-a2c0-ff91e17b6179"));
        using var firstConstructionStarted = new ManualResetEventSlim();
        using var firstCancellationObserved = new ManualResetEventSlim();
        using var coordinator = new ReplayLoadCoordinator((record, cancellationToken) =>
        {
            if (record.CaptureId == first.CaptureId)
            {
                firstConstructionStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                firstCancellationObserved.Set();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ReplaySession(record);
        });

        coordinator.Start(
            first,
            ReplaySourceMode.RuntimeLastCompletedPull,
            sourceGeneration: 1,
            "first runtime source",
            "first loaded");
        Assert.True(firstConstructionStarted.Wait(TimeSpan.FromSeconds(5)));

        coordinator.Start(
            second,
            ReplaySourceMode.RuntimeLastCompletedPull,
            sourceGeneration: 2,
            "second runtime source",
            "second loaded");
        Assert.True(firstCancellationObserved.Wait(TimeSpan.FromSeconds(5)));
        var completion = WaitForCompletion(coordinator);

        Assert.Null(completion.Error);
        Assert.Equal(2, completion.SourceGeneration);
        Assert.Equal(second.CaptureId, completion.CaptureId);
        Assert.Equal(second.CaptureId, Assert.IsType<ReplaySession>(completion.Session).Record.CaptureId);
    }

    [Fact]
    public void DisposeCancelsPendingWorkAndPreventsLaterAdoption()
    {
        var record = CreateRecord(Guid.Parse("b14061e4-b222-44c2-a0a6-65b6715db3aa"));
        using var constructionStarted = new ManualResetEventSlim();
        using var cancellationObserved = new ManualResetEventSlim();
        using var constructionExited = new ManualResetEventSlim();
        var coordinator = new ReplayLoadCoordinator((pull, cancellationToken) =>
        {
            try
            {
                constructionStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                cancellationObserved.Set();
                cancellationToken.ThrowIfCancellationRequested();
                return new ReplaySession(pull);
            }
            finally
            {
                constructionExited.Set();
            }
        });
        coordinator.Start(
            record,
            ReplaySourceMode.RuntimeLastCompletedPull,
            sourceGeneration: 1,
            "runtime source",
            "loaded");
        Assert.True(constructionStarted.Wait(TimeSpan.FromSeconds(5)));

        coordinator.Dispose();

        Assert.True(cancellationObserved.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(constructionExited.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.TryTakeCompleted(out _));
        Assert.False(coordinator.IsLoading);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Start(
            record,
            ReplaySourceMode.RuntimeLastCompletedPull,
            sourceGeneration: 2,
            "disposed source",
            "must not load"));
    }

    private static ReplayLoadCompletion WaitForCompletion(ReplayLoadCoordinator coordinator)
    {
        ReplayLoadCompletion completion = default;
        var completed = SpinWait.SpinUntil(
            () => coordinator.TryTakeCompleted(out completion),
            TimeSpan.FromSeconds(5));
        Assert.True(completed, "Replay load did not complete within five seconds.");
        return completion;
    }

    private static PullRecord CreateRecord(Guid captureId)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-10T00:00:00Z");
        return new PullRecord
        {
            Features = CaptureFeatures.All,
            CaptureId = captureId,
            StartedAtUtc = timestamp,
            EndedAtUtc = timestamp,
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors = [],
            Frames = [],
        };
    }
}
