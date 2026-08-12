namespace RaidDebrief.Core;

public enum WaymarkId
{
    A,
    B,
    C,
    D,
    One,
    Two,
    Three,
    Four,
}

public sealed record WaymarkState
{
    public required WaymarkId Id { get; init; }

    public required bool Active { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Z { get; init; }
}

public sealed record WaymarkFrame
{
    public required long TimestampMilliseconds { get; init; }

    public required WaymarkState[] Waymarks { get; init; }
}

public readonly record struct WaymarkObservation(
    WaymarkId Id,
    bool Active,
    float X,
    float Y,
    float Z);

public sealed class WaymarkTimelineBuilder
{
    private readonly List<WaymarkFrame> frames = new(16);
    private WaymarkState[] latestStates = [];

    public IReadOnlyList<WaymarkFrame> Frames => this.frames;

    public WaymarkFrame[] ToArray() => this.frames.ToArray();

    public IReadOnlyList<WaymarkState> LatestStates => this.latestStates;

    public bool Observe(long timestampMilliseconds, ReadOnlySpan<WaymarkObservation> waymarks)
    {
        if (timestampMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampMilliseconds),
                timestampMilliseconds,
                "Waymark timestamps must be non-negative.");
        }

        ValidateObservations(waymarks);
        if (!this.HasChanged(waymarks))
        {
            return false;
        }

        if (this.frames.Count > 0 && timestampMilliseconds <= this.frames[^1].TimestampMilliseconds)
        {
            throw new ArgumentException(
                "Changed Waymark frames require strictly increasing timestamps.",
                nameof(timestampMilliseconds));
        }

        var states = new WaymarkState[waymarks.Length];
        for (var index = 0; index < waymarks.Length; index++)
        {
            var observation = waymarks[index];
            states[index] = new WaymarkState
            {
                Id = observation.Id,
                Active = observation.Active,
                X = observation.X,
                Y = observation.Y,
                Z = observation.Z,
            };
        }

        this.latestStates = states;
        this.frames.Add(new WaymarkFrame
        {
            TimestampMilliseconds = timestampMilliseconds,
            Waymarks = states,
        });
        return true;
    }

    private static void ValidateObservations(ReadOnlySpan<WaymarkObservation> waymarks)
    {
        if (waymarks.Length != 8)
        {
            throw new ArgumentException("A Waymark observation must contain exactly eight markers.", nameof(waymarks));
        }

        Span<bool> seen = stackalloc bool[8];
        foreach (ref readonly var waymark in waymarks)
        {
            var index = (int)waymark.Id;
            if (index < 0 || index >= seen.Length || seen[index])
            {
                throw new ArgumentException($"Waymark ID {waymark.Id} is invalid or duplicated.", nameof(waymarks));
            }

            if (!float.IsFinite(waymark.X)
                || !float.IsFinite(waymark.Y)
                || !float.IsFinite(waymark.Z))
            {
                throw new ArgumentException($"Waymark {waymark.Id} position must be finite.", nameof(waymarks));
            }

            seen[index] = true;
        }
    }

    private bool HasChanged(ReadOnlySpan<WaymarkObservation> waymarks)
    {
        if (this.latestStates.Length != waymarks.Length)
        {
            return true;
        }

        for (var index = 0; index < waymarks.Length; index++)
        {
            var previous = this.latestStates[index];
            var current = waymarks[index];
            if (previous.Id != current.Id
                || previous.Active != current.Active
                || previous.X != current.X
                || previous.Y != current.Y
                || previous.Z != current.Z)
            {
                return true;
            }
        }

        return false;
    }
}
