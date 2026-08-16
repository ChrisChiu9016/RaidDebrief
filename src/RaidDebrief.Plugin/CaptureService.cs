using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.DutyState;
using Dalamud.Plugin.Services;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed class CaptureService : IDisposable
{
    private const long SampleIntervalMilliseconds = 100;
    private readonly object gate = new();
    private readonly string developerExportDirectory;
    private readonly Action<string, PullRecord> exportCapture;
    private readonly Action<PullRecord> validateCapture;
    private readonly Func<PullRecord, long?, DebriefSummary> analyzeDebrief;
    private readonly Func<uint, uint, RecordedActionName?> resolveActionName;
    private readonly IDutyState dutyState;
    private readonly IPluginLog log;
    private readonly WaymarkReader waymarkReader;
    private readonly TargetMarkerReader targetMarkerReader;
    private readonly Action<bool> automaticCaptureChanged;
    private readonly AutomaticPullLifecycle automaticLifecycle;
    private readonly long lifecycleStartedTimestamp = Stopwatch.GetTimestamp();

    private ActiveCapture? activeCapture;
    private PullRecord? lastCompletedPull;
    private PullEndReason? lastCompletedEndReason;
    private DebriefSummary? lastCompletedDebrief;
    private Task? backgroundTask;
    private Task? developerExportTask;
    private long finalizationGeneration;
    private ReplaySourceFinalizationState replaySourceFinalizationState;
    private Guid? replaySourceFinalizationCaptureId;
    private string? replaySourceFinalizationError;
    private bool disposed;
    private bool automaticCaptureEnabled;
    private bool isInDutyInstance;
    private bool activeCaptureIsAutomatic;
    private bool actionEffectCaptureAvailable;
    private long completedPullCount;
    private long nextAutomaticPullOrdinal;
    private long nextActiveCaptureEpoch;
    private long activeCaptureEpoch;
    private long? activePullOrdinal;
    private FrameworkCaptureSample? pendingFrameworkSample;
    private long sampleCount;
    private int actorCount;
    private long gapCount;
    private long rejectedActorSampleCount;
    private int eventCount;
    private int waymarkFrameCount;
    private int actionEffectCount;
    private ActionEffectRecord? lastActionEffectRecord;
    private string? lastActionEffectText;
    private long waymarkReadFailureCount;
    private bool waymarkReaderChecked;
    private bool waymarkReaderAvailable;
    private int targetMarkerFrameCount;
    private long targetMarkerReadFailureCount;
    private bool targetMarkerReaderChecked;
    private bool targetMarkerReaderAvailable;
    private double averageSampleIntervalMilliseconds;
    private double lastSamplingMilliseconds;
    private double maximumSamplingMilliseconds;
    private double? lastSerializationMilliseconds;
    private string statusMessage = "尚未開始擷取。";
    private ObservedEvent? lastEventRecord;
    private string? lastEventText;
    private long diagnosticsFormatCount;
    private string? lastWaymarkError;
    private string? lastTargetMarkerError;
    private string? lastExportPath;
    private string? lastExportError;
    private WaymarkState[] latestWaymarks = [];

    public CaptureService(
        string developerExportDirectory,
        IDutyState dutyState,
        IPluginLog log,
        WaymarkReader waymarkReader,
        TargetMarkerReader targetMarkerReader,
        bool automaticCaptureEnabled,
        Action<bool> automaticCaptureChanged,
        AutomaticPullLifecycle? automaticLifecycle = null,
        Action<string, PullRecord>? exportCapture = null,
        Action<PullRecord>? validateCapture = null,
        Func<PullRecord, long?, DebriefSummary>? analyzeDebrief = null,
        Func<uint, uint, RecordedActionName?>? resolveActionName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(developerExportDirectory);
        this.developerExportDirectory = Path.GetFullPath(developerExportDirectory);
        this.exportCapture = exportCapture ?? CaptureJson.Save;
        this.validateCapture = validateCapture ?? PullRecordValidator.Validate;
        this.analyzeDebrief = analyzeDebrief ?? new DebriefAnalyzer().Analyze;
        this.resolveActionName = resolveActionName ?? ((_, _) => null);
        this.dutyState = dutyState;
        this.log = log;
        this.waymarkReader = waymarkReader;
        this.targetMarkerReader = targetMarkerReader;
        this.automaticCaptureEnabled = automaticCaptureEnabled;
        this.automaticCaptureChanged = automaticCaptureChanged;
        this.statusMessage = automaticCaptureEnabled
            ? "自動 Pull 擷取已啟用；等待進入 instance。"
            : "手動擷取模式已啟用。";
        this.automaticLifecycle = automaticLifecycle ?? new AutomaticPullLifecycle();
        this.dutyState.DutyStarted += this.OnDutyStarted;
        this.dutyState.DutyWiped += this.OnDutyWiped;
        this.dutyState.DutyRecommenced += this.OnDutyRecommenced;
        this.dutyState.DutyCompleted += this.OnDutyCompleted;
    }

    public CaptureStatus Status
    {
        get
        {
            lock (this.gate)
            {
                return new CaptureStatus(
                    this.activeCapture is not null,
                    this.backgroundTask is { IsCompleted: false },
                    this.developerExportTask is { IsCompleted: false },
                    this.automaticCaptureEnabled,
                    this.isInDutyInstance,
                    this.automaticLifecycle.IsArmedForCombatStart,
                    this.automaticLifecycle.State,
                    this.automaticLifecycle.LastEndReason,
                    this.GetCombatEndRemainingMilliseconds(),
                    this.completedPullCount,
                    this.lastCompletedPull?.CaptureId,
                    this.sampleCount,
                    this.actorCount,
                    this.gapCount,
                    this.rejectedActorSampleCount,
                    this.eventCount,
                    this.GetLastEventText(),
                    this.actionEffectCount,
                    this.GetLastActionEffectText(),
                    this.waymarkFrameCount,
                    this.waymarkReadFailureCount,
                    this.waymarkReaderChecked,
                    this.waymarkReaderAvailable,
                    this.lastWaymarkError,
                    this.latestWaymarks,
                    this.targetMarkerFrameCount,
                    this.targetMarkerReadFailureCount,
                    this.targetMarkerReaderChecked,
                    this.targetMarkerReaderAvailable,
                    this.lastTargetMarkerError,
                    this.averageSampleIntervalMilliseconds,
                    this.lastSamplingMilliseconds,
                    this.maximumSamplingMilliseconds,
                    this.lastSerializationMilliseconds,
                    this.developerExportDirectory,
                    this.lastExportPath,
                    this.lastExportError,
                    this.statusMessage);
            }
        }
    }
    public ReplaySourceSnapshot GetReplaySourceSnapshot()
    {
        lock (this.gate)
        {
            return new ReplaySourceSnapshot(
                this.finalizationGeneration,
                this.replaySourceFinalizationState,
                this.replaySourceFinalizationCaptureId,
                this.replaySourceFinalizationError,
                this.completedPullCount,
                this.lastCompletedPull,
                this.lastCompletedEndReason,
                this.lastCompletedDebrief);
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (this.gate)
            {
                return this.activeCapture is not null;
            }
        }
    }

    internal long DiagnosticsFormatCount
    {
        get
        {
            lock (this.gate)
            {
                return this.diagnosticsFormatCount;
            }
        }
    }

    public bool SetAutomaticCaptureEnabled(bool enabled)
    {
        lock (this.gate)
        {
            this.ThrowIfDisposed();
            if (this.activeCapture is not null || this.backgroundTask is { IsCompleted: false })
            {
                return false;
            }

            if (this.automaticCaptureEnabled == enabled)
            {
                return true;
            }

            this.automaticCaptureEnabled = enabled;
            this.automaticLifecycle.Reset();
            this.statusMessage = enabled
                ? this.isInDutyInstance
                    ? "自動 Pull 擷取已啟用；等待 InCombat。"
                    : "自動 Pull 擷取已啟用；等待進入 instance。"
                : "手動擷取模式已啟用。";
        }

        this.automaticCaptureChanged(enabled);
        this.log.Information(
            "Raid Debrief automatic Pull capture {State}.",
            enabled ? "enabled" : "disabled");
        return true;
    }


    public bool Start(uint territoryType, uint mapId, uint instance)
    {
        lock (this.gate)
        {
            this.ThrowIfDisposed();
            if (this.automaticCaptureEnabled
                || this.activeCapture is not null
                || this.backgroundTask is { IsCompleted: false })
            {
                return false;
            }

            return this.BeginCapture(territoryType, mapId, instance, isAutomatic: false);
        }
    }

    public bool StopAndExport()
    {
        lock (this.gate)
        {
            this.ThrowIfDisposed();
            if (this.automaticCaptureEnabled
                || this.activeCapture is null
                || this.backgroundTask is { IsCompleted: false })
            {
                return false;
            }

            this.BeginFinalize(
                isAutomatic: false,
                exportJson: true,
                "手動擷取已停止；正在驗證並背景匯出壓縮 JSON。");
            return true;
        }
    }

    public bool ExportLastCompletedPull()
    {
        PullRecord record;
        lock (this.gate)
        {
            this.ThrowIfDisposed();
            if (this.lastCompletedPull is not { } lastCompletedPull
                || this.backgroundTask is { IsCompleted: false }
                || this.developerExportTask is { IsCompleted: false })
            {
                return false;
            }

            record = lastCompletedPull;
            this.lastExportError = null;
            this.developerExportTask = Task.Run(
                () => this.ExportCaptureToDeveloperDirectory(
                    record,
                    "Runtime LastCompletedPull",
                    updateStatusMessage: false));
        }

        return true;
    }

    public FrameworkCaptureDecision BeginFrameworkUpdate(
        bool inCombat,
        uint territoryType,
        uint mapId,
        uint instance,
        bool isInDutyInstance = true)
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return default;
            }

            if (this.pendingFrameworkSample is not null)
            {
                throw new InvalidOperationException(
                    "The pending framework sample must be submitted or cancelled before the next update.");
            }

            var wasInDutyInstance = this.isInDutyInstance;
            this.isInDutyInstance = isInDutyInstance;
            var lifecycleCommand = AutomaticPullCommand.None;
            if (this.automaticCaptureEnabled)
            {
                var lifecycleTimestampMilliseconds = this.GetLifecycleElapsedMilliseconds();
                if (!isInDutyInstance)
                {
                    if (this.activeCapture is not null)
                    {
                        lifecycleCommand = this.automaticLifecycle.EndImmediately(
                            lifecycleTimestampMilliseconds,
                            PullEndReason.InstanceExited);
                        if (lifecycleCommand == AutomaticPullCommand.Finalize)
                        {
                            this.BeginFinalize(
                                isAutomatic: true,
                                exportJson: false,
                                "已離開 instance；正在背景完成並驗證自動 Pull。");
                        }

                        return default;
                    }

                    if (this.backgroundTask is { IsCompleted: false })
                    {
                        return default;
                    }

                    if (this.automaticLifecycle.State == AutomaticPullState.Idle
                        && this.automaticLifecycle.IsArmedForCombatStart)
                    {
                        this.automaticLifecycle.Reset();
                    }

                    this.statusMessage = "自動 Pull 擷取等待進入 instance；instance 外不會開始。";
                    return default;
                }

                if (!wasInDutyInstance)
                {
                    this.statusMessage = "已進入 instance；等待 InCombat。";
                    this.log.Information(
                        "Raid Debrief automatic Pull capture entered an instanced duty.");
                }

                if (this.activeCapture is null
                    && this.backgroundTask is { IsCompleted: false })
                {
                    if (this.automaticLifecycle.State == AutomaticPullState.Finalizing)
                    {
                        this.ObserveAutomaticLifecycle(lifecycleTimestampMilliseconds, inCombat);
                    }

                    return default;
                }

                lifecycleCommand = this.ObserveAutomaticLifecycle(
                    lifecycleTimestampMilliseconds,
                    inCombat);
                if (lifecycleCommand == AutomaticPullCommand.StartRecording)
                {
                    this.BeginCapture(territoryType, mapId, instance, isAutomatic: true);
                }
            }

            if (lifecycleCommand == AutomaticPullCommand.Finalize)
            {
                this.BeginFinalize(
                    isAutomatic: true,
                    exportJson: false,
                    "自動 Pull 已脫戰；正在背景完成並驗證。");
                return default;
            }

            if (this.activeCapture is null)
            {
                return default;
            }

            var preparation = this.activeCapture.PrepareFrame();
            if (!preparation.ShouldSample)
            {
                return default;
            }

            var sample = new FrameworkCaptureSample(
                this.activeCaptureEpoch,
                inCombat,
                preparation);
            this.pendingFrameworkSample = sample;
            return new FrameworkCaptureDecision(true, sample);
        }
    }

    public void SubmitFrameworkSample(
        in FrameworkCaptureSample sample,
        ReadOnlySpan<ActorProbeSnapshot> actors,
        ReadOnlySpan<PartyMemberProbeSnapshot> partyMembers)
    {
        lock (this.gate)
        {
            this.RequirePendingFrameworkSample(sample);
            if (this.activeCapture is null
                || sample.CaptureEpoch != this.activeCaptureEpoch)
            {
                this.pendingFrameworkSample = null;
                return;
            }

            var startedAt = Stopwatch.GetTimestamp();
            FrameRecordResult result;
            try
            {
                result = this.activeCapture.RecordPreparedFrame(
                    sample.Preparation,
                    actors,
                    partyMembers,
                    sample.InCombat,
                    this.waymarkReader,
                    this.targetMarkerReader);
            }
            finally
            {
                this.pendingFrameworkSample = null;
            }

            this.UpdateSampleStatus(result, startedAt);
        }
    }

    public void CancelFrameworkSample(in FrameworkCaptureSample sample)
    {
        lock (this.gate)
        {
            this.RequirePendingFrameworkSample(sample);
            if (this.activeCapture is not null
                && sample.CaptureEpoch == this.activeCaptureEpoch)
            {
                this.activeCapture.CancelFrame(sample.Preparation);
            }

            this.pendingFrameworkSample = null;
        }
    }

    internal void RecordFrameworkSnapshot(
        ReadOnlySpan<ActorProbeSnapshot> actors,
        ReadOnlySpan<PartyMemberProbeSnapshot> partyMembers,
        bool inCombat,
        uint territoryType,
        uint mapId,
        uint instance,
        bool isInDutyInstance = true)
    {
        var decision = this.BeginFrameworkUpdate(
            inCombat,
            territoryType,
            mapId,
            instance,
            isInDutyInstance);
        if (decision.SampleRequested)
        {
            this.SubmitFrameworkSample(
                decision.Sample,
                actors,
                partyMembers);
        }
    }

    private void UpdateSampleStatus(
        in FrameRecordResult result,
        long startedAt)
    {
        if (!result.Recorded || this.activeCapture is null)
        {
            return;
        }

        this.sampleCount = this.activeCapture.Frames.Count;
        this.actorCount = this.activeCapture.Actors.Count;
        this.gapCount += result.Gaps;
        this.rejectedActorSampleCount += result.RejectedActorSamples;
        this.eventCount = this.activeCapture.Events.Count;
        this.UpdateLastEvent(this.activeCapture.LastEvent);
        this.waymarkReaderChecked = true;
        this.waymarkReaderAvailable = result.WaymarkReadSucceeded;
        if (!result.WaymarkReadSucceeded)
        {
            this.waymarkReadFailureCount++;
        }

        this.lastWaymarkError = this.waymarkReader.LastError;
        this.waymarkFrameCount = this.activeCapture.WaymarkFrames.Count;
        this.latestWaymarks = this.activeCapture.LatestWaymarks;
        this.targetMarkerReaderChecked = true;
        this.targetMarkerReaderAvailable = result.TargetMarkerReadSucceeded;
        if (!result.TargetMarkerReadSucceeded)
        {
            this.targetMarkerReadFailureCount++;
        }

        this.lastTargetMarkerError = this.targetMarkerReader.LastError;
        this.targetMarkerFrameCount = this.activeCapture.TargetMarkerFrames.Count;
        this.averageSampleIntervalMilliseconds = this.activeCapture.AverageSampleIntervalMilliseconds;
        this.lastSamplingMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        this.maximumSamplingMilliseconds = Math.Max(
            this.maximumSamplingMilliseconds,
            this.lastSamplingMilliseconds);
    }

    public void SetActionEffectCaptureAvailability(bool available)
    {
        lock (this.gate)
        {
            this.actionEffectCaptureAvailable = available;
            if (!available)
            {
                this.activeCapture?.MarkActionEffectCaptureUnavailable();
            }
        }
    }

    public void RecordActionEffect(
        uint globalSequence,
        uint actionId,
        byte actionType,
        ulong sourceObjectId,
        uint sourceEntityId,
        ulong? animationTargetObjectId,
        ActionEffectTargetRecord[] targets)
    {
        lock (this.gate)
        {
            if (this.activeCapture is null)
            {
                return;
            }

            var actionEffect = this.activeCapture.AddActionEffect(
                globalSequence,
                actionId,
                actionType,
                sourceObjectId,
                sourceEntityId,
                animationTargetObjectId,
                targets);
            this.actionEffectCount = this.activeCapture.ActionEffects.Count;
            this.lastActionEffectRecord = actionEffect;
            this.lastActionEffectText = null;
        }
    }

    private bool BeginCapture(
        uint territoryType,
        uint mapId,
        uint instance,
        bool isAutomatic)
    {
        if (this.activeCapture is not null || this.backgroundTask is { IsCompleted: false })
        {
            return false;
        }

        this.activeCapture = new ActiveCapture(
            territoryType,
            mapId,
            instance,
            this.actionEffectCaptureAvailable,
            this.resolveActionName);
        this.activeCaptureEpoch = ++this.nextActiveCaptureEpoch;
        this.pendingFrameworkSample = null;
        this.activeCaptureIsAutomatic = isAutomatic;
        this.activePullOrdinal = isAutomatic
            ? ++this.nextAutomaticPullOrdinal
            : null;
        this.sampleCount = 0;
        this.actorCount = 0;
        this.gapCount = 0;
        this.rejectedActorSampleCount = 0;
        this.eventCount = 0;
        this.lastEventRecord = null;
        this.lastEventText = null;
        this.actionEffectCount = 0;
        this.lastActionEffectRecord = null;
        this.lastActionEffectText = null;
        this.diagnosticsFormatCount = 0;
        this.waymarkFrameCount = 0;
        this.waymarkReadFailureCount = 0;
        this.waymarkReaderChecked = false;
        this.waymarkReaderAvailable = false;
        this.lastWaymarkError = null;
        this.latestWaymarks = [];
        this.targetMarkerFrameCount = 0;
        this.targetMarkerReadFailureCount = 0;
        this.targetMarkerReaderChecked = false;
        this.targetMarkerReaderAvailable = false;
        this.lastTargetMarkerError = null;
        this.averageSampleIntervalMilliseconds = 0;
        this.lastSamplingMilliseconds = 0;
        this.maximumSamplingMilliseconds = 0;
        this.lastSerializationMilliseconds = null;
        this.statusMessage = isAutomatic
            ? "自動 Pull 擷取中；Framework thread 每 100 ms 取樣一次。"
            : "手動擷取中；Framework thread 每 100 ms 取樣一次。";
        this.log.Information(
            "Raid Debrief {Mode} capture started in territory {TerritoryType}, map {MapId}, instance {Instance}.",
            isAutomatic ? "automatic Pull" : "manual",
            territoryType,
            mapId,
            instance);
        return true;
    }

    private void BeginFinalize(bool isAutomatic, bool exportJson, string message)
    {
        if (this.activeCapture is null || this.activeCaptureIsAutomatic != isAutomatic)
        {
            throw new InvalidOperationException("Capture mode changed while a pull was active.");
        }

        var record = this.activeCapture.Complete();
        var completedEndReason = isAutomatic
            ? this.automaticLifecycle.LastEndReason
            : null;
        var pullNumber = this.activePullOrdinal;
        var finalizationGeneration = this.BeginReplayFinalization(record);
        var endReason = completedEndReason?.ToString() ?? "ManualStop";
        this.activeCapture = null;
        this.activeCaptureEpoch = 0;
        this.pendingFrameworkSample = null;
        this.activeCaptureIsAutomatic = false;
        this.activePullOrdinal = null;
        this.statusMessage = message;
        this.log.Information(
            "Raid Debrief {Mode} capture {CaptureId} finalizing because {EndReason} with {FrameCount} frames, {EventCount} events, and {ActionEffectCount} Action Effects.",
            isAutomatic ? "automatic Pull" : "manual",
            record.CaptureId,
            endReason,
            record.Frames.Length,
            record.Events.Length,
            record.ActionEffects.Length);
        this.backgroundTask = Task.Run(
            () => this.FinalizeCapture(
                record,
                isAutomatic,
                completedEndReason,
                pullNumber,
                endReason,
                exportJson,
                finalizationGeneration));
    }

    private void OnDutyStarted(IDutyStateEventArgs _) =>
        this.RecordDutyEvent(ObservedEventType.DutyStarted);

    private void OnDutyWiped(IDutyStateEventArgs _) =>
        this.RecordDutyEvent(ObservedEventType.DutyWiped);

    private void OnDutyRecommenced(IDutyStateEventArgs _) =>
        this.RecordDutyEvent(ObservedEventType.DutyRecommenced);

    private void OnDutyCompleted(IDutyStateEventArgs _) =>
        this.RecordDutyEvent(ObservedEventType.DutyCompleted);

    private void RecordDutyEvent(ObservedEventType type)
    {
        lock (this.gate)
        {
            if (this.activeCapture is null)
            {
                return;
            }

            var observedEvent = this.activeCapture.AddDutyEvent(type);
            this.eventCount = this.activeCapture.Events.Count;
            this.UpdateLastEvent(observedEvent);

            if (!this.automaticCaptureEnabled || !this.activeCaptureIsAutomatic)
            {
                return;
            }

            var reason = type switch
            {
                ObservedEventType.DutyWiped => PullEndReason.DutyWiped,
                ObservedEventType.DutyCompleted => PullEndReason.DutyCompleted,
                _ => (PullEndReason?)null,
            };
            if (reason is not { } endReason
                || this.automaticLifecycle.EndImmediately(
                    this.GetLifecycleElapsedMilliseconds(),
                    endReason) != AutomaticPullCommand.Finalize)
            {
                return;
            }

            this.BeginFinalize(
                isAutomatic: true,
                exportJson: false,
                $"自動 Pull 因 {endReason} 結束；正在背景完成並驗證。");
        }
    }


    public void Dispose()
    {
        Task? task;
        Task? exportTask;
        PullRecord? pendingRecord = null;
        var pendingRecordIsAutomatic = false;
        PullEndReason? pendingCompletedEndReason = null;
        string? pendingFinalizeReason = null;
        var pendingFinalizationGeneration = 0L;
        long? pendingPullNumber = null;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            if (this.activeCapture is not null)
            {
                pendingRecordIsAutomatic = this.activeCaptureIsAutomatic;
                pendingPullNumber = this.activePullOrdinal;
                pendingFinalizeReason = PullEndReason.PluginReload.ToString();
                pendingCompletedEndReason = pendingRecordIsAutomatic
                    ? PullEndReason.PluginReload
                    : null;
                if (pendingRecordIsAutomatic)
                {
                    this.automaticLifecycle.EndImmediately(
                        this.GetLifecycleElapsedMilliseconds(),
                        PullEndReason.PluginReload);
                }

                pendingRecord = this.activeCapture.Complete();
                pendingFinalizationGeneration = this.BeginReplayFinalization(pendingRecord);
                this.activeCapture = null;
                this.activeCaptureIsAutomatic = false;
                this.activePullOrdinal = null;
            }

            task = this.backgroundTask;
            exportTask = this.developerExportTask;
        }

        this.dutyState.DutyStarted -= this.OnDutyStarted;
        this.dutyState.DutyWiped -= this.OnDutyWiped;
        this.dutyState.DutyRecommenced -= this.OnDutyRecommenced;
        this.dutyState.DutyCompleted -= this.OnDutyCompleted;

        task?.GetAwaiter().GetResult();
        exportTask?.GetAwaiter().GetResult();
        if (pendingRecord is not null)
        {
            this.log.Information(
                "Raid Debrief capture {CaptureId} is being finalized because the plugin is unloading.",
                pendingRecord.CaptureId);
            this.FinalizeCapture(
                pendingRecord,
                pendingRecordIsAutomatic,
                pendingCompletedEndReason,
                pendingPullNumber,
                pendingFinalizeReason!,
                exportJson: false,
                pendingFinalizationGeneration);
        }
    }

    private long BeginReplayFinalization(PullRecord record)
    {
        this.finalizationGeneration++;
        this.replaySourceFinalizationState = ReplaySourceFinalizationState.Finalizing;
        this.replaySourceFinalizationCaptureId = record.CaptureId;
        this.replaySourceFinalizationError = null;
        return this.finalizationGeneration;
    }

    private void FinalizeCapture(
        PullRecord record,
        bool isAutomatic,
        PullEndReason? completedEndReason,
        long? pullNumber,
        string finalizeReason,
        bool exportJson,
        long finalizationGeneration)
    {
        DebriefSummary debrief;
        try
        {
            this.validateCapture(record);
            debrief = this.analyzeDebrief(record, pullNumber);
        }
        catch (Exception exception)
        {
            lock (this.gate)
            {
                this.statusMessage = $"Pull 完成、驗證或 Debrief 分析失敗：{exception.Message}";
                if (this.finalizationGeneration == finalizationGeneration)
                {
                    this.replaySourceFinalizationState = ReplaySourceFinalizationState.Failed;
                    this.replaySourceFinalizationError = exception.Message;
                }
                if (isAutomatic)
                {
                    this.automaticLifecycle.Reset();
                }
            }
            this.log.Error(
                exception,
                "Raid Debrief {Mode} capture {CaptureId} finalization, validation, or Debrief analysis failed.",
                isAutomatic ? "automatic Pull" : "manual",
                record.CaptureId);
            return;
        }

        lock (this.gate)
        {
            this.lastCompletedPull = record;
            this.lastCompletedEndReason = completedEndReason;
            this.lastCompletedDebrief = debrief;
            this.completedPullCount++;
            this.replaySourceFinalizationState = ReplaySourceFinalizationState.Succeeded;
            this.replaySourceFinalizationError = null;
            if (isAutomatic)
            {
                this.automaticLifecycle.MarkCompleted();
            }
            this.statusMessage =
                $"Pull 已完成並通過驗證：{record.Frames.Length:N0} samples、{record.Actors.Length:N0} actors、{record.Events.Length:N0} events、{record.ActionEffects.Length:N0} Action Effects、{record.ActionNames.Length:N0} Action names、{record.WaymarkFrames.Length:N0} Waymark frames、{record.TargetMarkerFrames.Length:N0} Target Marker frames。";
        }
        this.log.Information(
            "Raid Debrief {Mode} capture {CaptureId} finalized and validated because {FinalizeReason}.",
            isAutomatic ? "automatic Pull" : "manual",
            record.CaptureId,
            finalizeReason);

        if (!exportJson)
        {
            return;
        }

        this.ExportCaptureToDeveloperDirectory(
            record,
            "manual",
            updateStatusMessage: true);
    }

    private void ExportCaptureToDeveloperDirectory(
        PullRecord record,
        string source,
        bool updateStatusMessage)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var captureStartedAtUtc = record.StartedAtUtc.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH-mm-ss",
            CultureInfo.InvariantCulture);
        var exportPath = Path.Combine(
            this.developerExportDirectory,
            $"{captureStartedAtUtc}_{record.CaptureId:D}.json.gz");
        try
        {
            this.exportCapture(exportPath, record);
            var serializationMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            lock (this.gate)
            {
                this.lastSerializationMilliseconds = serializationMilliseconds;
                this.lastExportPath = exportPath;
                this.lastExportError = null;
                if (updateStatusMessage)
                {
                    this.statusMessage =
                        $"Pull 已完成並通過驗證；Developer/Test 壓縮 JSON 已匯出至 {exportPath}。";
                }
            }
            this.log.Information(
                "Raid Debrief {Source} capture {CaptureId} exported to {ExportPath} in {SerializationMilliseconds:F2} ms.",
                source,
                record.CaptureId,
                exportPath,
                serializationMilliseconds);
        }
        catch (Exception exception)
        {
            lock (this.gate)
            {
                this.lastExportError = exception.Message;
                if (updateStatusMessage)
                {
                    this.statusMessage =
                        $"Pull 已保留在 LastCompletedPull，但 Developer/Test JSON 匯出失敗：{exception.Message}";
                }
            }
            this.log.Error(
                exception,
                "Raid Debrief {Source} capture {CaptureId} developer JSON export failed.",
                source,
                record.CaptureId);
        }
    }

    private long GetLifecycleElapsedMilliseconds() =>
        (long)Stopwatch.GetElapsedTime(this.lifecycleStartedTimestamp).TotalMilliseconds;

    private AutomaticPullCommand ObserveAutomaticLifecycle(
        long timestampMilliseconds,
        bool inCombat)
    {
        var previousState = this.automaticLifecycle.State;
        var wasArmed = this.automaticLifecycle.IsArmedForCombatStart;
        var previousDeadline = this.automaticLifecycle.CombatEndDeadlineMilliseconds;
        var command = this.automaticLifecycle.Observe(timestampMilliseconds, inCombat);

        if (!wasArmed && this.automaticLifecycle.IsArmedForCombatStart)
        {
            this.log.Information(
                "Raid Debrief automatic Pull lifecycle armed after observing InCombat=false.");
        }

        if (previousDeadline is null
            && this.automaticLifecycle.CombatEndDeadlineMilliseconds is { } deadline)
        {
            this.log.Information(
                "Raid Debrief automatic Pull combat-end debounce started; lifecycle deadline {DeadlineMilliseconds} ms.",
                deadline);
        }
        else if (previousDeadline is not null
                 && this.automaticLifecycle.CombatEndDeadlineMilliseconds is null
                 && command == AutomaticPullCommand.None
                 && inCombat)
        {
            this.log.Information(
                "Raid Debrief automatic Pull combat-end debounce cancelled by combat re-entry.");
        }

        if (previousState == AutomaticPullState.Completed
            && this.automaticLifecycle.State == AutomaticPullState.Idle)
        {
            this.log.Information(
                "Raid Debrief automatic Pull lifecycle returned to Idle.");
        }

        return command;
    }

    private long? GetCombatEndRemainingMilliseconds()
    {
        if (this.automaticLifecycle.CombatEndDeadlineMilliseconds is not { } deadline)
        {
            return null;
        }

        return Math.Max(0, deadline - this.GetLifecycleElapsedMilliseconds());
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
    }

    private void RequirePendingFrameworkSample(in FrameworkCaptureSample sample)
    {
        if (this.pendingFrameworkSample is not { } pending || pending != sample)
        {
            throw new InvalidOperationException("The framework sample is not pending.");
        }
    }

    private void UpdateLastEvent(ObservedEvent? observedEvent)
    {
        if (ReferenceEquals(this.lastEventRecord, observedEvent))
        {
            return;
        }

        this.lastEventRecord = observedEvent;
        this.lastEventText = null;
    }

    private string? GetLastEventText()
    {
        if (this.lastEventRecord is null)
        {
            return null;
        }

        if (this.lastEventText is null)
        {
            this.lastEventText = FormatEvent(this.lastEventRecord);
            this.diagnosticsFormatCount++;
        }

        return this.lastEventText;
    }

    private string? GetLastActionEffectText()
    {
        if (this.lastActionEffectRecord is null)
        {
            return null;
        }

        if (this.lastActionEffectText is null)
        {
            this.lastActionEffectText = FormatActionEffect(this.lastActionEffectRecord);
            this.diagnosticsFormatCount++;
        }

        return this.lastActionEffectText;
    }

    private static string FormatEvent(ObservedEvent observedEvent)
    {
        var actor = observedEvent.StableActorId is { } stableActorId
            ? $" actor {stableActorId}"
            : string.Empty;
        var action = observedEvent.ActionId is { } actionId
            ? $" action {actionId}"
            : string.Empty;
        var status = observedEvent.StatusId is { } statusId
            ? $" status {statusId}"
            : string.Empty;
        var state = observedEvent.State is { } value
            ? $" state {value}"
            : string.Empty;
        return $"{observedEvent.TimestampMilliseconds} ms {observedEvent.Type}{actor}{action}{status}{state} [{observedEvent.Source}]";
    }
    private static string FormatActionEffect(ActionEffectRecord actionEffect)
    {
        var entryCount = 0;
        foreach (var target in actionEffect.Targets)
        {
            entryCount += target.Entries.Length;
        }

        return $"{actionEffect.TimestampMilliseconds} ms action {actionEffect.ActionId} source 0x{actionEffect.SourceObjectId:X8} / {actionEffect.Targets.Length} targets / {entryCount} effects";
    }



    private sealed class ActiveCapture
    {
        private readonly Dictionary<ulong, int> stableActorIds = new();
        private const long ActionNameRetryIntervalMilliseconds = 500;
        private const int MaximumActionNameResolutionAttempts = 20;

        private readonly PolledEventDetector eventDetector = new();
        private readonly Dictionary<uint, RecordedActionName> actionNames = new();
        private readonly Dictionary<uint, ActionNameRetryState> actionNameRetries = new();
        private readonly Func<uint, uint, RecordedActionName?> resolveActionName;
        private readonly List<uint> pendingActionNameIds = new();
        private readonly Dictionary<uint, uint> pendingActionNameSources = new();
        private readonly WaymarkTimelineBuilder waymarkTimeline = new();
        private readonly WaymarkObservation[] waymarkObservations = new WaymarkObservation[WaymarkReader.MarkerCount];
        private readonly TargetMarkerTimelineBuilder targetMarkerTimeline = new();
        private readonly ulong[] targetMarkerObjectIds = new ulong[TargetMarkerTimelineBuilder.MarkerCount];
        private readonly TargetMarkerObservation[] targetMarkerObservations =
            new TargetMarkerObservation[TargetMarkerTimelineBuilder.MarkerCount];
        private readonly long startedTimestamp = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        private int playerAliasCount;
        private PolledActorObservation[] observations = [];
        private readonly CaptureSamplingScheduler samplingScheduler =
            new(SampleIntervalMilliseconds);
        private bool capturedTargetMarkers;
        private bool omnidirectionalityStateComplete = true;
        private bool castTimingComplete = true;
        private bool statusTimingComplete = true;
        private bool actionEffectCaptureComplete;

        public ActiveCapture(
            uint territoryType,
            uint mapId,
            uint instance,
            bool actionEffectCaptureAvailable,
            Func<uint, uint, RecordedActionName?> resolveActionName)
        {
            this.TerritoryType = territoryType;
            this.MapId = mapId;
            this.Instance = instance;
            this.actionEffectCaptureComplete = actionEffectCaptureAvailable;
            this.resolveActionName = resolveActionName;
        }

        public uint TerritoryType { get; }

        public uint MapId { get; }

        public uint Instance { get; }

        public List<ActorRecord> Actors { get; } = new(32);

        public List<PositionFrame> Frames { get; } = new(600);

        public List<ObservedEvent> Events { get; } = new(128);

        public List<ActionEffectRecord> ActionEffects { get; } = new(256);

        public IReadOnlyList<WaymarkFrame> WaymarkFrames => this.waymarkTimeline.Frames;

        public IReadOnlyList<TargetMarkerFrame> TargetMarkerFrames => this.targetMarkerTimeline.Frames;

        public WaymarkState[] LatestWaymarks { get; private set; } = [];

        public ObservedEvent? LastEvent => this.Events.Count == 0 ? null : this.Events[^1];

        public double AverageSampleIntervalMilliseconds => this.Frames.Count < 2
            ? 0
            : (double)(this.Frames[^1].TimestampMilliseconds - this.Frames[0].TimestampMilliseconds)
                / (this.Frames.Count - 1);

        public void MarkActionEffectCaptureUnavailable() =>
            this.actionEffectCaptureComplete = false;

        public CaptureSamplePreparation PrepareFrame() =>
            this.samplingScheduler.Prepare(this.GetElapsedMilliseconds());

        public void CancelFrame(in CaptureSamplePreparation preparation) =>
            this.samplingScheduler.Cancel(preparation);

        public FrameRecordResult RecordPreparedFrame(
            in CaptureSamplePreparation preparation,
            ReadOnlySpan<ActorProbeSnapshot> actors,
            ReadOnlySpan<PartyMemberProbeSnapshot> partyMembers,
            bool inCombat,
            WaymarkReader waymarkReader,
            TargetMarkerReader targetMarkerReader)
        {
            if (!preparation.ShouldSample)
            {
                throw new InvalidOperationException("A prepared sample is required.");
            }

            this.samplingScheduler.Commit(preparation);
            var timestampMilliseconds = preparation.TimestampMilliseconds;
            this.ResolvePendingActionNames(timestampMilliseconds);

            if (this.observations.Length < actors.Length)
            {
                Array.Resize(ref this.observations, actors.Length);
            }

            var states = new List<ActorStateSample>(actors.Length);
            var observationCount = 0;
            long rejectedActorSamples = 0;
            foreach (var actor in actors)
            {
                if (actor.GameObjectId == 0
                    || !float.IsFinite(actor.Position.X)
                    || !float.IsFinite(actor.Position.Y)
                    || !float.IsFinite(actor.Position.Z)
                    || !float.IsFinite(actor.Rotation)
                    || !float.IsFinite(actor.HitboxRadius)
                    || actor.HitboxRadius < 0
                    || actor.CurrentHp > actor.MaxHp)
                {
                    rejectedActorSamples++;
                    continue;
                }

                this.omnidirectionalityStateComplete &= actor.IsOmnidirectionalityKnown;
                var partyIndex = FindPartyIndex(actor, partyMembers);
                if (actor.IsCasting
                    && (!float.IsFinite(actor.CurrentCastTime)
                        || actor.CurrentCastTime < 0
                        || !float.IsFinite(actor.TotalCastTime)
                        || actor.TotalCastTime <= 0))
                {
                    this.castTimingComplete = false;
                }

                if (actor.IsCasting && actor.CastActionId != 0)
                {
                    this.TryCaptureActionName(
                        actor.CastActionId,
                        actor.EntityId,
                        timestampMilliseconds);
                }

                var statusCount = Math.Min(actor.StatusCount, actor.Statuses.Length);
                for (var statusIndex = 0; statusIndex < statusCount; statusIndex++)
                {
                    var status = actor.Statuses[statusIndex];
                    if (!float.IsFinite(status.RemainingTime) || status.RemainingTime < 0)
                    {
                        this.statusTimingComplete = false;
                    }
                }

                if (!this.stableActorIds.TryGetValue(actor.GameObjectId, out var stableActorId))
                {
                    stableActorId = this.stableActorIds.Count + 1;
                    this.stableActorIds.Add(actor.GameObjectId, stableActorId);
                    this.Actors.Add(new ActorRecord
                    {
                        StableActorId = stableActorId,
                        Name = actor.ObjectKind == ObjectKind.Pc
                            ? $"Player {++this.playerAliasCount}"
                            : actor.Name,
                        ObjectKind = actor.ObjectKind.ToString(),
                        EntityId = actor.EntityId,
                        GameObjectId = actor.GameObjectId,
                        OwnerId = actor.OwnerId,
                        BaseId = actor.BaseId,
                        ClassJobId = actor.ClassJobId,
                        PartyIndex = partyIndex,
                        Level = actor.Level,
                    });
                }
                else if (partyIndex is { } newlyResolvedPartyIndex
                    && this.Actors[stableActorId - 1].PartyIndex is null)
                {
                    this.Actors[stableActorId - 1] = this.Actors[stableActorId - 1] with
                    {
                        PartyIndex = newlyResolvedPartyIndex,
                    };
                }

                states.Add(new ActorStateSample
                {
                    StableActorId = stableActorId,
                    X = actor.Position.X,
                    Y = actor.Position.Y,
                    Z = actor.Position.Z,
                    Rotation = actor.Rotation,
                    HitboxRadius = actor.HitboxRadius,
                    CurrentHp = actor.CurrentHp,
                    MaxHp = actor.MaxHp,
                    BarrierPercentage = actor.BarrierPercentage,
                    IsDead = actor.IsDead,
                    IsTargetable = actor.IsTargetable,
                    IsOmnidirectional = actor.IsOmnidirectional,
                });

                this.observations[observationCount] = new PolledActorObservation(
                    stableActorId,
                    actor.GameObjectId,
                    actor.IsDead,
                    actor.IsCasting,
                    actor.CastActionId,
                    actor.CastTargetObjectId,
                    actor.CurrentCastTime,
                    actor.TotalCastTime,
                    actor.Statuses.AsMemory(0, statusCount));
                observationCount++;
            }

            this.Frames.Add(new PositionFrame
            {
                TimestampMilliseconds = timestampMilliseconds,
                Actors = states.ToArray(),
            });
            this.eventDetector.ObserveFrame(
                timestampMilliseconds,
                this.observations.AsSpan(0, observationCount),
                inCombat,
                this.Events);

            var waymarkReadSucceeded = this.TryCaptureWaymarks(timestampMilliseconds, waymarkReader);
            var targetMarkerReadSucceeded = this.TryCaptureTargetMarkers(
                timestampMilliseconds,
                targetMarkerReader);

            return new FrameRecordResult(
                true,
                preparation.Gaps,
                rejectedActorSamples,
                waymarkReadSucceeded,
                targetMarkerReadSucceeded);
        }

        public ActionEffectRecord AddActionEffect(
            uint globalSequence,
            uint actionId,
            byte actionType,
            ulong sourceObjectId,
            uint sourceEntityId,
            ulong? animationTargetObjectId,
            ActionEffectTargetRecord[] targets)
        {
            var actionEffect = new ActionEffectRecord
            {
                TimestampMilliseconds = this.GetElapsedMilliseconds(),
                GlobalSequence = globalSequence,
                ActionId = actionId,
                ActionType = actionType,
                SourceObjectId = sourceObjectId,
                SourceStableActorId = this.ResolveStableActorId(sourceObjectId),
                AnimationTargetObjectId = animationTargetObjectId,
                Targets = targets,
            };
            this.ResolveTargetActorIds(targets);
            this.QueueActionNameResolution(actionId, sourceEntityId);
            this.ActionEffects.Add(actionEffect);
            return actionEffect;
        }

        public ObservedEvent AddDutyEvent(ObservedEventType type)
        {
            if (type is not (ObservedEventType.DutyStarted
                or ObservedEventType.DutyWiped
                or ObservedEventType.DutyRecommenced
                or ObservedEventType.DutyCompleted))
            {
                throw new ArgumentOutOfRangeException(nameof(type), type, "Only DutyState events can be added directly.");
            }

            var observedEvent = new ObservedEvent
            {
                TimestampMilliseconds = this.GetElapsedMilliseconds(),
                Type = type,
                Source = ObservedEventSource.DutyState,
            };
            this.Events.Add(observedEvent);
            return observedEvent;
        }

        public PullRecord Complete()
        {
            var actionEffects = this.ActionEffects.ToArray();
            for (var index = 0; index < actionEffects.Length; index++)
            {
                var actionEffect = actionEffects[index];
                this.ResolveTargetActorIds(actionEffect.Targets);
                actionEffects[index] = actionEffect with
                {
                    SourceStableActorId = this.ResolveStableActorId(actionEffect.SourceObjectId),
                };
            }

            var actionNames = new List<RecordedActionName>(this.actionNames.Values);
            actionNames.Sort(static (left, right) => left.ActionId.CompareTo(right.ActionId));

            return new PullRecord
            {
                Features = CaptureFeatures.ActorOwnerId
                    | CaptureFeatures.HitboxRadius
                    | (this.omnidirectionalityStateComplete
                        ? CaptureFeatures.OmnidirectionalState
                        : CaptureFeatures.None)
                    | (this.capturedTargetMarkers
                        ? CaptureFeatures.TargetMarkers | CaptureFeatures.TargetMarkerCanonicalOrder
                        : CaptureFeatures.None)
                    // Party membership is a recorded field, not an observation: every sample reads
                    // IPartyList, so a solo Pull records "no party members", it does not lose the field.
                    | CaptureFeatures.PartyMembership
                    | (this.castTimingComplete
                        ? CaptureFeatures.CastTiming
                        : CaptureFeatures.None)
                    | (this.statusTimingComplete
                        ? CaptureFeatures.StatusTiming
                        : CaptureFeatures.None)
                    | (this.actionEffectCaptureComplete
                        ? CaptureFeatures.ActionEffectCapture
                        : CaptureFeatures.None)
                    | CaptureFeatures.ActionNameSnapshot
                    | CaptureFeatures.BarrierState,
                CaptureId = Guid.NewGuid(),
                StartedAtUtc = this.startedAtUtc,
                EndedAtUtc = DateTimeOffset.UtcNow,
                TerritoryType = this.TerritoryType,
                MapId = this.MapId,
                Instance = this.Instance,
                Actors = this.Actors.ToArray(),
                Frames = this.Frames.ToArray(),
                Events = this.Events.ToArray(),
                WaymarkFrames = this.waymarkTimeline.ToArray(),
                ActionEffects = actionEffects,
                ActionNames = actionNames.ToArray(),
                TargetMarkerFrames = this.targetMarkerTimeline.ToArray(),
            };
        }
        private void QueueActionNameResolution(uint actionId, uint sourceEntityId)
        {
            if (actionId == 0 || this.actionNames.ContainsKey(actionId))
            {
                return;
            }

            if (this.actionNameRetries.TryGetValue(actionId, out var retry)
                && retry.AttemptCount >= MaximumActionNameResolutionAttempts)
            {
                return;
            }

            if (this.pendingActionNameSources.TryAdd(actionId, sourceEntityId))
            {
                this.pendingActionNameIds.Add(actionId);
                return;
            }

            if (sourceEntityId != 0 && this.pendingActionNameSources[actionId] == 0)
            {
                this.pendingActionNameSources[actionId] = sourceEntityId;
            }
        }

        private void ResolvePendingActionNames(long timestampMilliseconds)
        {
            for (var index = this.pendingActionNameIds.Count - 1; index >= 0; index--)
            {
                var actionId = this.pendingActionNameIds[index];
                var sourceEntityId = this.pendingActionNameSources[actionId];
                if (!this.TryCaptureActionName(actionId, sourceEntityId, timestampMilliseconds))
                {
                    continue;
                }

                this.pendingActionNameSources.Remove(actionId);
                var lastIndex = this.pendingActionNameIds.Count - 1;
                this.pendingActionNameIds[index] = this.pendingActionNameIds[lastIndex];
                this.pendingActionNameIds.RemoveAt(lastIndex);
            }
        }

        private bool TryCaptureActionName(
            uint actionId,
            uint sourceEntityId,
            long timestampMilliseconds)
        {
            if (this.actionNames.ContainsKey(actionId))
            {
                return true;
            }

            if (this.actionNameRetries.TryGetValue(actionId, out var retry))
            {
                if (retry.AttemptCount >= MaximumActionNameResolutionAttempts)
                {
                    return true;
                }

                if (timestampMilliseconds < retry.NextAttemptTimestampMilliseconds)
                {
                    return false;
                }
            }

            var resolved = this.resolveActionName(actionId, sourceEntityId);
            if (resolved is { } actionName
                && actionName.ActionId == actionId
                && !string.IsNullOrWhiteSpace(actionName.Name)
                && !actionName.Name.StartsWith("_rsv_", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(actionName.Language)
                && Enum.IsDefined(actionName.Source))
            {
                this.actionNames.Add(actionId, actionName);
                this.actionNameRetries.Remove(actionId);
                return true;
            }

            var nextRetry = new ActionNameRetryState(
                retry.AttemptCount + 1,
                timestampMilliseconds + ActionNameRetryIntervalMilliseconds);
            this.actionNameRetries[actionId] = nextRetry;
            return nextRetry.AttemptCount >= MaximumActionNameResolutionAttempts;
        }

        private readonly record struct ActionNameRetryState(
            int AttemptCount,
            long NextAttemptTimestampMilliseconds);


        private bool TryCaptureWaymarks(long timestampMilliseconds, WaymarkReader waymarkReader)
        {
            try
            {
                if (!waymarkReader.TryRead(this.waymarkObservations))
                {
                    return false;
                }

                if (this.waymarkTimeline.Observe(timestampMilliseconds, this.waymarkObservations))
                {
                    this.LatestWaymarks = this.WaymarkFrames[^1].Waymarks;
                }

                return true;
            }
            catch (Exception exception)
            {
                waymarkReader.RecordFailure(exception.Message, exception);
                return false;
            }
        }

        private bool TryCaptureTargetMarkers(
            long timestampMilliseconds,
            TargetMarkerReader targetMarkerReader)
        {
            try
            {
                if (!targetMarkerReader.TryRead(this.targetMarkerObjectIds))
                {
                    return false;
                }

                for (var index = 0; index < this.targetMarkerObjectIds.Length; index++)
                {
                    var targetObjectId = this.targetMarkerObjectIds[index];
                    var markerId = TargetMarkerReader.GetMarkerIdForNativeSlot(index);
                    this.targetMarkerObservations[(int)markerId] = new TargetMarkerObservation(
                        markerId,
                        targetObjectId,
                        targetObjectId == 0 ? null : this.ResolveStableActorId(targetObjectId));
                }

                this.targetMarkerTimeline.Observe(
                    timestampMilliseconds,
                    this.targetMarkerObservations);
                this.capturedTargetMarkers = true;
                return true;
            }
            catch (Exception exception)
            {
                targetMarkerReader.RecordFailure(exception.Message, exception);
                return false;
            }
        }

        private static int? FindPartyIndex(
            in ActorProbeSnapshot actor,
            ReadOnlySpan<PartyMemberProbeSnapshot> partyMembers)
        {
            if (actor.ObjectKind != ObjectKind.Pc)
            {
                return null;
            }

            foreach (ref readonly var member in partyMembers)
            {
                if ((member.GameObjectId != 0 && member.GameObjectId == actor.GameObjectId)
                    || (member.EntityId != 0 && member.EntityId == actor.EntityId))
                {
                    return member.Index;
                }
            }

            return null;
        }

        private void ResolveTargetActorIds(ActionEffectTargetRecord[] targets)
        {
            for (var index = 0; index < targets.Length; index++)
            {
                targets[index] = targets[index] with
                {
                    TargetStableActorId = this.ResolveStableActorId(targets[index].TargetObjectId),
                };
            }
        }

        private int? ResolveStableActorId(ulong gameObjectId) =>
            this.stableActorIds.TryGetValue(gameObjectId, out var stableActorId)
                ? stableActorId
                : null;

        private long GetElapsedMilliseconds() =>
            (long)Stopwatch.GetElapsedTime(this.startedTimestamp).TotalMilliseconds;
    }
}

internal enum ReplaySourceFinalizationState
{
    None,
    Finalizing,
    Succeeded,
    Failed,
}

internal readonly record struct ReplaySourceSnapshot(
    long FinalizationGeneration,
    ReplaySourceFinalizationState FinalizationState,
    Guid? FinalizationCaptureId,
    string? FinalizationError,
    long CompletedGeneration,
    PullRecord? LastCompletedPull,
    PullEndReason? LastCompletedEndReason = null,
    DebriefSummary? LastCompletedDebrief = null);

internal readonly record struct FrameworkCaptureSample(
    long CaptureEpoch,
    bool InCombat,
    CaptureSamplePreparation Preparation);

internal readonly record struct FrameworkCaptureDecision(
    bool SampleRequested,
    FrameworkCaptureSample Sample);

internal readonly record struct FrameRecordResult(
    bool Recorded,
    long Gaps,
    long RejectedActorSamples,
    bool WaymarkReadSucceeded,
    bool TargetMarkerReadSucceeded);

internal readonly record struct CaptureStatus(
    bool IsRecording,
    bool IsBusy,
    bool IsDeveloperExportBusy,
    bool AutomaticCaptureEnabled,
    bool IsInDutyInstance,
    bool IsArmedForCombatStart,
    AutomaticPullState AutomaticState,
    PullEndReason? LastEndReason,
    long? CombatEndRemainingMilliseconds,
    long CompletedPullCount,
    Guid? LastCompletedCaptureId,
    long SampleCount,
    int ActorCount,
    long GapCount,
    long RejectedActorSampleCount,
    int EventCount,
    string? LastEvent,
    int ActionEffectCount,
    string? LastActionEffect,
    int WaymarkFrameCount,
    long WaymarkReadFailureCount,
    bool WaymarkReaderChecked,
    bool WaymarkReaderAvailable,
    string? LastWaymarkError,
    WaymarkState[] LatestWaymarks,
    int TargetMarkerFrameCount,
    long TargetMarkerReadFailureCount,
    bool TargetMarkerReaderChecked,
    bool TargetMarkerReaderAvailable,
    string? LastTargetMarkerError,
    double AverageSampleIntervalMilliseconds,
    double LastSamplingMilliseconds,
    double MaximumSamplingMilliseconds,
    double? LastSerializationMilliseconds,
    string DeveloperExportDirectory,
    string? LastExportPath,
    string? LastExportError,
    string Message);
