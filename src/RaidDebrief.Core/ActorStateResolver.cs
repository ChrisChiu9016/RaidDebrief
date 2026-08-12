namespace RaidDebrief.Core;

public readonly record struct ResolvedActorState
{
    public required ActorRecord Actor { get; init; }

    public required float X { get; init; }

    public required float Y { get; init; }

    public required float Z { get; init; }

    public required float Rotation { get; init; }

    public required float HitboxRadius { get; init; }

    public required uint CurrentHp { get; init; }

    public required uint MaxHp { get; init; }

    public required bool IsDead { get; init; }

    public required bool IsTargetable { get; init; }

    public required bool IsOmnidirectional { get; init; }
}

public sealed class ActorStateResolver
{
    private readonly ActorTrack[] tracks;
    private readonly Dictionary<int, int> trackIndices;

    public ActorStateResolver(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        this.tracks = new ActorTrack[record.Actors.Length];
        this.trackIndices = new Dictionary<int, int>(record.Actors.Length);
        var samples = new List<TimestampedActorSample>?[record.Actors.Length];
        var lifecycleEvents = new List<ActorLifecycleEvent>?[record.Actors.Length];

        for (var index = 0; index < record.Actors.Length; index++)
        {
            var actor = record.Actors[index];
            if (!this.trackIndices.TryAdd(actor.StableActorId, index))
            {
                throw new InvalidDataException($"Duplicate stable actor ID {actor.StableActorId}.");
            }
        }

        foreach (var frame in record.Frames)
        {
            foreach (var sample in frame.Actors)
            {
                if (!this.trackIndices.TryGetValue(sample.StableActorId, out var trackIndex))
                {
                    throw new InvalidDataException(
                        $"Frame contains unknown stable actor ID {sample.StableActorId}.");
                }

                (samples[trackIndex] ??= []).Add(
                    new TimestampedActorSample(frame.TimestampMilliseconds, sample));
            }
        }

        foreach (var observedEvent in record.Events)
        {
            if (observedEvent.Type is not ObservedEventType.ActorSpawned
                and not ObservedEventType.ActorDespawned)
            {
                continue;
            }

            if (observedEvent.StableActorId is not { } stableActorId
                || !this.trackIndices.TryGetValue(stableActorId, out var trackIndex))
            {
                throw new InvalidDataException("Actor lifecycle event references an unknown actor.");
            }

            (lifecycleEvents[trackIndex] ??= []).Add(
                new ActorLifecycleEvent(observedEvent.TimestampMilliseconds, observedEvent.Type));
        }

        for (var index = 0; index < this.tracks.Length; index++)
        {
            this.tracks[index] = new ActorTrack(
                record.Actors[index],
                samples[index]?.ToArray() ?? [],
                lifecycleEvents[index]?.ToArray() ?? []);
        }
    }

    public int ActorCount => this.tracks.Length;

    public bool TryResolveActor(
        int stableActorId,
        long timestampMilliseconds,
        out ResolvedActorState state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        if (!this.trackIndices.TryGetValue(stableActorId, out var trackIndex))
        {
            state = default;
            return false;
        }

        return this.tracks[trackIndex].TryResolve(timestampMilliseconds, out state);
    }

    public int ResolveAll(long timestampMilliseconds, Span<ResolvedActorState> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        if (destination.Length < this.tracks.Length)
        {
            throw new ArgumentException(
                $"Destination requires space for {this.tracks.Length} actor states.",
                nameof(destination));
        }

        var count = 0;
        foreach (var track in this.tracks)
        {
            if (track.TryResolve(timestampMilliseconds, out var state))
            {
                destination[count++] = state;
            }
        }

        return count;
    }

    private sealed class ActorTrack(
        ActorRecord actor,
        TimestampedActorSample[] samples,
        ActorLifecycleEvent[] lifecycleEvents)
    {
        public bool TryResolve(long timestampMilliseconds, out ResolvedActorState state)
        {
            var previousIndex = FindLastSampleAtOrBefore(samples, timestampMilliseconds);
            if (previousIndex < 0)
            {
                state = default;
                return false;
            }

            var previous = samples[previousIndex];
            var lifecycleIndex = FindLastLifecycleEventAtOrBefore(
                lifecycleEvents,
                timestampMilliseconds);
            if (lifecycleIndex >= 0)
            {
                var lifecycleEvent = lifecycleEvents[lifecycleIndex];
                if ((lifecycleEvent.Type == ObservedEventType.ActorDespawned
                        && lifecycleEvent.TimestampMilliseconds >= previous.TimestampMilliseconds)
                    || lifecycleEvent.TimestampMilliseconds > previous.TimestampMilliseconds)
                {
                    state = default;
                    return false;
                }
            }

            var x = previous.Sample.X;
            var y = previous.Sample.Y;
            var z = previous.Sample.Z;
            var rotation = NormalizeAngle(previous.Sample.Rotation);
            var nextIndex = previousIndex + 1;
            if (nextIndex < samples.Length)
            {
                var next = samples[nextIndex];
                if (!HasLifecycleBoundary(
                        lifecycleEvents,
                        previous.TimestampMilliseconds,
                        next.TimestampMilliseconds))
                {
                    var amount = (float)((timestampMilliseconds - previous.TimestampMilliseconds)
                        / (double)(next.TimestampMilliseconds - previous.TimestampMilliseconds));
                    x = Lerp(previous.Sample.X, next.Sample.X, amount);
                    y = Lerp(previous.Sample.Y, next.Sample.Y, amount);
                    z = Lerp(previous.Sample.Z, next.Sample.Z, amount);
                    rotation = InterpolateAngle(
                        previous.Sample.Rotation,
                        next.Sample.Rotation,
                        amount);
                }
            }

            state = new ResolvedActorState
            {
                Actor = actor,
                X = x,
                Y = y,
                Z = z,
                Rotation = rotation,
                HitboxRadius = previous.Sample.HitboxRadius,
                CurrentHp = previous.Sample.CurrentHp,
                MaxHp = previous.Sample.MaxHp,
                IsDead = previous.Sample.IsDead,
                IsTargetable = previous.Sample.IsTargetable,
                IsOmnidirectional = previous.Sample.IsOmnidirectional,
            };
            return true;
        }

        private static int FindLastSampleAtOrBefore(
            TimestampedActorSample[] values,
            long timestampMilliseconds)
        {
            var lower = 0;
            var upper = values.Length;
            while (lower < upper)
            {
                var middle = lower + ((upper - lower) / 2);
                if (values[middle].TimestampMilliseconds <= timestampMilliseconds)
                {
                    lower = middle + 1;
                }
                else
                {
                    upper = middle;
                }
            }

            return lower - 1;
        }

        private static int FindLastLifecycleEventAtOrBefore(
            ActorLifecycleEvent[] values,
            long timestampMilliseconds)
        {
            var lower = 0;
            var upper = values.Length;
            while (lower < upper)
            {
                var middle = lower + ((upper - lower) / 2);
                if (values[middle].TimestampMilliseconds <= timestampMilliseconds)
                {
                    lower = middle + 1;
                }
                else
                {
                    upper = middle;
                }
            }

            return lower - 1;
        }

        private static bool HasLifecycleBoundary(
            ActorLifecycleEvent[] values,
            long afterTimestampMilliseconds,
            long throughTimestampMilliseconds)
        {
            var lower = 0;
            var upper = values.Length;
            while (lower < upper)
            {
                var middle = lower + ((upper - lower) / 2);
                if (values[middle].TimestampMilliseconds <= afterTimestampMilliseconds)
                {
                    lower = middle + 1;
                }
                else
                {
                    upper = middle;
                }
            }

            return lower < values.Length
                && values[lower].TimestampMilliseconds <= throughTimestampMilliseconds;
        }

        private static float Lerp(float start, float end, float amount) =>
            start + ((end - start) * amount);

        private static float InterpolateAngle(float start, float end, float amount)
        {
            var delta = NormalizeAngle(end - start);
            return NormalizeAngle(start + (delta * amount));
        }

        private static float NormalizeAngle(float angle)
        {
            var normalized = (angle + MathF.PI) % MathF.Tau;
            if (normalized < 0)
            {
                normalized += MathF.Tau;
            }

            return normalized - MathF.PI;
        }
    }

    private readonly record struct TimestampedActorSample(
        long TimestampMilliseconds,
        ActorStateSample Sample);

    private readonly record struct ActorLifecycleEvent(
        long TimestampMilliseconds,
        ObservedEventType Type);
}
