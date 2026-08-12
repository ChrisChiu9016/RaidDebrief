using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayPreviousPullLoadingTests
{
    [Fact]
    public void FinalizingPullWaitsInsteadOfSelectingOlderCompletedPull()
    {
        var previous = CreateRecord(Guid.Parse("62e7834c-9b2f-46dc-b24c-26d1a667afbd"));
        var finalizingId = Guid.Parse("ab3e9467-b84c-43c3-b143-b2e5313ea805");
        var decision = ReplayWindow.ResolveRuntimeSource(
            new ReplaySourceSnapshot(
                FinalizationGeneration: 2,
                FinalizationState: ReplaySourceFinalizationState.Finalizing,
                FinalizationCaptureId: finalizingId,
                FinalizationError: null,
                CompletedGeneration: 1,
                LastCompletedPull: previous));

        Assert.Equal(RuntimeReplaySourceDecisionKind.WaitForFinalization, decision.Kind);
        Assert.Equal(2, decision.SourceGeneration);
        Assert.Null(decision.Record);
        Assert.False(ReplayWindow.IsRuntimeLoadCurrent(
            decision,
            sourceGeneration: 1,
            captureId: previous.CaptureId));
        Assert.Contains(finalizingId.ToString(), decision.Message, StringComparison.Ordinal);
        Assert.Contains("不會載入較舊", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedPullSelectsPreservedRecordWithoutMisreportingItAsLatest()
    {
        var previous = CreateRecord(Guid.Parse("5c0a294d-ecc3-4b96-913d-f02ec33bc739"));
        var failedId = Guid.Parse("7db7e437-f357-4d18-9280-af127d99a97d");
        var decision = ReplayWindow.ResolveRuntimeSource(
            new ReplaySourceSnapshot(
                FinalizationGeneration: 2,
                FinalizationState: ReplaySourceFinalizationState.Failed,
                FinalizationCaptureId: failedId,
                FinalizationError: "invalid action target",
                CompletedGeneration: 1,
                LastCompletedPull: previous));

        Assert.Equal(RuntimeReplaySourceDecisionKind.LoadPreviousAfterFailure, decision.Kind);
        Assert.Same(previous, decision.Record);
        Assert.Contains("previous valid", decision.SourceDetail, StringComparison.Ordinal);
        Assert.Contains(failedId.ToString(), decision.SourceDetail, StringComparison.Ordinal);
        Assert.Contains("完成或驗證失敗", decision.Message, StringComparison.Ordinal);
        Assert.Contains(previous.CaptureId.ToString(), decision.Message, StringComparison.Ordinal);
        Assert.True(ReplayWindow.IsRuntimeLoadCurrent(
            decision,
            sourceGeneration: 2,
            captureId: previous.CaptureId));
        Assert.False(ReplayWindow.IsRuntimeLoadCurrent(
            decision,
            sourceGeneration: 1,
            captureId: previous.CaptureId));
    }

    [Fact]
    public void EmptyRuntimeSourceNeverFallsBackToDisk()
    {
        var decision = ReplayWindow.ResolveRuntimeSource(default);

        Assert.Equal(RuntimeReplaySourceDecisionKind.Empty, decision.Kind);
        Assert.Null(decision.Record);
        Assert.Contains("不會自動從 disk 恢復", decision.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewerRequestSupersedesOlderBackgroundRuntimeBuild(
        bool useDeveloperTestSource)
    {
        var newerMode = useDeveloperTestSource
            ? ReplaySourceMode.DeveloperTestFixture
            : ReplaySourceMode.RuntimeLastCompletedPull;
        var first = CreateRecord(Guid.Parse("f6ee424c-583c-4714-a013-af74e14c34ee"));
        var second = CreateRecord(Guid.Parse("fb26cb22-d2de-49d7-b12c-b1fcc931159c"));
        using var firstStarted = new ManualResetEventSlim();
        using var allowFirst = new ManualResetEventSlim();
        using var firstFinished = new ManualResetEventSlim();
        var coordinator = new ReplayLoadCoordinator(record =>
        {
            if (record.CaptureId == first.CaptureId)
            {
                firstStarted.Set();
                allowFirst.Wait();
            }

            var session = new ReplaySession(record);
            if (record.CaptureId == first.CaptureId)
            {
                firstFinished.Set();
            }

            return session;
        });

        coordinator.Start(
            first,
            ReplaySourceMode.RuntimeLastCompletedPull,
            sourceGeneration: 1,
            "first runtime source",
            "first loaded");
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        coordinator.Start(
            second,
            newerMode,
            sourceGeneration: 2,
            "newer source",
            "newer loaded");
        var completion = WaitForCompletion(coordinator);

        Assert.Null(completion.Error);
        Assert.Equal(newerMode, completion.Mode);
        Assert.Equal(2, completion.SourceGeneration);
        Assert.Equal(second.CaptureId, completion.CaptureId);
        Assert.Equal(second.CaptureId, Assert.IsType<ReplaySession>(completion.Session).Record.CaptureId);

        allowFirst.Set();
        Assert.True(firstFinished.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.TryTakeCompleted(out _));
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
        var startedAtUtc = DateTimeOffset.Parse("2026-08-10T00:00:00Z");
        return new PullRecord
        {
            Features = CaptureFeatures.All,
            CaptureId = captureId,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = startedAtUtc.AddSeconds(1),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors =
            [
                new ActorRecord
                {
                    StableActorId = 1,
                    Name = "Player 1",
                    ObjectKind = "Pc",
                    EntityId = 0x10000001,
                    GameObjectId = 0x10000001,
                    BaseId = 0,
                    ClassJobId = 19,
                    Level = 100,
                },
            ],
            Frames =
            [
                CreateFrame(0, 0),
                CreateFrame(1_000, 10),
            ],
        };
    }

    private static PositionFrame CreateFrame(long timestampMilliseconds, float position) => new()
    {
        TimestampMilliseconds = timestampMilliseconds,
        Actors =
        [
            new ActorStateSample
            {
                StableActorId = 1,
                X = position,
                Y = 0,
                Z = position,
                Rotation = 0,
                HitboxRadius = 1,
                CurrentHp = 100,
                MaxHp = 100,
                IsDead = false,
                IsTargetable = true,
            },
        ],
    };
}
