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
    /// <summary>
    /// A barrier stood between the actor and the killing blow, but the recorded
    /// Action Effect amount cannot be reconciled against the absorbed portion.
    /// </summary>
    BarrierAbsorptionUnverified = 1 << 7,
}

/// <summary>
/// What the recorded barrier was doing when the actor died.
/// </summary>
/// <remarks>
/// Deliberately not split into "consumed" versus "expired". Dying strips every status at
/// once, so the duration remaining on a lost status says nothing about the barrier at the
/// moment of death; a real capture shows ten statuses ending together with between 0.8 s
/// and 24.6 s remaining. The barrier percentage carried by the last living sample is the
/// only sound signal, and it self-aligns: that sample reports the state roughly one HP
/// observation lag earlier, which is approximately when the killing blow landed.
/// </remarks>
public enum BarrierDisposition
{
    /// <summary>The capture predates <see cref="CaptureFeatures.BarrierState"/>.</summary>
    NotRecorded,

    /// <summary>No barrier was recorded on the last sample before death.</summary>
    None,

    /// <summary>A barrier was still recorded on the last sample before death.</summary>
    Present,
}

public readonly record struct DeathBarrierObservation(
    BarrierDisposition Disposition,
    uint AmountAtDeath,
    byte PercentageAtDeath)
{
    public bool StoodAgainstTheKillingBlow => this.Disposition == BarrierDisposition.Present;
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
    public const int CurrentAlgorithmVersion = 2;

    public required int DeathOriginalRecordedIndex { get; init; }

    public required int DeadActorStableId { get; init; }

    public required long DeathTimestampMilliseconds { get; init; }

    public CorrelatedDamageEvent? KillingBlowCandidate { get; init; }

    /// <summary>
    /// Health remaining before the killing blow, excluding any barrier.
    /// </summary>
    public uint? EstimatedHpBeforeHit { get; init; }

    /// <summary>
    /// Total damage the actor could still absorb before the killing blow: health plus a
    /// barrier that the recorded status timeline shows was consumed rather than expired.
    /// Equal to <see cref="EstimatedHpBeforeHit"/> when no barrier stood against the blow.
    /// </summary>
    public uint? EstimatedEffectivePoolBeforeHit { get; init; }

    /// <summary>
    /// Recorded damage in excess of <see cref="EstimatedEffectivePoolBeforeHit"/>.
    /// </summary>
    public uint? EstimatedOverkill { get; init; }

    public DeathBarrierObservation Barrier { get; init; }

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
    private const long DefaultHpObservationLagMilliseconds = 1_000;
    private const long MaximumCalibrationLagMilliseconds = 2_000;
    private const int MinimumCalibrationSamples = 3;
    private const int MaximumLastHits = 5;

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

        var hpLagCalibration = CalibrateHpObservationLag(record, playerActorIds);
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

            results.Add(
                this.AnalyzeDeath(
                    record,
                    eventIndex,
                    actorId,
                    observedEvent.TimestampMilliseconds,
                    hpLagCalibration.ThresholdMilliseconds));
        }

        return results.ToArray();
    }

    private DeathEventCorrelation AnalyzeDeath(
        PullRecord record,
        int deathEventIndex,
        int actorId,
        long deathTimestampMilliseconds,
        long hpLagThresholdMilliseconds)
    {
        var lastHits = new List<CorrelatedDamageEvent>(MaximumLastHits + 8);
        var deltas = new List<HpDelta>(32);
        var simulationStart = Math.Max(
            0,
            deathTimestampMilliseconds
                - CorrelationLookbackMilliseconds
                - hpLagThresholdMilliseconds);
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

                foreach (var entry in target.Entries)
                {
                    if (ActionEffectDecoder.ResolveTargetStableActorId(
                            actionEffect,
                            target,
                            entry) != actorId)
                    {
                        continue;
                    }
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

        var anchor = FindLastLivingHpSampleBeforeDeath(
            record,
            actorId,
            deathTimestampMilliseconds);
        var barrier = ResolveDeathBarrier(record, actorId, deathTimestampMilliseconds);
        uint? estimatedHpBeforeHit = null;
        uint? estimatedPoolBeforeHit = null;
        uint? estimatedOverkill = null;
        CorrelatedDamageEvent? candidate = null;
        var crossingCount = 0;
        long candidateTimestamp = 0;

        if (anchor is { CurrentHp: > 0 })
        {
            evidence |= DeathCorrelationEvidence.HpAnchorAvailable;
            long initialBarrierPool =
                barrier.StoodAgainstTheKillingBlow ? barrier.AmountAtDeath : 0;
            if (initialBarrierPool > 0)
            {
                limitations |= DeathCorrelationLimitations.BarrierAbsorptionUnverified;
            }

            // The HP sample and Action Effect callback use the same clock but are not
            // content-synchronous. Start from the last observed living HP, then choose
            // the latest ordered suffix of effects that can cross zero. The suffix index,
            // rather than its timestamp relative to the sample, is the replay anchor.
            var replayStartIndex = FindLatestCrossingSuffix(
                deltas,
                anchor.Value,
                initialBarrierPool);
            if (replayStartIndex is { } startIndex)
            {
                long virtualHp = anchor.Value.CurrentHp;
                var barrierPool = initialBarrierPool;
                for (var deltaIndex = startIndex; deltaIndex < deltas.Count; deltaIndex++)
                {
                    var delta = deltas[deltaIndex];
                    if (delta.Kind == ActionEffectKind.Heal)
                    {
                        virtualHp = Math.Min(anchor.Value.MaxHp, virtualHp + delta.Event.Amount);
                        continue;
                    }

                    var hpBefore = Math.Max(0, virtualHp);
                    var poolBefore = hpBefore + barrierPool;
                    var absorbed = Math.Min(barrierPool, (long)delta.Event.Amount);
                    barrierPool -= absorbed;
                    virtualHp -= delta.Event.Amount - absorbed;
                    if (poolBefore > 0 && virtualHp <= 0)
                    {
                        crossingCount++;
                        candidate = delta.Event;
                        candidateTimestamp = delta.Event.TimestampMilliseconds;
                        estimatedHpBeforeHit = (uint)Math.Min(uint.MaxValue, hpBefore);
                        estimatedPoolBeforeHit = (uint)Math.Min(uint.MaxValue, poolBefore);
                        estimatedOverkill =
                            (uint)Math.Min(uint.MaxValue, Math.Max(0, -virtualHp));
                    }
                }
            }

            if (candidate is not null)
            {
                evidence |= DeathCorrelationEvidence.VirtualHpCrossedZero;
                var deathSample = FindFirstHpSampleAtOrAfter(
                    record,
                    actorId,
                    deathTimestampMilliseconds);
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

        if (deathTimestampMilliseconds - candidate.Value.TimestampMilliseconds
            > hpLagThresholdMilliseconds)
        {
            limitations |= DeathCorrelationLimitations.HpObservationLag;
        }

        var confidence = CorrelationConfidence.Low;
        if ((evidence & DeathCorrelationEvidence.VirtualHpCrossedZero) != 0)
        {
            const DeathCorrelationLimitations highConfidenceBlockers =
                DeathCorrelationLimitations.AmbiguousEffectOrdering
                | DeathCorrelationLimitations.HpObservationLag
                | DeathCorrelationLimitations.BarrierAbsorptionUnverified;
            confidence = (record.Features & CaptureFeatures.ActionEffectCapture) != 0
                && crossingCount == 1
                && (limitations & highConfidenceBlockers) == 0
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
            EstimatedEffectivePoolBeforeHit = estimatedPoolBeforeHit,
            EstimatedOverkill = estimatedOverkill,
            Barrier = barrier,
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


    /// <summary>
    /// Reads the barrier the actor still carried on the last sample where they were alive.
    /// See <see cref="BarrierDisposition"/> for why the status timeline cannot refine this.
    /// </summary>
    internal static DeathBarrierObservation ResolveDeathBarrier(
        PullRecord record,
        int actorId,
        long deathTimestampMilliseconds)
    {
        if ((record.Features & CaptureFeatures.BarrierState) == 0)
        {
            return new DeathBarrierObservation(BarrierDisposition.NotRecorded, 0, 0);
        }

        ActorStateSample? lastLiving = null;
        foreach (var frame in record.Frames)
        {
            var dead = false;
            foreach (var actor in frame.Actors)
            {
                if (actor.StableActorId != actorId)
                {
                    continue;
                }

                if (frame.TimestampMilliseconds >= deathTimestampMilliseconds
                    && (actor.IsDead || actor.CurrentHp == 0))
                {
                    dead = true;
                }
                else
                {
                    lastLiving = actor;
                }

                break;
            }

            if (dead)
            {
                break;
            }
        }

        if (lastLiving is not { BarrierPercentage: > 0 } sample)
        {
            return new DeathBarrierObservation(BarrierDisposition.None, 0, 0);
        }

        return new DeathBarrierObservation(
            BarrierDisposition.Present,
            (uint)(((ulong)sample.MaxHp * sample.BarrierPercentage) / 100),
            sample.BarrierPercentage);
    }

    private static HpObservation? FindLastLivingHpSampleBeforeDeath(
        PullRecord record,
        int actorId,
        long deathTimestampMilliseconds)
    {
        HpObservation? result = null;
        foreach (var frame in record.Frames)
        {
            if (frame.TimestampMilliseconds >= deathTimestampMilliseconds)
            {
                break;
            }

            foreach (var actor in frame.Actors)
            {
                if (actor.StableActorId == actorId
                    && !actor.IsDead
                    && actor.CurrentHp > 0)
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

    private static int? FindLatestCrossingSuffix(
        List<HpDelta> deltas,
        in HpObservation anchor,
        long initialBarrierPool)
    {
        for (var startIndex = deltas.Count - 1; startIndex >= 0; startIndex--)
        {
            long virtualHp = anchor.CurrentHp;
            var barrierPool = initialBarrierPool;
            for (var deltaIndex = startIndex; deltaIndex < deltas.Count; deltaIndex++)
            {
                var delta = deltas[deltaIndex];
                if (delta.Kind == ActionEffectKind.Heal)
                {
                    virtualHp = Math.Min(anchor.MaxHp, virtualHp + delta.Event.Amount);
                    continue;
                }

                var absorbed = Math.Min(barrierPool, (long)delta.Event.Amount);
                barrierPool -= absorbed;
                virtualHp -= delta.Event.Amount - absorbed;
            }

            if (virtualHp <= 0)
            {
                return startIndex;
            }
        }

        return null;
    }

    private static HpLagCalibration CalibrateHpObservationLag(
        PullRecord record,
        HashSet<int> playerActorIds)
    {
        var observations = new List<long>(32);
        var previousSamples = new Dictionary<int, HpObservation>(playerActorIds.Count);
        foreach (var frame in record.Frames)
        {
            foreach (var actor in frame.Actors)
            {
                if (!playerActorIds.Contains(actor.StableActorId))
                {
                    continue;
                }

                var current = new HpObservation(
                    frame.TimestampMilliseconds,
                    actor.CurrentHp,
                    actor.MaxHp,
                    actor.IsDead);
                if (previousSamples.TryGetValue(actor.StableActorId, out var previous)
                    && !previous.IsDead
                    && !current.IsDead
                    && previous.CurrentHp != current.CurrentHp
                    && actor.BarrierPercentage == 0)
                {
                    TryAddCalibrationObservation(
                        record,
                        actor.StableActorId,
                        previous,
                        current,
                        observations);
                }

                previousSamples[actor.StableActorId] = current;
            }
        }

        if (observations.Count < MinimumCalibrationSamples)
        {
            return new HpLagCalibration(DefaultHpObservationLagMilliseconds);
        }

        observations.Sort();
        var percentileIndex = (observations.Count * 9) / 10;
        var threshold = observations[Math.Min(observations.Count - 1, percentileIndex)];
        return new HpLagCalibration(
            Math.Clamp(
                threshold,
                DefaultHpObservationLagMilliseconds / 2,
                MaximumCalibrationLagMilliseconds));
    }

    private static void TryAddCalibrationObservation(
        PullRecord record,
        int actorId,
        in HpObservation previous,
        in HpObservation current,
        List<long> destination)
    {
        HpDelta? match = null;
        var matchCount = 0;
        var windowStart = Math.Max(
            0,
            current.TimestampMilliseconds - MaximumCalibrationLagMilliseconds);
        foreach (var actionEffect in record.ActionEffects)
        {
            if (actionEffect.TimestampMilliseconds < windowStart)
            {
                continue;
            }

            if (actionEffect.TimestampMilliseconds > current.TimestampMilliseconds)
            {
                break;
            }

            for (var targetIndex = 0; targetIndex < actionEffect.Targets.Length; targetIndex++)
            {
                var target = actionEffect.Targets[targetIndex];

                foreach (var entry in target.Entries)
                {
                    if (ActionEffectDecoder.ResolveTargetStableActorId(
                            actionEffect,
                            target,
                            entry) != actorId)
                    {
                        continue;
                    }
                    if (entry.Amount is not { } amount
                        || entry.Kind is not (ActionEffectKind.Damage or ActionEffectKind.Heal))
                    {
                        continue;
                    }

                    var observedAmount = previous.CurrentHp > current.CurrentHp
                        ? previous.CurrentHp - current.CurrentHp
                        : current.CurrentHp - previous.CurrentHp;
                    var expectedKind = previous.CurrentHp > current.CurrentHp
                        ? ActionEffectKind.Damage
                        : ActionEffectKind.Heal;
                    if (entry.Kind != expectedKind || amount != observedAmount)
                    {
                        continue;
                    }

                    matchCount++;
                    match = new HpDelta(
                        new CorrelatedDamageEvent(
                            actionEffect.TimestampMilliseconds,
                            actionEffect.GlobalSequence,
                            actionEffect.ActionId,
                            actionEffect.SourceStableActorId,
                            targetIndex,
                            entry.Index,
                            amount),
                        entry.Kind);
                }
            }
        }

        if (matchCount == 1 && match is { } unique)
        {
            destination.Add(current.TimestampMilliseconds - unique.Event.TimestampMilliseconds);
        }
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

    private readonly record struct HpLagCalibration(long ThresholdMilliseconds);

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
