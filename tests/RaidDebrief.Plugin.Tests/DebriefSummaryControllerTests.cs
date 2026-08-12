using System;
using Newtonsoft.Json;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class DebriefSummaryControllerTests
{
    [Fact]
    public void OffersEveryValidatedWipeAfterCombatClears()
    {
        var controller = new DebriefSummaryController();
        var firstPull = CreateRecord("8a9854f8-510f-4ce4-a1b9-ee760d94bc50");
        var firstWipe = CreateSnapshot(1, firstPull, PullEndReason.DutyWiped);

        controller.Observe(firstWipe, inCombat: false, enabled: true);

        Assert.Equal(firstPull.CaptureId, controller.Pending?.Summary.CaptureId);
        controller.Observe(firstWipe, inCombat: false, enabled: true);
        Assert.Equal(firstPull.CaptureId, controller.Pending?.Summary.CaptureId);

        Assert.True(controller.TryTake(out var request));
        Assert.Equal(firstPull.CaptureId, request.CaptureId);
        Assert.Equal(1, request.SourceGeneration);
        Assert.Equal(new DebriefReplayWindow(40_000, 60_000), request.Window);
        Assert.Null(controller.Pending);

        controller.Observe(firstWipe, inCombat: true, enabled: true);
        var secondPull = CreateRecord("b86f3619-54a1-43ff-ab54-9d496843d1d5");
        var secondWipe = CreateSnapshot(2, secondPull, PullEndReason.DutyWiped);
        controller.Observe(secondWipe, inCombat: true, enabled: true);
        Assert.Null(controller.Pending);

        controller.Observe(secondWipe, inCombat: false, enabled: true);
        Assert.Equal(secondPull.CaptureId, controller.Pending?.Summary.CaptureId);
    }

    [Fact]
    public void NonWipeMismatchedSummaryAndCombatEntryNeverLeaveDebriefPending()
    {
        var controller = new DebriefSummaryController();
        var clear = CreateRecord("a712a958-c1b7-4bb3-99f9-b16a6d550f86");
        controller.Observe(
            CreateSnapshot(1, clear, PullEndReason.DutyCompleted),
            inCombat: false,
            enabled: true);
        Assert.Null(controller.Pending);

        var wipe = CreateRecord("1ee06776-05c9-4981-bfb2-927b5d71a772");
        controller.Observe(
            CreateSnapshot(
                2,
                wipe,
                PullEndReason.DutyWiped,
                summaryCaptureId: Guid.Parse("af968630-1f2c-4456-a4aa-3ac25f90b60d")),
            inCombat: false,
            enabled: true);
        Assert.Null(controller.Pending);

        controller.Observe(
            CreateSnapshot(3, wipe, PullEndReason.DutyWiped),
            inCombat: false,
            enabled: true);
        Assert.NotNull(controller.Pending);

        controller.Observe(default, inCombat: true, enabled: true);
        Assert.Null(controller.Pending);
        Assert.False(controller.TryTake(out _));
    }

    [Fact]
    public void DisabledDebriefSkipsPastWipesAndClearsVisibleSummary()
    {
        var controller = new DebriefSummaryController();
        var firstWipe = CreateSnapshot(
            1,
            CreateRecord("4ff7fe1e-220e-44bb-8a28-dd3be22c55cb"),
            PullEndReason.DutyWiped);

        controller.Observe(firstWipe, inCombat: false, enabled: false);
        Assert.Null(controller.Pending);
        controller.Observe(firstWipe, inCombat: false, enabled: true);
        Assert.Null(controller.Pending);

        var secondWipe = CreateSnapshot(
            2,
            CreateRecord("b11a21f9-1bc8-4fa9-9bd8-e1a4636a9b8a"),
            PullEndReason.DutyWiped);
        controller.Observe(secondWipe, inCombat: false, enabled: true);
        Assert.NotNull(controller.Pending);

        controller.Observe(secondWipe, inCombat: false, enabled: false);
        Assert.Null(controller.Pending);
    }

    [Fact]
    public void OneClickRequestTargetsExactGenerationAndStartsPausedAtSuggestedTime()
    {
        var record = CreateRecord("785449f9-0d9c-49e6-a084-d6f6f2572bd0");
        var snapshot = CreateSnapshot(7, record, PullEndReason.DutyWiped);
        var request = new DebriefReplayRequest(
            7,
            record.CaptureId,
            new DebriefReplayWindow(40_000, 60_000));

        Assert.True(ReplayWindow.IsDebriefReplayRequestCurrent(snapshot, request));
        Assert.False(
            ReplayWindow.IsDebriefReplayRequestCurrent(
                snapshot,
                request with { SourceGeneration = 6 }));
        Assert.False(
            ReplayWindow.IsDebriefReplayRequestCurrent(
                snapshot,
                request with { CaptureId = Guid.Parse("c63fce80-bda0-462d-afda-fc2d493b91bd") }));

        var session = new ReplaySession(record);
        session.Play();
        ReplayWindow.ApplyDebriefReplayRequest(
            session,
            request.Window.StartTimestampMilliseconds);

        Assert.Equal(40_000, session.CurrentTimeMilliseconds);
        Assert.False(session.IsPlaying);
    }

    [Fact]
    public void DebriefSettingDefaultsEnabledAndPreservesLegacySerializedKey()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.ShowPostWipeDebrief);
        Assert.True(configuration.CloseReplayOnCombatStart);

        var restored = JsonConvert.DeserializeObject<PluginConfiguration>(
            """{"Version":1,"ShowWipeReplayPrompt":false}""");
        Assert.NotNull(restored);
        Assert.False(restored.ShowPostWipeDebrief);
    }

    private static ReplaySourceSnapshot CreateSnapshot(
        long completedGeneration,
        PullRecord record,
        PullEndReason endReason,
        Guid? summaryCaptureId = null) =>
        new(
            FinalizationGeneration: completedGeneration,
            FinalizationState: ReplaySourceFinalizationState.Succeeded,
            FinalizationCaptureId: record.CaptureId,
            FinalizationError: null,
            CompletedGeneration: completedGeneration,
            LastCompletedPull: record,
            LastCompletedEndReason: endReason,
            LastCompletedDebrief: CreateSummary(summaryCaptureId ?? record.CaptureId));

    private static DebriefSummary CreateSummary(Guid captureId) =>
        new()
        {
            CaptureId = captureId,
            PullNumber = 1,
            DurationMilliseconds = 60_000,
            WipeTimestampMilliseconds = 60_000,
            DeathSequence = [],
            UnresolvedDeathEventCount = 0,
            SuggestedReplayWindow = new DebriefReplayWindow(40_000, 60_000),
        };

    private static PullRecord CreateRecord(string captureId) =>
        new()
        {
            CaptureId = Guid.Parse(captureId),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-10T00:01:00Z"),
            TerritoryType = 1,
            MapId = 2,
            Instance = 0,
            Actors = [],
            Frames = [],
        };
}
