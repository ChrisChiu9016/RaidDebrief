namespace RaidDebrief.Core;

public sealed class DebriefAnalyzer
{
    public const long PreFirstDeathMilliseconds = 8_000;
    public const long PostFirstDeathMilliseconds = 12_000;
    public const long WipeFallbackWindowMilliseconds = 20_000;

    public DebriefSummary Analyze(PullRecord record, long? pullNumber = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (pullNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pullNumber),
                pullNumber,
                "Pull number must be positive when provided.");
        }

        var durationMilliseconds = PullTiming.CalculateDurationMilliseconds(record);
        var wipeTimestampMilliseconds = FindWipeTimestamp(record.Events);
        var deathLimitMilliseconds = wipeTimestampMilliseconds ?? durationMilliseconds;
        var actorsById = new Dictionary<int, ActorRecord>(record.Actors.Length);
        foreach (var actor in record.Actors)
        {
            actorsById.Add(actor.StableActorId, actor);
        }

        var unresolvedDeathEventCount = 0;
        var deaths = new List<DebriefDeathEntry>();
        for (var index = 0; index < record.Events.Length; index++)
        {
            var observedEvent = record.Events[index];
            if (observedEvent.Type != ObservedEventType.Death
                || observedEvent.TimestampMilliseconds > deathLimitMilliseconds)
            {
                continue;
            }

            if (observedEvent.StableActorId is not { } stableActorId
                || !actorsById.TryGetValue(stableActorId, out var actor))
            {
                unresolvedDeathEventCount++;
                continue;
            }

            if (!string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal))
            {
                continue;
            }

            deaths.Add(new DebriefDeathEntry(
                observedEvent.TimestampMilliseconds,
                index,
                actor.StableActorId,
                actor.Name,
                actor.ClassJobId));
        }

        deaths.Sort(static (left, right) =>
        {
            var timestampComparison = left.TimestampMilliseconds.CompareTo(right.TimestampMilliseconds);
            return timestampComparison != 0
                ? timestampComparison
                : left.OriginalRecordedIndex.CompareTo(right.OriginalRecordedIndex);
        });

        var deathSequence = deaths.ToArray();
        DebriefDeathEntry? firstDeath = deathSequence.Length == 0
            ? null
            : deathSequence[0];
        return new DebriefSummary
        {
            CaptureId = record.CaptureId,
            PullNumber = pullNumber,
            DurationMilliseconds = durationMilliseconds,
            WipeTimestampMilliseconds = wipeTimestampMilliseconds,
            BossHpAtEnd = wipeTimestampMilliseconds is { } wipeTimestamp
                ? ResolveBossHpAtTimestamp(record, wipeTimestamp)
                : null,
            FirstDeath = firstDeath,
            DeathSequence = deathSequence,
            UnresolvedDeathEventCount = unresolvedDeathEventCount,
            SuggestedReplayWindow = ResolveSuggestedReplayWindow(
                durationMilliseconds,
                wipeTimestampMilliseconds,
                firstDeath),
        };
    }

    private static long? FindWipeTimestamp(ObservedEvent[] events)
    {
        foreach (var observedEvent in events)
        {
            if (observedEvent.Type == ObservedEventType.DutyWiped)
            {
                return observedEvent.TimestampMilliseconds;
            }
        }

        return null;
    }

    public static DebriefBossHp? ResolveFinalBossHp(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ResolveBossHpAtTimestamp(
            record,
            PullTiming.CalculateDurationMilliseconds(record));
    }

    private static DebriefBossHp? ResolveBossHpAtTimestamp(
        PullRecord record,
        long timestampMilliseconds)
    {
        var resolver = new ActorStateResolver(record);
        var states = new ResolvedActorState[resolver.ActorCount];
        var stateCount = resolver.ResolveAll(timestampMilliseconds, states);
        var renderableActorIds = ArenaActorVisibility.BuildRenderableActorIds(record);
        DebriefBossHp? result = null;
        for (var index = 0; index < stateCount; index++)
        {
            ref readonly var state = ref states[index];
            if (!ArenaActorVisibility.TryGetMarkerKind(
                    state.Actor,
                    renderableActorIds,
                    out var kind)
                || kind != ArenaActorMarkerKind.BattleNpc
                || state.MaxHp == 0)
            {
                continue;
            }

            if (result is not null)
            {
                return null;
            }

            result = new DebriefBossHp(
                state.Actor.StableActorId,
                state.Actor.Name,
                state.CurrentHp,
                state.MaxHp);
        }

        return result;
    }

    private static DebriefReplayWindow? ResolveSuggestedReplayWindow(
        long durationMilliseconds,
        long? wipeTimestampMilliseconds,
        DebriefDeathEntry? firstDeath)
    {
        if (wipeTimestampMilliseconds is not { } wipeTimestamp)
        {
            return null;
        }

        if (firstDeath is not { } death)
        {
            return new DebriefReplayWindow(
                Math.Max(0, wipeTimestamp - WipeFallbackWindowMilliseconds),
                Math.Min(durationMilliseconds, wipeTimestamp));
        }

        var startTimestamp = Math.Max(0, death.TimestampMilliseconds - PreFirstDeathMilliseconds);
        var endTimestamp = Math.Min(
            durationMilliseconds,
            death.TimestampMilliseconds + PostFirstDeathMilliseconds);
        endTimestamp = Math.Min(endTimestamp, wipeTimestamp);
        return new DebriefReplayWindow(startTimestamp, Math.Max(startTimestamp, endTimestamp));
    }
}
