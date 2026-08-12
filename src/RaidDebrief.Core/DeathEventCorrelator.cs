namespace RaidDebrief.Core;

public enum CorrelationConfidence
{
    Unavailable,
    Low,
    Medium,
    High,
}

[Flags]
public enum DeathCorrelationEvidence
{
    None = 0,
    TargetMatched = 1 << 0,
    DamageAmountDecoded = 1 << 1,
    GlobalSequenceAvailable = 1 << 2,
    VirtualHpCrossedZero = 1 << 3,
    DeathTransitionObserved = 1 << 4,
    HpAnchorAvailable = 1 << 5,
    HpSamplesReconciled = 1 << 6,
    SingleCandidateInWindow = 1 << 7,
}

[Flags]
public enum DeathCorrelationLimitations
{
    None = 0,
    ActionEffectCaptureUnavailable = 1 << 0,
    PeriodicHpEffectsNotObserved = 1 << 1,
    UnexplainedHpDelta = 1 << 2,
    MultipleCandidateHits = 1 << 3,
    AmbiguousEffectOrdering = 1 << 4,
    ActorIdentityUnresolved = 1 << 5,
    HpObservationLag = 1 << 6,
}

public readonly record struct CorrelatedDamageEvent(
    long TimestampMilliseconds,
    uint GlobalSequence,
    uint ActionId,
    int? SourceStableActorId,
    int TargetSlotIndex,
    byte EffectEntryIndex,
    uint Amount);

public sealed record DeathEventCorrelation
{
    public const int CurrentAlgorithmVersion = 1;

    public required int DeathOriginalRecordedIndex { get; init; }

    public required int DeadActorStableId { get; init; }

    public required long DeathTimestampMilliseconds { get; init; }

    public CorrelatedDamageEvent? KillingBlowCandidate { get; init; }

    public uint? EstimatedHpBeforeHit { get; init; }

    public uint? EstimatedOverkill { get; init; }

    public required CorrelatedDamageEvent[] LastHits { get; init; }

    public required CorrelationConfidence Confidence { get; init; }

    public required DeathCorrelationEvidence Evidence { get; init; }

    public required DeathCorrelationLimitations Limitations { get; init; }

    public int AlgorithmVersion { get; init; } = CurrentAlgorithmVersion;
}

/// <summary>
/// Correlates recorded HP samples, Action Effects, and Death transitions. Results are deterministic
/// interpretations of recorded evidence; they are not server-authored Killing Blow fields.
/// </summary>
public sealed class DeathEventCorrelator
{
    private const long CorrelationLookbackMilliseconds = 3_000;
    private const long LastHitsLookbackMilliseconds = 10_000;
    private const long HpLagThresholdMilliseconds = 250;
    private const int MaximumLastHits = 6;

    public DeathEventCorrelation[] Analyze(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var playerActorIds = new HashSet<int>();
        foreach (var actor in record.Actors)
        {
            if (string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal))
            {
                playerActorIds.Add(actor.StableActorId);
            }
        }

        var results = new List<DeathEventCorrelation>();
        for (var eventIndex = 0; eventIndex < record.Events.Length; eventIndex++)
        {
            var observedEvent = record.Events[eventIndex];
            if (observedEvent.Type != ObservedEventType.Death
                || observedEvent.StableActorId is not { } actorId
                || !playerActorIds.Contains(actorId))
            {
                continue;
            }

            results.Add(this.AnalyzeDeath(record, eventIndex, actorId, observedEvent.TimestampMilliseconds));
        }

        return results.ToArray();
    }

    private DeathEventCorrelation AnalyzeDeath(
        PullRecord record,
        int deathEventIndex,
        int actorId,
        long deathTimestampMilliseconds)
    {
        var lastHits = new List<CorrelatedDamageEvent>(MaximumLastHits + 8);
        var deltas = new List<HpDelta>(32);
        var simulationStart = Math.Max(0, deathTimestampMilliseconds - CorrelationLookbackMilliseconds);
        var lastHitsStart = Math.Max(0, deathTimestampMilliseconds - LastHitsLookbackMilliseconds);

        foreach (var actionEffect in record.ActionEffects)
        {
            if (actionEffect.TimestampMilliseconds > deathTimestampMilliseconds)
            {
                break;
            }

            for (var targetIndex = 0; targetIndex < actionEffect.Targets.Length; targetIndex++)
            {
                var target = actionEffect.Targets[targetIndex];
                if (target.TargetStableActorId != actorId)
                {
                    continue;
                }

                foreach (var entry in target.Entries)
                {
                    if (entry.Amount is not { } amount || amount == 0)
                    {
                        continue;
                    }

                    var damageEvent = new CorrelatedDamageEvent(
                        actionEffect.TimestampMilliseconds,
                        actionEffect.GlobalSequence,
                        actionEffect.ActionId,
                        actionEffect.SourceStableActorId,
                        targetIndex,
                        entry.Index,
                        amount);
                    if (entry.Kind == ActionEffectKind.Damage
                        && actionEffect.TimestampMilliseconds >= lastHitsStart)
                    {
                        lastHits.Add(damageEvent);
                    }

                    if (actionEffect.TimestampMilliseconds >= simulationStart
                        && entry.Kind is ActionEffectKind.Damage or ActionEffectKind.Heal)
                    {
                        deltas.Add(new HpDelta(damageEvent, entry.Kind));
                    }
                }
            }
        }

        deltas.Sort(HpDeltaComparer.Instance);
        lastHits.Sort(CorrelatedDamageComparer.Instance);
        if (lastHits.Count > MaximumLastHits)
        {
            lastHits.RemoveRange(0, lastHits.Count - MaximumLastHits);
        }

        var evidence = DeathCorrelationEvidence.DeathTransitionObserved;
        var limitations = DeathCorrelationLimitations.PeriodicHpEffectsNotObserved;
        if ((record.Features & CaptureFeatures.ActionEffectCapture) == 0)
        {
            limitations |= DeathCorrelationLimitations.ActionEffectCaptureUnavailable;
        }

        if (lastHits.Count == 0)
        {
            return CreateUnavailable(
                deathEventIndex,
                actorId,
                deathTimestampMilliseconds,
                lastHits,
                evidence,
                limitations);
        }

        evidence |= DeathCorrelationEvidence.TargetMatched
            | DeathCorrelationEvidence.DamageAmountDecoded
            | DeathCorrelationEvidence.GlobalSequenceAvailable;

        var firstDeltaTimestamp = deltas.Count == 0
            ? simulationStart
            : deltas[0].Event.TimestampMilliseconds;
        var anchor = FindLastHpSampleAtOrBefore(record, actorId, firstDeltaTimestamp);
        uint? estimatedHpBeforeHit = null;
        uint? estimatedOverkill = null;
        CorrelatedDamageEvent? candidate = null;
        var crossingCount = 0;
        long candidateTimestamp = 0;

        if (anchor is { IsDead: false, CurrentHp: > 0 })
        {
            evidence |= DeathCorrelationEvidence.HpAnchorAvailable;
            long virtualHp = anchor.Value.CurrentHp;
            foreach (var delta in deltas)
            {
                if (delta.Event.TimestampMilliseconds < anchor.Value.TimestampMilliseconds)
                {
                    continue;
                }

                if (delta.Kind == ActionEffectKind.Heal)
                {
                    virtualHp = Math.Min(anchor.Value.MaxHp, virtualHp + delta.Event.Amount);
                    continue;
                }

                var hpBefore = Math.Max(0, virtualHp);
                virtualHp -= delta.Event.Amount;
                if (hpBefore > 0 && virtualHp <= 0)
                {
                    crossingCount++;
                    candidate = delta.Event;
                    candidateTimestamp = delta.Event.TimestampMilliseconds;
                    estimatedHpBeforeHit = (uint)Math.Min(uint.MaxValue, hpBefore);
                    estimatedOverkill = (uint)Math.Min(uint.MaxValue, Math.Max(0, -virtualHp));
                }
            }

            if (candidate is not null)
            {
                evidence |= DeathCorrelationEvidence.VirtualHpCrossedZero;
                var deathSample = FindFirstHpSampleAtOrAfter(record, actorId, deathTimestampMilliseconds);
                if (deathSample is { IsDead: true, CurrentHp: 0 })
                {
                    evidence |= DeathCorrelationEvidence.HpSamplesReconciled;
                }
            }
            else
            {
                limitations |= DeathCorrelationLimitations.UnexplainedHpDelta;
            }
        }
        else
        {
            limitations |= DeathCorrelationLimitations.UnexplainedHpDelta;
        }

        if (candidate is null)
        {
            candidate = lastHits[^1];
        }

        if (crossingCount == 1)
        {
            evidence |= DeathCorrelationEvidence.SingleCandidateInWindow;
        }
        else if (crossingCount > 1)
        {
            limitations |= DeathCorrelationLimitations.MultipleCandidateHits;
        }

        var sameTimestampCandidateCount = 0;
        foreach (var hit in lastHits)
        {
            if (hit.TimestampMilliseconds == candidate.Value.TimestampMilliseconds)
            {
                sameTimestampCandidateCount++;
            }
        }

        if (sameTimestampCandidateCount > 1 && candidate.Value.GlobalSequence == 0)
        {
            limitations |= DeathCorrelationLimitations.AmbiguousEffectOrdering;
        }

        if (deathTimestampMilliseconds - candidate.Value.TimestampMilliseconds > HpLagThresholdMilliseconds)
        {
            limitations |= DeathCorrelationLimitations.HpObservationLag;
        }

        var confidence = CorrelationConfidence.Low;
        if ((evidence & DeathCorrelationEvidence.VirtualHpCrossedZero) != 0)
        {
            confidence = (record.Features & CaptureFeatures.ActionEffectCapture) != 0
                && crossingCount == 1
                && (limitations & DeathCorrelationLimitations.AmbiguousEffectOrdering) == 0
                    ? CorrelationConfidence.High
                    : CorrelationConfidence.Medium;
        }

        return new DeathEventCorrelation
        {
            DeathOriginalRecordedIndex = deathEventIndex,
            DeadActorStableId = actorId,
            DeathTimestampMilliseconds = deathTimestampMilliseconds,
            KillingBlowCandidate = candidate,
            EstimatedHpBeforeHit = estimatedHpBeforeHit,
            EstimatedOverkill = estimatedOverkill,
            LastHits = lastHits.ToArray(),
            Confidence = confidence,
            Evidence = evidence,
            Limitations = limitations,
        };
    }

    private static DeathEventCorrelation CreateUnavailable(
        int deathEventIndex,
        int actorId,
        long deathTimestampMilliseconds,
        List<CorrelatedDamageEvent> lastHits,
        DeathCorrelationEvidence evidence,
        DeathCorrelationLimitations limitations) =>
        new()
        {
            DeathOriginalRecordedIndex = deathEventIndex,
            DeadActorStableId = actorId,
            DeathTimestampMilliseconds = deathTimestampMilliseconds,
            LastHits = lastHits.ToArray(),
            Confidence = CorrelationConfidence.Unavailable,
            Evidence = evidence,
            Limitations = limitations,
        };

    private static HpObservation? FindLastHpSampleAtOrBefore(
        PullRecord record,
        int actorId,
        long timestampMilliseconds)
    {
        HpObservation? result = null;
        foreach (var frame in record.Frames)
        {
            if (frame.TimestampMilliseconds > timestampMilliseconds)
            {
                break;
            }

            foreach (var actor in frame.Actors)
            {
                if (actor.StableActorId == actorId)
                {
                    result = new HpObservation(
                        frame.TimestampMilliseconds,
                        actor.CurrentHp,
                        actor.MaxHp,
                        actor.IsDead);
                    break;
                }
            }
        }

        return result;
    }

    private static HpObservation? FindFirstHpSampleAtOrAfter(
        PullRecord record,
        int actorId,
        long timestampMilliseconds)
    {
        foreach (var frame in record.Frames)
        {
            if (frame.TimestampMilliseconds < timestampMilliseconds)
            {
                continue;
            }

            foreach (var actor in frame.Actors)
            {
                if (actor.StableActorId == actorId)
                {
                    return new HpObservation(
                        frame.TimestampMilliseconds,
                        actor.CurrentHp,
                        actor.MaxHp,
                        actor.IsDead);
                }
            }
        }

        return null;
    }

    private readonly record struct HpObservation(
        long TimestampMilliseconds,
        uint CurrentHp,
        uint MaxHp,
        bool IsDead);

    private readonly record struct HpDelta(
        CorrelatedDamageEvent Event,
        ActionEffectKind Kind);

    private sealed class HpDeltaComparer : IComparer<HpDelta>
    {
        public static HpDeltaComparer Instance { get; } = new();

        public int Compare(HpDelta left, HpDelta right) =>
            CorrelatedDamageComparer.Instance.Compare(left.Event, right.Event);
    }

    private sealed class CorrelatedDamageComparer : IComparer<CorrelatedDamageEvent>
    {
        public static CorrelatedDamageComparer Instance { get; } = new();

        public int Compare(CorrelatedDamageEvent left, CorrelatedDamageEvent right)
        {
            var result = left.TimestampMilliseconds.CompareTo(right.TimestampMilliseconds);
            if (result != 0)
            {
                return result;
            }

            result = left.GlobalSequence.CompareTo(right.GlobalSequence);
            if (result != 0)
            {
                return result;
            }

            result = left.TargetSlotIndex.CompareTo(right.TargetSlotIndex);
            return result != 0
                ? result
                : left.EffectEntryIndex.CompareTo(right.EffectEntryIndex);
        }
    }
}
