using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed unsafe class TargetMarkerReader
{
    private const ulong InvalidObjectId = 0xE0000000;
    private readonly IPluginLog log;

    public TargetMarkerReader(IPluginLog log)
    {
        this.log = log;
    }

    internal static TargetMarkerId GetMarkerIdForNativeSlot(int slot) =>
        TargetMarkerNativeSlotOrder.GetMarkerId(slot);

    public bool IsAvailable { get; private set; }

    public long ErrorCount { get; private set; }

    public string? LastError { get; private set; }

    public bool TryRead(Span<ulong> destination)
    {
        if (destination.Length < TargetMarkerTimelineBuilder.MarkerCount)
        {
            throw new ArgumentException(
                $"Target Marker destination requires {TargetMarkerTimelineBuilder.MarkerCount} entries.",
                nameof(destination));
        }

        try
        {
            var controller = MarkingController.Instance();
            if (controller is null)
            {
                this.RecordFailure("MarkingController.Instance() is unavailable.");
                return false;
            }

            var markers = controller->Markers;
            if (markers.Length != TargetMarkerTimelineBuilder.MarkerCount)
            {
                this.RecordFailure(
                    $"FFXIVClientStructs exposed {markers.Length} Target Markers; expected {TargetMarkerTimelineBuilder.MarkerCount}.");
                return false;
            }

            for (var index = 0; index < TargetMarkerTimelineBuilder.MarkerCount; index++)
            {
                ulong targetObjectId = markers[index];
                destination[index] = targetObjectId is 0 or InvalidObjectId
                    ? 0
                    : targetObjectId;
            }

            this.IsAvailable = true;
            this.LastError = null;
            return true;
        }
        catch (Exception exception)
        {
            this.RecordFailure(exception.Message, exception);
            return false;
        }
    }

    internal void RecordFailure(string message, Exception? exception = null)
    {
        this.IsAvailable = false;
        this.LastError = message;
        this.ErrorCount++;

        if (this.ErrorCount != 1 && this.ErrorCount % 300 != 0)
        {
            return;
        }

        if (exception is null)
        {
            this.log.Warning("Raid Debrief Target Marker reader unavailable: {Message}", message);
        }
        else
        {
            this.log.Error(exception, "Raid Debrief Target Marker reader failed.");
        }
    }
}
