using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed unsafe class WaymarkReader
{
    public const int MarkerCount = 8;
    private readonly IPluginLog log;

    public WaymarkReader(IPluginLog log)
    {
        this.log = log;
    }

    public bool IsAvailable { get; private set; }

    public long ErrorCount { get; private set; }

    public string? LastError { get; private set; }

    public bool TryRead(Span<WaymarkObservation> destination)
    {
        if (destination.Length < MarkerCount)
        {
            throw new ArgumentException($"Waymark destination requires {MarkerCount} entries.", nameof(destination));
        }

        try
        {
            var controller = MarkingController.Instance();
            if (controller is null)
            {
                this.RecordFailure("MarkingController.Instance() is unavailable.");
                return false;
            }

            var fieldMarkers = controller->FieldMarkers;
            if (fieldMarkers.Length != MarkerCount)
            {
                this.RecordFailure(
                    $"FFXIVClientStructs exposed {fieldMarkers.Length} FieldMarkers; expected {MarkerCount}.");
                return false;
            }

            for (var index = 0; index < MarkerCount; index++)
            {
                ref readonly var marker = ref fieldMarkers[index];
                var position = marker.Position;
                if (!float.IsFinite(position.X)
                    || !float.IsFinite(position.Y)
                    || !float.IsFinite(position.Z))
                {
                    this.RecordFailure($"FieldMarker {index} contains a non-finite position.");
                    return false;
                }

                destination[index] = new WaymarkObservation(
                    (WaymarkId)index,
                    marker.Active,
                    position.X,
                    position.Y,
                    position.Z);
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
            this.log.Warning("Raid Debrief Waymark reader unavailable: {Message}", message);
        }
        else
        {
            this.log.Error(exception, "Raid Debrief Waymark reader failed.");
        }
    }
}
