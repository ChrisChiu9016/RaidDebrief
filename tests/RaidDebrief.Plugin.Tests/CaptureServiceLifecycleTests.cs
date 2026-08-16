using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using RaidDebrief.Core;
using Serilog;
using Serilog.Events;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class CaptureServiceLifecycleTests
{
    [Theory]
    [InlineData(ObservedEventType.DutyWiped, PullEndReason.DutyWiped)]
    [InlineData(ObservedEventType.DutyCompleted, PullEndReason.DutyCompleted)]
    public void DutyEndFinalizesOnceAndWaitsForCombatClear(
        ObservedEventType eventType,
        PullEndReason endReason)
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState);
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 100, 200, 3);
            service.RecordFrameworkSnapshot([CreateActor("First Pull", 101)], [], true, 100, 200, 3);
            Assert.True(service.IsRecording);

            dutyState.Raise(eventType);
            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);

            Assert.Equal(endReason, service.Status.LastEndReason);
            Assert.Single(record.Events, observedEvent => observedEvent.Type == eventType);
            Assert.Equal((uint)100, record.TerritoryType);
            Assert.Equal((uint)200, record.MapId);
            Assert.Equal((uint)3, record.Instance);
            Assert.False(Directory.Exists(exportDirectory));

            dutyState.Raise(eventType);
            Assert.Equal(1, service.Status.CompletedPullCount);

            service.RecordFrameworkSnapshot([CreateActor("Ignored Mid-Pull", 202)], [], true, 100, 200, 3);
            Assert.False(service.IsRecording);
            Assert.Equal(AutomaticPullState.Completed, service.Status.AutomaticState);

            service.RecordFrameworkSnapshot([], [], false, 100, 200, 3);
            Assert.Equal(AutomaticPullState.Idle, service.Status.AutomaticState);
            service.RecordFrameworkSnapshot([CreateActor("Second Pull", 303)], [], true, 100, 200, 3);
            Assert.True(service.IsRecording);
            Assert.Same(record, service.GetReplaySourceSnapshot().LastCompletedPull);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void AutomaticPullsSnapshotDutyRunIdentityAndQueueHistory()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var tracker = new DutyRunTracker(
            () => DateTimeOffset.Parse("2026-08-16T07:43:26Z"),
            TimeZoneInfo.Utc);
        tracker.ObserveBoundState(true, 100, 1_003, "AAC Heavyweight M4");
        var archived = new List<PullRecord>();
        var service = CreateService(
            exportDirectory,
            dutyState,
            beginDutyPull: tracker.BeginPull,
            archiveAutomaticPull: archived.Add);
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 100, 200, 0);
            service.RecordFrameworkSnapshot(
                [CreateActor("First Pull", 101)],
                [],
                true,
                100,
                200,
                0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var first = WaitForCompleted(service, expectedCompletedPullCount: 1);

            service.RecordFrameworkSnapshot([], [], false, 100, 200, 0);
            service.RecordFrameworkSnapshot(
                [CreateActor("Second Pull", 202)],
                [],
                true,
                100,
                200,
                0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var second = WaitForCompleted(service, expectedCompletedPullCount: 2);

            Assert.NotNull(first.DutyRun);
            Assert.NotNull(second.DutyRun);
            Assert.Equal(first.DutyRun.DutyRunId, second.DutyRun.DutyRunId);
            Assert.Equal(1, first.DutyRun.PullOrdinalWithinDutyRun);
            Assert.Equal(2, second.DutyRun.PullOrdinalWithinDutyRun);
            Assert.Equal(CaptureMode.AutomaticPull, first.CaptureMode);
            Assert.Equal(PullEndReason.DutyWiped, first.EndReason);
            Assert.Equal([first.CaptureId, second.CaptureId], archived.Select(item => item.CaptureId));
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }
    [Fact]
    public void ReplaySourceSnapshotExposesFinalizationAndCompletionAtomically()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        using var validationStarted = new ManualResetEventSlim();
        using var allowValidation = new ManualResetEventSlim();
        var service = CreateService(
            exportDirectory,
            dutyState,
            validateCapture: record =>
            {
                validationStarted.Set();
                allowValidation.Wait();
                PullRecordValidator.Validate(record);
            });
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 100, 200, 3);
            service.RecordFrameworkSnapshot([CreateActor("Finalizing Pull", 101)], [], true, 100, 200, 3);
            dutyState.Raise(ObservedEventType.DutyWiped);
            Assert.True(validationStarted.Wait(TimeSpan.FromSeconds(5)));

            var finalizing = service.GetReplaySourceSnapshot();
            Assert.Equal(1, finalizing.FinalizationGeneration);
            Assert.Equal(ReplaySourceFinalizationState.Finalizing, finalizing.FinalizationState);
            Assert.NotNull(finalizing.FinalizationCaptureId);
            Assert.Null(finalizing.FinalizationError);
            Assert.Equal(0, finalizing.CompletedGeneration);
            Assert.Null(finalizing.LastCompletedPull);

            allowValidation.Set();
            var completedRecord = WaitForCompleted(service, expectedCompletedPullCount: 1);
            var completed = service.GetReplaySourceSnapshot();

            Assert.Equal(finalizing.FinalizationGeneration, completed.FinalizationGeneration);
            Assert.Equal(ReplaySourceFinalizationState.Succeeded, completed.FinalizationState);
            Assert.Equal(finalizing.FinalizationCaptureId, completed.FinalizationCaptureId);
            Assert.Null(completed.FinalizationError);
            Assert.Equal(1, completed.CompletedGeneration);
            Assert.Same(completedRecord, completed.LastCompletedPull);
        }
        finally
        {
            allowValidation.Set();
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }


    [Fact]
    public void ThreeConsecutivePullsRemainIsolatedInMemory()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState);
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 10, 20, 0);
            service.RecordFrameworkSnapshot([CreateActor("First Actor", 111)], [], true, 10, 20, 0);
            service.RecordActionEffect(
                globalSequence: 1,
                actionId: 1_001,
                actionType: 1,
                sourceObjectId: 111,
                sourceEntityId: 111,
                animationTargetObjectId: 111,
                [CreateDamageTarget(111, 1_111)]);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var first = WaitForCompleted(service, expectedCompletedPullCount: 1);
            var firstSummary = Assert.IsType<DebriefSummary>(
                service.GetReplaySourceSnapshot().LastCompletedDebrief);

            service.RecordFrameworkSnapshot([], [], false, 30, 40, 1);
            service.RecordFrameworkSnapshot([CreateActor("Second Actor", 222)], [], true, 30, 40, 1);
            Assert.Same(first, service.GetReplaySourceSnapshot().LastCompletedPull);
            service.RecordActionEffect(
                globalSequence: 2,
                actionId: 2_002,
                actionType: 1,
                sourceObjectId: 222,
                sourceEntityId: 222,
                animationTargetObjectId: 222,
                [CreateDamageTarget(222, 2_222)]);
            dutyState.Raise(ObservedEventType.DutyCompleted);
            var second = WaitForCompleted(service, expectedCompletedPullCount: 2);
            var secondSummary = Assert.IsType<DebriefSummary>(
                service.GetReplaySourceSnapshot().LastCompletedDebrief);

            service.RecordFrameworkSnapshot([], [], false, 50, 60, 2);
            service.RecordFrameworkSnapshot([CreateActor("Third Actor", 333)], [], true, 50, 60, 2);
            Assert.Same(second, service.GetReplaySourceSnapshot().LastCompletedPull);
            service.RecordActionEffect(
                globalSequence: 3,
                actionId: 3_003,
                actionType: 1,
                sourceObjectId: 333,
                sourceEntityId: 333,
                animationTargetObjectId: 333,
                [CreateDamageTarget(333, 3_333)]);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var third = WaitForCompleted(service, expectedCompletedPullCount: 3);
            var thirdSummary = Assert.IsType<DebriefSummary>(
                service.GetReplaySourceSnapshot().LastCompletedDebrief);

            Assert.Same(third, service.GetReplaySourceSnapshot().LastCompletedPull);
            Assert.Equal(3, new[] { first.CaptureId, second.CaptureId, third.CaptureId }.Distinct().Count());
            Assert.Equal(1, firstSummary.PullNumber);
            Assert.Equal(first.CaptureId, firstSummary.CaptureId);
            Assert.Equal(2, secondSummary.PullNumber);
            Assert.Equal(second.CaptureId, secondSummary.CaptureId);
            Assert.Equal(3, thirdSummary.PullNumber);
            Assert.Equal(third.CaptureId, thirdSummary.CaptureId);
            Assert.Equal((uint)10, first.TerritoryType);
            Assert.Equal((uint)30, second.TerritoryType);
            Assert.Equal((uint)50, third.TerritoryType);
            Assert.True(first.EndedAtUtc <= second.StartedAtUtc);
            Assert.True(second.EndedAtUtc <= third.StartedAtUtc);
            Assert.False(Directory.Exists(exportDirectory));
            Assert.Contains(first.Actors, actor => actor.GameObjectId == 111 && actor.Name == "Player 1");
            Assert.DoesNotContain(first.Actors, actor => actor.GameObjectId == 222);
            Assert.Contains(second.Actors, actor => actor.GameObjectId == 222 && actor.Name == "Player 1");
            Assert.DoesNotContain(second.Actors, actor => actor.GameObjectId == 111);
            Assert.Contains(third.Actors, actor => actor.GameObjectId == 333 && actor.Name == "Player 1");
            Assert.DoesNotContain(third.Actors, actor => actor.GameObjectId is 111 or 222);
            Assert.Contains(first.Events, observedEvent => observedEvent.Type == ObservedEventType.DutyWiped);
            Assert.DoesNotContain(second.Events, observedEvent => observedEvent.Type == ObservedEventType.DutyWiped);
            Assert.Contains(second.Events, observedEvent => observedEvent.Type == ObservedEventType.DutyCompleted);
            Assert.DoesNotContain(third.Events, observedEvent => observedEvent.Type == ObservedEventType.DutyCompleted);
            var firstActionEffect = Assert.Single(first.ActionEffects);
            Assert.Equal((uint)1_001, firstActionEffect.ActionId);
            Assert.Equal(1, Assert.Single(firstActionEffect.Targets).TargetStableActorId);
            Assert.Equal(1, firstActionEffect.SourceStableActorId);
            var secondActionEffect = Assert.Single(second.ActionEffects);
            Assert.Equal((uint)2_002, secondActionEffect.ActionId);
            Assert.Equal(1, secondActionEffect.SourceStableActorId);
            Assert.Equal(1, Assert.Single(secondActionEffect.Targets).TargetStableActorId);
            var thirdActionEffect = Assert.Single(third.ActionEffects);
            Assert.Equal((uint)3_003, thirdActionEffect.ActionId);
            Assert.Equal(1, thirdActionEffect.SourceStableActorId);
            Assert.Equal(1, Assert.Single(thirdActionEffect.Targets).TargetStableActorId);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void ReloadDoesNotRestoreRuntimePullAndWaitsForCombatClear()
    {
        var exportDirectory = CreateExportDirectory();
        var firstDutyState = new FakeDutyState();
        var firstService = CreateService(exportDirectory, firstDutyState);
        firstService.RecordFrameworkSnapshot([], [], false, 50, 60, 0);
        firstService.RecordFrameworkSnapshot([CreateActor("Before Reload", 333)], [], true, 50, 60, 0);
        firstService.Dispose();

        try
        {
            var beforeReload = Assert.IsType<PullRecord>(firstService.GetReplaySourceSnapshot().LastCompletedPull);
            Assert.Contains(
                beforeReload.Actors,
                actor => actor.GameObjectId == 333 && actor.Name == "Player 1");
            Assert.False(Directory.Exists(exportDirectory));

            var secondDutyState = new FakeDutyState();
            var secondService = CreateService(exportDirectory, secondDutyState);
            try
            {
                Assert.Null(secondService.GetReplaySourceSnapshot().LastCompletedPull);
                secondService.RecordFrameworkSnapshot([CreateActor("Existing Combat", 444)], [], true,
                50,
                60,
                0);
                Assert.False(secondService.IsRecording);
                Assert.False(secondService.Status.IsArmedForCombatStart);

                secondService.RecordFrameworkSnapshot([], [], false, 50, 60, 0);
                secondService.RecordFrameworkSnapshot([CreateActor("After Reload", 555)], [], true,
                50,
                60,
                0);
                Assert.True(secondService.IsRecording);
            }
            finally
            {
                secondService.Dispose();
            }

            var afterReload = Assert.IsType<PullRecord>(secondService.GetReplaySourceSnapshot().LastCompletedPull);
            Assert.NotEqual(beforeReload.CaptureId, afterReload.CaptureId);
            Assert.Contains(
                afterReload.Actors,
                actor => actor.GameObjectId == 555 && actor.Name == "Player 1");
            Assert.DoesNotContain(afterReload.Actors, actor => actor.GameObjectId == 333);
            Assert.DoesNotContain(afterReload.Actors, actor => actor.GameObjectId == 444);
            Assert.False(Directory.Exists(exportDirectory));
        }
        finally
        {
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void AutomaticCaptureRequiresDutyInstanceAndFinalizesOnExit()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState);
        try
        {
            service.RecordFrameworkSnapshot([], [], false,
            10,
            20,
            0,
            isInDutyInstance: false);
            service.RecordFrameworkSnapshot([CreateActor("Open World Combat", 601)], [], true,
            10,
            20,
            0,
            isInDutyInstance: false);

            Assert.False(service.IsRecording);
            Assert.False(service.Status.IsInDutyInstance);
            Assert.False(service.Status.IsArmedForCombatStart);

            service.RecordFrameworkSnapshot([CreateActor("Existing Instance Combat", 602)], [], true,
            30,
            40,
            0,
            isInDutyInstance: true);
            Assert.False(service.IsRecording);

            service.RecordFrameworkSnapshot([], [], false,
            30,
            40,
            0,
            isInDutyInstance: true);
            service.RecordFrameworkSnapshot([CreateActor("Inside Instance", 603)], [], true,
            30,
            40,
            0,
            isInDutyInstance: true);
            Assert.True(service.IsRecording);

            service.RecordFrameworkSnapshot([CreateActor("After Instance Exit", 604)], [], true,
            50,
            60,
            0,
            isInDutyInstance: false);
            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);

            Assert.Equal(PullEndReason.InstanceExited, service.Status.LastEndReason);
            Assert.False(service.Status.IsInDutyInstance);
            Assert.Equal((uint)30, record.TerritoryType);
            Assert.Contains(record.Actors, actor => actor.GameObjectId == 603);
            Assert.DoesNotContain(
                record.Actors,
                actor => actor.GameObjectId is 601 or 602 or 604);
            Assert.False(Directory.Exists(exportDirectory));
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void AutomaticCombatEndDebouncesAndReentryCancelsPendingEnd()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var log = new FakePluginLog();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticLifecycle: new AutomaticPullLifecycle(combatEndDebounceMilliseconds: 75),
            pluginLog: log);
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 90, 91, 0);
            service.RecordFrameworkSnapshot([CreateActor("Debounce Actor", 777)], [], true, 90, 91, 0);

            service.RecordFrameworkSnapshot([], [], false, 90, 91, 0);
            Thread.Sleep(40);
            service.RecordFrameworkSnapshot([], [], true, 90, 91, 0);
            Thread.Sleep(100);
            Assert.True(service.IsRecording);

            service.RecordFrameworkSnapshot([], [], false, 90, 91, 0);
            Thread.Sleep(100);
            service.RecordFrameworkSnapshot([], [], false, 90, 91, 0);
            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);

            Assert.Equal(PullEndReason.CombatEnded, service.Status.LastEndReason);
            Assert.Single(
                record.Events,
                observedEvent => observedEvent.Type == ObservedEventType.InCombatChanged
                    && observedEvent.State == false);
            Assert.False(Directory.Exists(exportDirectory));
            Assert.Contains(
                log.Messages,
                message => message.Contains("armed after observing InCombat=false", StringComparison.Ordinal));
            Assert.Contains(
                log.Messages,
                message => message.Contains("combat-end debounce started", StringComparison.Ordinal));
            Assert.Contains(
                log.Messages,
                message => message.Contains("debounce cancelled by combat re-entry", StringComparison.Ordinal));
            Assert.Contains(
                log.Messages,
                message => message.Contains("finalizing because {EndReason}", StringComparison.Ordinal));
            Assert.Contains(
                log.Messages,
                message => message.Contains("finalized and validated because {FinalizeReason}", StringComparison.Ordinal));
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void PlayerDeathAndBossDespawnRespawnStayInOnePull()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState);
        try
        {
            var playerAlive = CreateActor("Player", 801);
            var playerDead = CreateActor("Player", 801, isDead: true);
            var boss = CreateActor(
                "Boss",
                802,
                objectKind: ObjectKind.BattleNpc,
                hitboxRadius: 6.5f,
                isOmnidirectional: true);
            var summon = CreateActor("Summon", 803, ObjectKind.BattleNpc, ownerId: playerAlive.EntityId);

            service.RecordFrameworkSnapshot([], [], false, 95, 96, 0);
            service.RecordFrameworkSnapshot([playerAlive, boss, summon], [], true, 95, 96, 0);
            Thread.Sleep(110);
            service.RecordFrameworkSnapshot([playerDead, summon], [], true, 95, 96, 0);
            Assert.True(service.IsRecording);

            Thread.Sleep(110);
            service.RecordFrameworkSnapshot([playerDead, boss, summon], [], true, 95, 96, 0);
            Assert.True(service.IsRecording);

            dutyState.Raise(ObservedEventType.DutyWiped);
            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.Equal(
                CaptureFeatures.ActorOwnerId
                    | CaptureFeatures.HitboxRadius
                    | CaptureFeatures.OmnidirectionalState
                    | CaptureFeatures.PartyMembership
                    | CaptureFeatures.CastTiming
                    | CaptureFeatures.StatusTiming
                    | CaptureFeatures.ActionNameSnapshot
                    | CaptureFeatures.BarrierState,
                record.Features);
            Assert.Contains(record.Actors, actor => actor.GameObjectId == 803 && actor.OwnerId == 801);
            Assert.All(
                record.Frames.SelectMany(frame => frame.Actors).Where(actor => actor.StableActorId == 2),
                actor =>
                {
                    Assert.Equal(6.5f, actor.HitboxRadius);
                    Assert.True(actor.IsOmnidirectional);
                });

            Assert.Single(
                record.Events,
                observedEvent => observedEvent.Type == ObservedEventType.Death
                    && observedEvent.StableActorId == 1);
            Assert.Single(
                record.Events,
                observedEvent => observedEvent.Type == ObservedEventType.ActorDespawned
                    && observedEvent.StableActorId == 2);
            Assert.Single(
                record.Events,
                observedEvent => observedEvent.Type == ObservedEventType.ActorSpawned
                    && observedEvent.StableActorId == 2);
            Assert.Single(
                record.Events,
                observedEvent => observedEvent.Type == ObservedEventType.DutyWiped);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public async Task DisposeWaitsForPendingDeveloperExportBeforeReturning()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        using var exportStarted = new ManualResetEventSlim();
        using var allowExport = new ManualResetEventSlim();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: false,
            exportCapture: (path, record) =>
            {
                exportStarted.Set();
                allowExport.Wait();
                CaptureJson.Save(path, record);
            });
        try
        {
            Assert.True(service.Start(95, 96, 0));
            service.RecordFrameworkSnapshot([CreateActor("Player", 901)], [], false, 95, 96, 0);
            Assert.True(service.StopAndExport());
            Assert.True(exportStarted.Wait(TimeSpan.FromSeconds(5)));

            var disposeTask = Task.Run(service.Dispose);
            await Task.Delay(100);
            Assert.False(disposeTask.IsCompleted);

            allowExport.Set();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            var record = Assert.IsType<PullRecord>(service.GetReplaySourceSnapshot().LastCompletedPull);
            Assert.Equal(record.CaptureId, service.Status.LastCompletedCaptureId);
            Assert.True(File.Exists(service.Status.LastExportPath));
        }
        finally
        {
            allowExport.Set();
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void ExportLastCompletedPullReturnsFalseWithoutCompletedPull()
    {
        var exportDirectory = CreateExportDirectory();
        var service = CreateService(exportDirectory, new FakeDutyState());
        try
        {
            Assert.False(service.ExportLastCompletedPull());
            Assert.False(Directory.Exists(exportDirectory));
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void ExportLastCompletedPullWritesAutomaticPullWithoutBlockingNextCapture()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        using var exportStarted = new ManualResetEventSlim();
        using var allowExport = new ManualResetEventSlim();
        var service = CreateService(
            exportDirectory,
            dutyState,
            exportCapture: (path, record) =>
            {
                exportStarted.Set();
                allowExport.Wait();
                CaptureJson.Save(path, record);
            });
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 97, 98, 0);
            service.RecordFrameworkSnapshot(
                [CreateActor("First Player", 1_101)],
                [],
                true,
                97,
                98,
                0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var first = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.False(Directory.Exists(exportDirectory));

            Assert.True(service.ExportLastCompletedPull());
            Assert.True(exportStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(service.Status.IsDeveloperExportBusy);

            service.RecordFrameworkSnapshot([], [], false, 97, 98, 0);
            service.RecordFrameworkSnapshot(
                [CreateActor("Second Player", 1_102)],
                [],
                true,
                97,
                98,
                0);
            Assert.True(service.IsRecording);

            allowExport.Set();
            WaitForDeveloperExport(service);
            var exportPath = Assert.IsType<string>(service.Status.LastExportPath);
            Assert.Equal(ExpectedExportFileName(first), Path.GetFileName(exportPath));
            var loaded = CaptureJson.Load(exportPath);
            Assert.Equal(first.CaptureId, loaded.CaptureId);
            Assert.Equal("Player 1", Assert.Single(loaded.Actors).Name);
            Assert.Null(service.Status.LastExportError);
        }
        finally
        {
            allowExport.Set();
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public async Task DisposeWaitsForPendingLastCompletedPullExportBeforeReturning()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        using var exportStarted = new ManualResetEventSlim();
        using var allowExport = new ManualResetEventSlim();
        var service = CreateService(
            exportDirectory,
            dutyState,
            exportCapture: (path, record) =>
            {
                exportStarted.Set();
                allowExport.Wait();
                CaptureJson.Save(path, record);
            });
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 99, 100, 0);
            service.RecordFrameworkSnapshot(
                [CreateActor("Player", 1_201)],
                [],
                true,
                99,
                100,
                0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            WaitForCompleted(service, expectedCompletedPullCount: 1);

            Assert.True(service.ExportLastCompletedPull());
            Assert.True(exportStarted.Wait(TimeSpan.FromSeconds(5)));

            var disposeTask = Task.Run(service.Dispose);
            await Task.Delay(100);
            Assert.False(disposeTask.IsCompleted);

            allowExport.Set();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(service.Status.LastExportPath));
        }
        finally
        {
            allowExport.Set();
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void ValidationFailurePreservesPreviousLastCompletedPull()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState);
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 92, 93, 0);
            service.RecordFrameworkSnapshot([CreateActor("First Pull", 1_001)], [], true, 92, 93, 0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var first = WaitForCompleted(service, expectedCompletedPullCount: 1);

            service.RecordFrameworkSnapshot([], [], false, 92, 93, 0);
            service.RecordFrameworkSnapshot([CreateActor("Invalid Pull", 1_002)], [], true, 92, 93, 0);
            service.RecordActionEffect(
                globalSequence: 2,
                actionId: 2_002,
                actionType: 1,
                sourceObjectId: 1_002,
                sourceEntityId: 1_002,
                animationTargetObjectId: 1_002,
                []);
            dutyState.Raise(ObservedEventType.DutyWiped);
            WaitForBackgroundWork(service);

            var replaySource = service.GetReplaySourceSnapshot();
            Assert.Equal(1, service.Status.CompletedPullCount);
            Assert.Equal(2, replaySource.FinalizationGeneration);
            Assert.Equal(ReplaySourceFinalizationState.Failed, replaySource.FinalizationState);
            Assert.NotEqual(first.CaptureId, replaySource.FinalizationCaptureId);
            Assert.False(string.IsNullOrWhiteSpace(replaySource.FinalizationError));
            Assert.Equal(1, replaySource.CompletedGeneration);
            Assert.Same(first, replaySource.LastCompletedPull);
            var preservedDebrief = Assert.IsType<DebriefSummary>(
                replaySource.LastCompletedDebrief);
            Assert.Equal(first.CaptureId, preservedDebrief.CaptureId);
            Assert.Equal(1, preservedDebrief.PullNumber);
            Assert.Equal(AutomaticPullState.Idle, service.Status.AutomaticState);
            Assert.Contains("失敗", service.Status.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(exportDirectory));

            service.RecordFrameworkSnapshot([], [], false, 92, 93, 0);
            service.RecordFrameworkSnapshot([CreateActor("Third Pull", 1_003)], [], true, 92, 93, 0);
            Assert.True(service.IsRecording);
            Assert.Same(first, service.GetReplaySourceSnapshot().LastCompletedPull);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void DeveloperCompressedJsonExportAnonymizesPlayersAndLoadsOffline()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState, automaticCaptureEnabled: false);
        try
        {
            Assert.True(service.Start(70, 80, 2));
            service.RecordFrameworkSnapshot([
                CreateActor("Alice Example", 601),
                CreateActor("Bob Example", 602),
                CreateActor("Raid Boss", 603, ObjectKind.BattleNpc),
            ], [], false,
            70,
            80,
            2,
            isInDutyInstance: false);
            Assert.True(service.StopAndExport());
            Assert.False(service.Status.IsInDutyInstance);

            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.Collection(
                record.Actors,
                actor => Assert.Equal("Player 1", actor.Name),
                actor => Assert.Equal("Player 2", actor.Name),
                actor => Assert.Equal("Raid Boss", actor.Name));

            var exportPath = Assert.IsType<string>(service.Status.LastExportPath);
            Assert.Equal(ExpectedExportFileName(record), Path.GetFileName(exportPath));
            var loaded = CaptureJson.Load(exportPath);
            Assert.Equal(record.CaptureId, loaded.CaptureId);
            var json = ReadCompressedJson(exportPath);
            Assert.DoesNotContain("Alice Example", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Bob Example", json, StringComparison.Ordinal);
            Assert.DoesNotContain("contentId", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void CapturePersistsPartyCastStatusTimingAndActionEffectCapability()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: false,
            resolveActionName: static (actionId, _) => new RecordedActionName
            {
                ActionId = actionId,
                Name = "Recorded Cast",
                Language = "English",
                Source = ActionNameSource.RuntimeRsv,
            });
        try
        {
            service.SetActionEffectCaptureAvailability(true);
            Assert.True(service.Start(70, 80, 2));
            var actor = CreateActor("Party Member", 601) with
            {
                ClassJobId = 39,
                IsCasting = true,
                CastActionId = 1_234,
                CurrentCastTime = 0.4f,
                TotalCastTime = 2.5f,
                Statuses = [new PolledStatusObservation(1191, 601, 19.8f)],
                StatusCount = 1,
            };
            PartyMemberProbeSnapshot[] party =
            [
                new(
                    0,
                    "Party Member",
                    601,
                    601,
                    39,
                    100,
                    100,
                    100,
                    10_000,
                    10_000,
                    Vector3.Zero,
                    true),
            ];
            service.RecordFrameworkSnapshot([actor], party, false, 70, 80, 2);
            Assert.True(service.StopAndExport());

            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.True((record.Features & CaptureFeatures.PartyMembership) != 0);
            Assert.True((record.Features & CaptureFeatures.CastTiming) != 0);
            Assert.True((record.Features & CaptureFeatures.StatusTiming) != 0);
            Assert.True((record.Features & CaptureFeatures.ActionEffectCapture) != 0);
            Assert.True((record.Features & CaptureFeatures.ActionNameSnapshot) != 0);
            var actionName = Assert.Single(record.ActionNames);
            Assert.Equal(1_234u, actionName.ActionId);
            Assert.Equal("Recorded Cast", actionName.Name);
            Assert.Equal(ActionNameSource.RuntimeRsv, actionName.Source);
            Assert.Equal(0, Assert.Single(record.Actors).PartyIndex);
            var cast = Assert.Single(
                record.Events,
                value => value.Type == ObservedEventType.CastStarted);
            Assert.Equal(0.4f, cast.CurrentCastTime);
            Assert.Equal(2.5f, cast.TotalCastTime);
            var status = Assert.Single(
                record.Events,
                value => value.Type == ObservedEventType.StatusGained);
            Assert.Equal(19.8f, status.StatusRemainingTime);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void SoloCaptureStillDeclaresPartyMembershipCapability()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState, automaticCaptureEnabled: false);
        try
        {
            Assert.True(service.Start(70, 80, 2));

            // A solo Pull observes an empty party list; the field is still recorded, so Replay must
            // not report it as a legacy recording limitation.
            service.RecordFrameworkSnapshot([CreateActor("Solo Player", 601)], [], false, 70, 80, 2);
            Assert.True(service.StopAndExport());

            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.True((record.Features & CaptureFeatures.PartyMembership) != 0);
            Assert.Null(Assert.Single(record.Actors).PartyIndex);

            var warning = ReplayWindow.BuildCaptureFeatureWarning(record);
            Assert.DoesNotContain("Party membership", warning ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void ActionEffectWithoutCastQueuesActionNameResolutionOnNextFrame()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var resolutionRequests = new List<(uint ActionId, uint SourceEntityId)>();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: false,
            resolveActionName: (actionId, sourceEntityId) =>
            {
                resolutionRequests.Add((actionId, sourceEntityId));
                return new RecordedActionName
                {
                    ActionId = actionId,
                    Name = "Auto-attack",
                    Language = "English",
                    Source = ActionNameSource.StaticExcel,
                };
            });
        try
        {
            Assert.True(service.Start(1154, 835, 0));
            service.RecordActionEffect(
                globalSequence: 1,
                actionId: 34_423,
                actionType: 1,
                sourceObjectId: 777,
                sourceEntityId: 777,
                animationTargetObjectId: 888,
                [CreateDamageTarget(888, 25_670)]);
            service.RecordActionEffect(
                globalSequence: 2,
                actionId: 34_423,
                actionType: 1,
                sourceObjectId: 777,
                sourceEntityId: 777,
                animationTargetObjectId: 888,
                [CreateDamageTarget(888, 27_472)]);
            Assert.Empty(resolutionRequests);

            service.RecordFrameworkSnapshot(
                [CreateActor("Boss", 777, ObjectKind.BattleNpc)],
                [],
                true,
                1154,
                835,
                0);
            Assert.True(service.StopAndExport());

            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.Equal(2, record.ActionEffects.Length);
            Assert.DoesNotContain(
                record.Events,
                observedEvent => observedEvent.Type == ObservedEventType.CastStarted);
            var actionName = Assert.Single(record.ActionNames);
            Assert.Equal(34_423u, actionName.ActionId);
            Assert.Equal("Auto-attack", actionName.Name);
            Assert.Equal(ActionNameSource.StaticExcel, actionName.Source);
            Assert.Equal([(34_423u, 777u)], resolutionRequests);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void ManualModeStillRequiresExplicitStartAndStop()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        bool? changedSetting = null;
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: false,
            automaticCaptureChanged: enabled => changedSetting = enabled);
        try
        {
            Assert.False(service.Status.AutomaticCaptureEnabled);
            Assert.True(service.Start(70, 80, 2));
            service.RecordFrameworkSnapshot([CreateActor("Manual Actor", 666)], [], false,
            70,
            80,
            2);
            Assert.False(service.SetAutomaticCaptureEnabled(true));
            Assert.Null(changedSetting);

            Assert.True(service.StopAndExport());
            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            Assert.Contains(record.Actors, actor => actor.GameObjectId == 666 && actor.Name == "Player 1");
            Assert.Equal((uint)70, record.TerritoryType);

            Assert.True(service.SetAutomaticCaptureEnabled(true));
            Assert.True(changedSetting);
            Assert.True(service.Status.AutomaticCaptureEnabled);
            Assert.False(service.Start(70, 80, 2));
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RepeatedNativeActionEffectTargetsFinalizeInBothCaptureModes(bool automatic)
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: automatic);
        try
        {
            if (automatic)
            {
                service.RecordFrameworkSnapshot([], [], false, 1149, 834, 0);
                service.RecordFrameworkSnapshot([CreateActor("Repeated Target", 777)], [], true,
                1149,
                834,
                0);
            }
            else
            {
                Assert.True(service.Start(1149, 834, 0));
                service.RecordFrameworkSnapshot([CreateActor("Repeated Target", 777)], [], true,
                1149,
                834,
                0);
            }

            service.RecordActionEffect(
                globalSequence: 1,
                actionId: 42,
                actionType: 1,
                sourceObjectId: 777,
                sourceEntityId: 777,
                animationTargetObjectId: 777,
                [CreateDamageTarget(777, 100), CreateDamageTarget(777, 200)]);

            if (automatic)
            {
                dutyState.Raise(ObservedEventType.DutyWiped);
            }
            else
            {
                Assert.True(service.StopAndExport());
            }

            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            var actionEffect = Assert.Single(record.ActionEffects);
            Assert.Equal(2, actionEffect.Targets.Length);
            Assert.All(actionEffect.Targets, target => Assert.Equal(1, target.TargetStableActorId));
            Assert.Equal((uint)100, actionEffect.Targets[0].Entries[0].Amount);
            Assert.Equal((uint)200, actionEffect.Targets[1].Entries[0].Amount);
            Assert.DoesNotContain("失敗", service.Status.Message, StringComparison.Ordinal);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void DebriefAnalysisFailurePreservesAtomicPreviousHandoffAndConsumesOrdinal()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var analyzer = new DebriefAnalyzer();
        var analysisCount = 0;
        DebriefSummary Analyze(PullRecord record, long? pullNumber)
        {
            analysisCount++;
            if (analysisCount == 2)
            {
                throw new InvalidDataException("Synthetic Debrief failure.");
            }

            return analyzer.Analyze(record, pullNumber);
        }

        var service = CreateService(
            exportDirectory,
            dutyState,
            analyzeDebrief: Analyze);
        try
        {
            service.RecordFrameworkSnapshot([], [], false, 10, 20, 0);
            service.RecordFrameworkSnapshot([CreateActor("First", 101)], [], true, 10, 20, 0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var first = WaitForCompleted(service, expectedCompletedPullCount: 1);
            var firstSnapshot = service.GetReplaySourceSnapshot();
            var firstDebrief = Assert.IsType<DebriefSummary>(firstSnapshot.LastCompletedDebrief);

            service.RecordFrameworkSnapshot([], [], false, 10, 20, 0);
            service.RecordFrameworkSnapshot([CreateActor("Second", 202)], [], true, 10, 20, 0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            WaitForBackgroundWork(service);

            var failed = service.GetReplaySourceSnapshot();
            Assert.Equal(ReplaySourceFinalizationState.Failed, failed.FinalizationState);
            Assert.Same(first, failed.LastCompletedPull);
            Assert.Same(firstDebrief, failed.LastCompletedDebrief);
            Assert.Equal(1, failed.CompletedGeneration);

            service.RecordFrameworkSnapshot([], [], false, 10, 20, 0);
            service.RecordFrameworkSnapshot([CreateActor("Third", 303)], [], true, 10, 20, 0);
            dutyState.Raise(ObservedEventType.DutyWiped);
            var third = WaitForCompleted(service, expectedCompletedPullCount: 2);
            var recovered = service.GetReplaySourceSnapshot();
            var thirdDebrief = Assert.IsType<DebriefSummary>(recovered.LastCompletedDebrief);
            Assert.Equal(third.CaptureId, thirdDebrief.CaptureId);
            Assert.Equal(3, thirdDebrief.PullNumber);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void DiagnosticsFormattingIsLazyAndCached()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: false);
        try
        {
            Assert.True(service.Start(10, 20, 0));
            dutyState.Raise(ObservedEventType.DutyStarted);
            service.RecordActionEffect(
                globalSequence: 1,
                actionId: 42,
                actionType: 1,
                sourceObjectId: 100,
                sourceEntityId: 100,
                animationTargetObjectId: null,
                []);

            Assert.Equal(0, service.DiagnosticsFormatCount);

            var firstStatus = service.Status;
            Assert.NotNull(firstStatus.LastEvent);
            Assert.NotNull(firstStatus.LastActionEffect);
            Assert.Equal(2, service.DiagnosticsFormatCount);

            _ = service.Status;
            Assert.Equal(2, service.DiagnosticsFormatCount);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void IdleFrameworkLifecyclePathAllocatesNothingAfterWarmup()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(
            exportDirectory,
            dutyState,
            automaticCaptureEnabled: false);
        try
        {
            for (var iteration = 0; iteration < 1_000; iteration++)
            {
                _ = service.BeginFrameworkUpdate(false, 10, 20, 0);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 10_000; iteration++)
            {
                _ = service.BeginFrameworkUpdate(false, 10, 20, 0);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    [Fact]
    public void AutomaticCombatStartRequestsAndRecordsImmediateFirstSample()
    {
        var exportDirectory = CreateExportDirectory();
        var dutyState = new FakeDutyState();
        var service = CreateService(exportDirectory, dutyState);
        try
        {
            Assert.False(service.BeginFrameworkUpdate(false, 10, 20, 0).SampleRequested);

            var decision = service.BeginFrameworkUpdate(true, 10, 20, 0);
            Assert.True(decision.SampleRequested);
            Assert.True(service.IsRecording);
            service.SubmitFrameworkSample(
                decision.Sample,
                [CreateActor("Immediate", 101)],
                []);

            dutyState.Raise(ObservedEventType.DutyWiped);
            var record = WaitForCompleted(service, expectedCompletedPullCount: 1);
            var frame = Assert.Single(record.Frames);
            Assert.Contains(frame.Actors, actor => actor.StableActorId == 1);
            Assert.InRange(frame.TimestampMilliseconds, 0, 100);
        }
        finally
        {
            service.Dispose();
            DeleteExportDirectory(exportDirectory);
        }
    }

    private static CaptureService CreateService(
        string developerExportDirectory,
        FakeDutyState dutyState,
        bool automaticCaptureEnabled = true,
        Action<bool>? automaticCaptureChanged = null,
        AutomaticPullLifecycle? automaticLifecycle = null,
        FakePluginLog? pluginLog = null,
        Action<string, PullRecord>? exportCapture = null,
        Action<PullRecord>? validateCapture = null,
        Func<PullRecord, long?, DebriefSummary>? analyzeDebrief = null,
        Func<uint, uint, RecordedActionName?>? resolveActionName = null,
        Func<DutyPullIdentity?>? beginDutyPull = null,
        Action<PullRecord>? archiveAutomaticPull = null)
    {
        var log = pluginLog ?? new FakePluginLog();
        return new CaptureService(
            developerExportDirectory,
            dutyState,
            log,
            new WaymarkReader(log),
            new TargetMarkerReader(log),
            automaticCaptureEnabled,
            automaticCaptureChanged ?? (_ => { }),
            automaticLifecycle,
            exportCapture,
            validateCapture,
            analyzeDebrief,
            resolveActionName,
            beginDutyPull,
            archiveAutomaticPull);
    }

    private static void WaitForBackgroundWork(CaptureService service)
    {
        var completed = SpinWait.SpinUntil(
            () => !service.Status.IsBusy,
            TimeSpan.FromSeconds(5));
        Assert.True(completed, "Capture background work did not finish within five seconds.");
    }

    private static void WaitForDeveloperExport(CaptureService service)
    {
        var completed = SpinWait.SpinUntil(
            () => !service.Status.IsDeveloperExportBusy,
            TimeSpan.FromSeconds(5));
        Assert.True(completed, "LastCompletedPull export did not finish within five seconds.");
    }

    private static PullRecord WaitForCompleted(
        CaptureService service,
        long expectedCompletedPullCount)
    {
        var completed = SpinWait.SpinUntil(
            () => service.Status.CompletedPullCount >= expectedCompletedPullCount
                && !service.Status.IsBusy,
            TimeSpan.FromSeconds(5));
        Assert.True(completed, "Capture did not finish validation within five seconds.");
        return Assert.IsType<PullRecord>(service.GetReplaySourceSnapshot().LastCompletedPull);
    }

    private static ActorProbeSnapshot CreateActor(
        string name,
        ulong gameObjectId,
        ObjectKind objectKind = ObjectKind.Pc,
        bool isDead = false,
        ulong ownerId = 0,
        float hitboxRadius = 1,
        bool isOmnidirectional = false,
        bool isOmnidirectionalityKnown = true) =>
        new(
            ObjectIndex: 0,
            Name: name,
            ObjectKind: objectKind,
            EntityId: (uint)gameObjectId,
            GameObjectId: gameObjectId,
            OwnerId: ownerId,
            DataId: 0,
            BaseId: 0,
            Position: Vector3.Zero,
            Rotation: 0,
            HitboxRadius: hitboxRadius,
            IsDead: isDead,
            IsTargetable: true,
            TargetObjectId: 0,
            CurrentHp: isDead ? 0u : 100u,
            MaxHp: 100,
            CurrentMp: 100,
            MaxMp: 100,
            ClassJobId: 1,
            Level: 100,
            IsCasting: false,
            IsCastInterruptible: false,
            CastActionId: 0,
            CastTargetObjectId: 0,
            CurrentCastTime: 0,
            TotalCastTime: 0,
            IsOmnidirectional: isOmnidirectional,
            IsOmnidirectionalityKnown: isOmnidirectionalityKnown,
            Statuses: [],
            StatusCount: 0);

    private static ActionEffectTargetRecord CreateDamageTarget(ulong targetObjectId, ushort amount) =>
        new()
        {
            TargetObjectId = targetObjectId,
            Entries =
            [
                new ActionEffectEntryRecord
                {
                    Index = 0,
                    Kind = ActionEffectKind.Damage,
                    RawType = ActionEffectDecoder.DamageType,
                    Param0 = 0,
                    Param1 = 0,
                    Param2 = 0,
                    Param3 = 0,
                    Param4 = 0,
                    Value = amount,
                    Amount = amount,
                    IsCritical = false,
                    IsDirectHit = false,
                },
            ],
        };

    private static string ExpectedExportFileName(PullRecord record) =>
        $"{record.StartedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture)}_{record.CaptureId:D}.json.gz";

    private static string CreateExportDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "RaidDebrief.Plugin.Tests",
            Guid.NewGuid().ToString("N"));

    private static string ReadCompressedJson(string path)
    {
        using var input = File.OpenRead(path);
        using var compressed = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(
            compressed,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static void DeleteExportDirectory(string exportDirectory)
    {
        if (Directory.Exists(exportDirectory))
        {
            Directory.Delete(exportDirectory, recursive: true);
        }
    }


    private sealed class FakeDutyState : IDutyState
    {
        public event IDutyState.DutyStartedDelegate? DutyStarted;

        public event IDutyState.DutyWipedDelegate? DutyWiped;

        public event IDutyState.DutyRecommencedDelegate? DutyRecommenced;

        public event IDutyState.DutyCompletedDelegate? DutyCompleted;

        public RowRef<ContentFinderCondition> ContentFinderCondition => default;

        public bool IsDutyStarted { get; private set; }

        public void Raise(ObservedEventType eventType)
        {
            var eventArgs = new FakeDutyStateEventArgs();
            switch (eventType)
            {
                case ObservedEventType.DutyStarted:
                    this.IsDutyStarted = true;
                    this.DutyStarted?.Invoke(eventArgs);
                    break;
                case ObservedEventType.DutyWiped:
                    this.DutyWiped?.Invoke(eventArgs);
                    break;
                case ObservedEventType.DutyRecommenced:
                    this.DutyRecommenced?.Invoke(eventArgs);
                    break;
                case ObservedEventType.DutyCompleted:
                    this.IsDutyStarted = false;
                    this.DutyCompleted?.Invoke(eventArgs);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(eventType), eventType, null);
            }
        }
    }

    private sealed class FakeDutyStateEventArgs : IDutyStateEventArgs
    {
        public RowRef<ContentFinderCondition> ContentFinderCondition => default;

        public RowRef<TerritoryType> TerritoryType => default;

        public uint EventHandlerId => 0;
    }

    private sealed class FakePluginLog : IPluginLog
    {
        public ILogger Logger => null!;

        public LogEventLevel MinimumLogLevel { get; set; }

        public List<string> Messages { get; } = [];

        public void Fatal(string messageTemplate, params object[] values) { }

        public void Fatal(Exception? exception, string messageTemplate, params object[] values) { }

        public void Error(string messageTemplate, params object[] values) { }

        public void Error(Exception? exception, string messageTemplate, params object[] values) { }

        public void Warning(string messageTemplate, params object[] values) { }

        public void Warning(Exception? exception, string messageTemplate, params object[] values) { }

        public void Information(string messageTemplate, params object[] values) =>
            this.Messages.Add(messageTemplate);

        public void Information(Exception? exception, string messageTemplate, params object[] values) =>
            this.Messages.Add(messageTemplate);

        public void Info(string messageTemplate, params object[] values) { }

        public void Info(Exception? exception, string messageTemplate, params object[] values) { }

        public void Debug(string messageTemplate, params object[] values) { }

        public void Debug(Exception? exception, string messageTemplate, params object[] values) { }

        public void Verbose(string messageTemplate, params object[] values) { }

        public void Verbose(Exception? exception, string messageTemplate, params object[] values) { }

        public void Write(
            LogEventLevel level,
            Exception? exception,
            string messageTemplate,
            params object[] values)
        {
        }
    }
}
