namespace RaidDebrief.Core;

public enum TargetMarkerId
{
    Attack1,
    Attack2,
    Attack3,
    Attack4,
    Attack5,
    Attack6,
    Attack7,
    Attack8,
    Bind1,
    Bind2,
    Bind3,
    Stop1,
    Stop2,
    Square,
    Circle,
    Plus,
    Triangle,
}

public static class TargetMarkerNativeSlotOrder
{
    public static TargetMarkerId GetMarkerId(int slot) =>
        slot switch
        {
            0 => TargetMarkerId.Attack1,
            1 => TargetMarkerId.Attack2,
            2 => TargetMarkerId.Attack3,
            3 => TargetMarkerId.Attack4,
            4 => TargetMarkerId.Attack5,
            5 => TargetMarkerId.Bind1,
            6 => TargetMarkerId.Bind2,
            7 => TargetMarkerId.Bind3,
            8 => TargetMarkerId.Stop1,
            9 => TargetMarkerId.Stop2,
            10 => TargetMarkerId.Square,
            11 => TargetMarkerId.Circle,
            12 => TargetMarkerId.Plus,
            13 => TargetMarkerId.Triangle,
            14 => TargetMarkerId.Attack6,
            15 => TargetMarkerId.Attack7,
            16 => TargetMarkerId.Attack8,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
}

public sealed record TargetMarkerState
{
    public required TargetMarkerId Id { get; init; }

    public required ulong TargetObjectId { get; init; }

    public int? TargetStableActorId { get; init; }
}

public sealed record TargetMarkerFrame
{
    public required long TimestampMilliseconds { get; init; }

    public required TargetMarkerState[] Markers { get; init; }
}

public readonly record struct TargetMarkerObservation(
    TargetMarkerId Id,
    ulong TargetObjectId,
    int? TargetStableActorId);

public sealed class TargetMarkerTimelineBuilder
{
    public const int MarkerCount = 17;
    private readonly List<TargetMarkerFrame> frames = new(16);
    private TargetMarkerState[] latestStates = [];

    public IReadOnlyList<TargetMarkerFrame> Frames => this.frames;

    public TargetMarkerFrame[] ToArray() => this.frames.ToArray();

    public bool Observe(long timestampMilliseconds, ReadOnlySpan<TargetMarkerObservation> markers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        ValidateObservations(markers);
        if (!this.HasChanged(markers))
        {
            return false;
        }

        if (this.frames.Count > 0 && timestampMilliseconds <= this.frames[^1].TimestampMilliseconds)
        {
            throw new ArgumentException(
                "Changed Target Marker frames require strictly increasing timestamps.",
                nameof(timestampMilliseconds));
        }

        var states = new TargetMarkerState[MarkerCount];
        for (var index = 0; index < markers.Length; index++)
        {
            ref readonly var marker = ref markers[index];
            states[index] = new TargetMarkerState
            {
                Id = marker.Id,
                TargetObjectId = marker.TargetObjectId,
                TargetStableActorId = marker.TargetStableActorId,
            };
        }

        this.latestStates = states;
        this.frames.Add(new TargetMarkerFrame
        {
            TimestampMilliseconds = timestampMilliseconds,
            Markers = states,
        });
        return true;
    }

    private static void ValidateObservations(ReadOnlySpan<TargetMarkerObservation> markers)
    {
        if (markers.Length != MarkerCount)
        {
            throw new ArgumentException(
                $"A Target Marker observation must contain exactly {MarkerCount} markers.",
                nameof(markers));
        }

        Span<bool> seen = stackalloc bool[MarkerCount];
        foreach (ref readonly var marker in markers)
        {
            var index = (int)marker.Id;
            if (index < 0 || index >= seen.Length || seen[index])
            {
                throw new ArgumentException(
                    $"Target Marker ID {marker.Id} is invalid or duplicated.",
                    nameof(markers));
            }

            if (marker.TargetObjectId == 0 && marker.TargetStableActorId is not null)
            {
                throw new ArgumentException(
                    $"Inactive Target Marker {marker.Id} cannot reference a stable actor.",
                    nameof(markers));
            }

            seen[index] = true;
        }
    }

    private bool HasChanged(ReadOnlySpan<TargetMarkerObservation> markers)
    {
        if (this.latestStates.Length != markers.Length)
        {
            return true;
        }

        for (var index = 0; index < markers.Length; index++)
        {
            var previous = this.latestStates[index];
            var current = markers[index];
            if (previous.Id != current.Id
                || previous.TargetObjectId != current.TargetObjectId
                || previous.TargetStableActorId != current.TargetStableActorId)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class TargetMarkerStateResolver
{
    private readonly TargetMarkerFrame[] frames;

    public TargetMarkerStateResolver(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.frames = record.TargetMarkerFrames;
    }

    public ReadOnlySpan<TargetMarkerState> Resolve(long timestampMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        var lower = 0;
        var upper = this.frames.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (this.frames[middle].TimestampMilliseconds <= timestampMilliseconds)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower == 0
            ? ReadOnlySpan<TargetMarkerState>.Empty
            : this.frames[lower - 1].Markers;
    }
}
