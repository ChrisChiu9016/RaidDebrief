using System;
using System.Numerics;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed unsafe class ActionEffectReader : IDisposable
{
    private const int MaximumTargetCount = 32;
    private readonly IPluginLog log;
    private readonly CaptureService captureService;
    private Hook<ReceiveDelegate>? receiveHook;
    private int disposed;
    private int captureEnabled;
    private long errorCount;

    public ActionEffectReader(
        IGameInteropProvider gameInteropProvider,
        IPluginLog log,
        CaptureService captureService)
    {
        this.log = log;
        this.captureService = captureService;

        try
        {
            var address = ActionEffectHandler.Addresses.Receive.Value;
            if (address == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "FFXIVClientStructs did not resolve ActionEffectHandler.Receive.");
            }

            this.receiveHook = gameInteropProvider.HookFromAddress<ReceiveDelegate>(
                address,
                this.ReceiveDetour);
            this.receiveHook.Enable();
            Volatile.Write(ref this.captureEnabled, 1);
            this.IsAvailable = true;
            this.captureService.SetActionEffectCaptureAvailability(true);
            this.log.Information(
                "Raid Debrief Action Effect hook enabled at {Address}.",
                address);
        }
        catch (Exception exception)
        {
            this.receiveHook?.Dispose();
            this.receiveHook = null;
            this.IsAvailable = false;
            this.captureService.SetActionEffectCaptureAvailability(false);
            this.LastError = exception.Message;
            this.log.Error(
                exception,
                "Raid Debrief Action Effect capture is unavailable; other capture remains enabled.");
        }
    }

    private delegate void ReceiveDelegate(
        uint casterEntityId,
        Character* caster,
        Vector3* targetPosition,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    public bool IsAvailable { get; private set; }

    public long ErrorCount => Interlocked.Read(ref this.errorCount);

    public string? LastError { get; private set; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        var hook = this.receiveHook;
        this.IsAvailable = false;
        this.captureService.SetActionEffectCaptureAvailability(false);
        if (hook is null)
        {
            return;
        }

        try
        {
            if (hook.IsEnabled)
            {
                hook.Disable();
            }

            hook.Dispose();
            this.receiveHook = null;
            this.log.Information("Raid Debrief Action Effect hook disabled and disposed.");
        }
        catch (Exception exception)
        {
            this.RecordFailure("Action Effect hook disposal failed.", exception);
        }
    }

    private void ReceiveDetour(
        uint casterEntityId,
        Character* caster,
        Vector3* targetPosition,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        var hook = this.receiveHook;
        try
        {
            if (Volatile.Read(ref this.disposed) == 0
                && Volatile.Read(ref this.captureEnabled) != 0
                && this.captureService.IsRecording)
            {
                this.TryCapture(casterEntityId, caster, header, effects, targetEntityIds);
            }
        }
        catch (Exception exception)
        {
            this.RecordFailure("Action Effect detour failed to decode an event.", exception);
        }
        finally
        {
            hook!.Original(casterEntityId, caster, targetPosition, header, effects, targetEntityIds);
        }
    }

    private void TryCapture(
        uint casterEntityId,
        Character* caster,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        if (header is null)
        {
            this.DisableDecoding("Action Effect callback contained a null header pointer.");
            return;
        }

        var targetCount = header->NumTargets;
        if (targetCount == 0)
        {
            return;
        }

        if (targetCount > MaximumTargetCount || effects is null || targetEntityIds is null)
        {
            this.DisableDecoding(
                $"Action Effect callback layout is incompatible (target count {targetCount}).");
            return;
        }

        var targets = new ActionEffectTargetRecord[targetCount];
        var sourceObjectId = caster is null
            ? casterEntityId
            : caster->GetGameObjectId().Id;
        if (sourceObjectId == 0)
        {
            sourceObjectId = casterEntityId;
        }

        if (sourceObjectId == 0 || header->ActionId == 0)
        {
            this.RecordFailure("Action Effect callback omitted its source or action ID.");
            return;
        }

        for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            var nativeEntries = effects[targetIndex].Effects;
            var entryCount = 0;
            for (var entryIndex = 0; entryIndex < nativeEntries.Length; entryIndex++)
            {
                if (nativeEntries[entryIndex].Type != ActionEffectDecoder.NothingType)
                {
                    entryCount++;
                }
            }

            var entries = new ActionEffectEntryRecord[entryCount];
            var nextEntry = 0;
            for (var entryIndex = 0; entryIndex < nativeEntries.Length; entryIndex++)
            {
                ref readonly var nativeEntry = ref nativeEntries[entryIndex];
                if (nativeEntry.Type == ActionEffectDecoder.NothingType)
                {
                    continue;
                }

                entries[nextEntry++] = new ActionEffectEntryRecord
                {
                    Index = (byte)entryIndex,
                    Kind = ActionEffectDecoder.Classify(nativeEntry.Type),
                    RawType = nativeEntry.Type,
                    Param0 = nativeEntry.Param0,
                    Param1 = nativeEntry.Param1,
                    Param2 = nativeEntry.Param2,
                    Param3 = nativeEntry.Param3,
                    Param4 = nativeEntry.Param4,
                    Value = nativeEntry.Value,
                    Amount = ActionEffectDecoder.DecodeAmount(
                        nativeEntry.Type,
                        nativeEntry.Param3,
                        nativeEntry.Param4,
                        nativeEntry.Value),
                    IsCritical = ActionEffectDecoder.DecodeCritical(
                        nativeEntry.Type,
                        nativeEntry.Param0,
                        nativeEntry.Param1),
                    IsDirectHit = ActionEffectDecoder.DecodeDirectHit(
                        nativeEntry.Type,
                        nativeEntry.Param0),
                };
            }

            var targetObjectId = targetEntityIds[targetIndex].Id;
            if (targetObjectId == 0)
            {
                this.RecordFailure("Action Effect callback contained an invalid target ID.");
                return;
            }

            targets[targetIndex] = new ActionEffectTargetRecord
            {
                TargetObjectId = targetObjectId,
                Entries = entries,
            };
        }

        this.captureService.RecordActionEffect(
            header->GlobalSequence,
            header->ActionId,
            header->ActionType,
            sourceObjectId,
            casterEntityId,
            header->AnimationTargetId.Id == 0 ? null : header->AnimationTargetId.Id,
            targets);
    }

    private void DisableDecoding(string message)
    {
        if (Interlocked.Exchange(ref this.captureEnabled, 0) == 0)
        {
            return;
        }

        this.IsAvailable = false;
        this.captureService.SetActionEffectCaptureAvailability(false);
        this.RecordFailure(
            $"{message} Action Effect capture was disabled; position and state capture remain available.");
    }

    private void RecordFailure(string message, Exception? exception = null)
    {
        this.LastError = exception is null ? message : $"{message} {exception.Message}";
        var count = Interlocked.Increment(ref this.errorCount);
        if (count != 1 && count % 100 != 0)
        {
            return;
        }

        if (exception is null)
        {
            this.log.Warning("Raid Debrief Action Effect reader: {Message}", message);
        }
        else
        {
            this.log.Error(exception, "Raid Debrief Action Effect reader: {Message}", message);
        }
    }
}
