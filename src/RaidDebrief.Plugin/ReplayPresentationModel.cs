using System;
using System.Collections.Generic;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal readonly record struct ReplayDeathItem(
    ActorRecord Actor,
    DeathEventCorrelation Correlation);

internal readonly record struct ReplayHealthChange(
    long TimestampMilliseconds,
    uint GlobalSequence,
    uint ActionId,
    int? SourceStableActorId,
    int TargetSlotIndex,
    byte EffectEntryIndex,
    uint Amount,
    ActionEffectKind Kind)
{
    public bool IsDamageCandidate(CorrelatedDamageEvent? candidate) =>
        this.Kind == ActionEffectKind.Damage
        && candidate is { } value
        && new CorrelatedDamageEvent(
            this.TimestampMilliseconds,
            this.GlobalSequence,
            this.ActionId,
            this.SourceStableActorId,
            this.TargetSlotIndex,
            this.EffectEntryIndex,
            this.Amount) == value;
}

internal sealed class ReplayHealthChangeIndex
{
    internal const int MaximumVisibleChanges = 5;
    internal const long LookbackMilliseconds = 10_000;
    internal const uint BloodbathActionId = 7_542;
    internal const uint BloodbathStatusId = 84;
    internal const uint BloodwhettingActionId = 25_751;
    internal const uint BloodwhettingStatusId = 2_678;
    private readonly Dictionary<int, ReplayHealthChange[]> changesByActorId = [];

    public ReplayHealthChangeIndex(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var builders = new Dictionary<int, List<ReplayHealthChange>>();
        var activeSelfHealActions = new Dictionary<int, uint>();
        var eventIndex = 0;
        foreach (var actionEffect in record.ActionEffects)
        {
            while (eventIndex < record.Events.Length
                   && record.Events[eventIndex].TimestampMilliseconds
                   <= actionEffect.TimestampMilliseconds)
            {
                UpdateSelfHealSources(record.Events[eventIndex], activeSelfHealActions);
                eventIndex++;
            }

            for (var targetIndex = 0; targetIndex < actionEffect.Targets.Length; targetIndex++)
            {
                var target = actionEffect.Targets[targetIndex];
                foreach (var entry in target.Entries)
                {
                    if (entry.Kind is not (ActionEffectKind.Damage or ActionEffectKind.Heal)
                        || entry.Amount is not { } amount
                        || amount == 0
                        || ActionEffectDecoder.ResolveTargetStableActorId(
                            actionEffect,
                            target,
                            entry) is not { } targetActorId)
                    {
                        continue;
                    }

                    if (!builders.TryGetValue(targetActorId, out var changes))
                    {
                        changes = [];
                        builders.Add(targetActorId, changes);
                    }

                    var actionId = ActionEffectDecoder.IsSourceActorHeal(
                            entry.RawType,
                            entry.Param0)
                        && activeSelfHealActions.TryGetValue(
                            targetActorId,
                            out var selfHealActionId)
                            ? selfHealActionId
                            : actionEffect.ActionId;
                    changes.Add(new ReplayHealthChange(
                        actionEffect.TimestampMilliseconds,
                        actionEffect.GlobalSequence,
                        actionId,
                        actionEffect.SourceStableActorId,
                        targetIndex,
                        entry.Index,
                        amount,
                        entry.Kind));
                }
            }
        }

        foreach (var (actorId, changes) in builders)
        {
            changes.Sort(static (left, right) =>
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
            });
            this.changesByActorId.Add(actorId, changes.ToArray());
        }
    }

    private static void UpdateSelfHealSources(
        ObservedEvent observedEvent,
        Dictionary<int, uint> activeActions)
    {
        if (observedEvent.StableActorId is not { } actorId)
        {
            return;
        }

        if (observedEvent.Type is ObservedEventType.Death or ObservedEventType.ActorDespawned)
        {
            activeActions.Remove(actorId);
            return;
        }

        var actionId = observedEvent.StatusId switch
        {
            BloodbathStatusId => BloodbathActionId,
            BloodwhettingStatusId => BloodwhettingActionId,
            _ => 0u,
        };
        if (actionId == 0)
        {
            return;
        }

        if (observedEvent.Type is ObservedEventType.StatusGained
            or ObservedEventType.StatusRefreshed)
        {
            activeActions[actorId] = actionId;
        }
        else if (observedEvent.Type == ObservedEventType.StatusLost
                 && activeActions.TryGetValue(actorId, out var activeActionId)
                 && activeActionId == actionId)
        {
            activeActions.Remove(actorId);
        }
    }

    public ReadOnlySpan<ReplayHealthChange> GetChangesInWindow(
        int actorId,
        long timestampMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        if (!this.changesByActorId.TryGetValue(actorId, out var changes))
        {
            return ReadOnlySpan<ReplayHealthChange>.Empty;
        }

        var firstAfter = FindFirstAfter(changes, timestampMilliseconds);
        var startTimestamp = timestampMilliseconds > LookbackMilliseconds
            ? timestampMilliseconds - LookbackMilliseconds
            : 0;
        var firstInWindow = FindFirstAtOrAfter(changes, startTimestamp);
        return changes.AsSpan(firstInWindow, firstAfter - firstInWindow);
    }

    public ReadOnlySpan<ReplayHealthChange> GetRecentChanges(
        int actorId,
        long timestampMilliseconds)
    {
        var changes = this.GetChangesInWindow(actorId, timestampMilliseconds);
        var firstVisible = Math.Max(0, changes.Length - MaximumVisibleChanges);
        return changes[firstVisible..];
    }

    private static int FindFirstAtOrAfter(
        ReplayHealthChange[] changes,
        long timestampMilliseconds)
    {
        var lower = 0;
        var upper = changes.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (changes[middle].TimestampMilliseconds < timestampMilliseconds)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower;
    }

    private static int FindFirstAfter(
        ReplayHealthChange[] changes,
        long timestampMilliseconds)
    {
        var lower = 0;
        var upper = changes.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (changes[middle].TimestampMilliseconds <= timestampMilliseconds)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower;
    }
}


internal sealed class ReplayPresentationModel
{
    private readonly Dictionary<int, ReplayDeathItem> deathsByRecordedIndex;

    private ReplayPresentationModel(
        DebriefSummary summary,
        ActorRecord[] partyActors,
        ActorRecord? bossActor,
        ReplayDeathItem[] deaths,
        ReplayHealthChangeIndex healthChanges)
    {
        this.Summary = summary;
        this.PartyActors = partyActors;
        this.BossActor = bossActor;
        this.Deaths = deaths;
        this.HealthChanges = healthChanges;
        this.deathsByRecordedIndex = new Dictionary<int, ReplayDeathItem>(deaths.Length);
        foreach (var death in deaths)
        {
            this.deathsByRecordedIndex.Add(
                death.Correlation.DeathOriginalRecordedIndex,
                death);
        }
    }

    public DebriefSummary Summary { get; }

    public ActorRecord[] PartyActors { get; }

    public ActorRecord? BossActor { get; }

    public ReplayDeathItem[] Deaths { get; }

    public ReplayHealthChangeIndex HealthChanges { get; }

    public static ReplayPresentationModel Create(
        PullRecord record,
        DebriefSummary? sourceSummary = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var summary = sourceSummary is { } existing && existing.CaptureId == record.CaptureId
            ? existing
            : new DebriefAnalyzer().Analyze(record);

        var actorsById = new Dictionary<int, ActorRecord>(record.Actors.Length);
        var playerOwnerIds = new HashSet<ulong>();
        var partyActors = new List<ActorRecord>(8);
        foreach (var actor in record.Actors)
        {
            actorsById.Add(actor.StableActorId, actor);
            if (string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal))
            {
                playerOwnerIds.Add(actor.EntityId);
                playerOwnerIds.Add(actor.GameObjectId);
            }
            if (string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal)
                && actor.PartyIndex is not null)
            {
                partyActors.Add(actor);
            }
        }

        if (partyActors.Count == 0)
        {
            foreach (var actor in record.Actors)
            {
                if (string.Equals(actor.ObjectKind, "Pc", StringComparison.Ordinal))
                {
                    partyActors.Add(actor);
                }
            }
        }

        // Role order keeps the same seat for a role across pulls. The recorded
        // PartyIndex is Dalamud's raw group array, which is neither the in-game
        // party list order nor stable between pulls, so it only breaks ties.
        partyActors.Sort(static (left, right) =>
        {
            var result = JobIconResources.GetRoleOrder(left.ClassJobId)
                .CompareTo(JobIconResources.GetRoleOrder(right.ClassJobId));
            if (result != 0)
            {
                return result;
            }

            result = (left.PartyIndex ?? int.MaxValue).CompareTo(right.PartyIndex ?? int.MaxValue);
            return result != 0
                ? result
                : left.StableActorId.CompareTo(right.StableActorId);
        });

        var correlations = new DeathEventCorrelator().Analyze(record);
        var deaths = new List<ReplayDeathItem>(correlations.Length);
        foreach (var correlation in correlations)
        {
            if (actorsById.TryGetValue(correlation.DeadActorStableId, out var actor))
            {
                deaths.Add(new ReplayDeathItem(actor, correlation));
            }
        }

        deaths.Sort(static (left, right) =>
        {
            var result = left.Correlation.DeathTimestampMilliseconds.CompareTo(
                right.Correlation.DeathTimestampMilliseconds);
            return result != 0
                ? result
                : left.Correlation.DeathOriginalRecordedIndex.CompareTo(
                    right.Correlation.DeathOriginalRecordedIndex);
        });

        return new ReplayPresentationModel(
            summary,
            partyActors.ToArray(),
            ResolveBoss(record, summary, actorsById, playerOwnerIds),
            deaths.ToArray(),
            new ReplayHealthChangeIndex(record));
    }

    public bool TryGetDeath(int originalRecordedIndex, out ReplayDeathItem death) =>
        this.deathsByRecordedIndex.TryGetValue(originalRecordedIndex, out death);

    private static ActorRecord? ResolveBoss(
        PullRecord record,
        DebriefSummary summary,
        IReadOnlyDictionary<int, ActorRecord> actorsById,
        IReadOnlySet<ulong> playerOwnerIds)
    {
        if (summary.BossHpAtEnd is { } summaryBoss
            && actorsById.TryGetValue(summaryBoss.StableActorId, out var exactBoss))
        {
            return exactBoss;
        }

        ActorRecord? result = null;
        uint largestMaxHp = 0;
        foreach (var frame in record.Frames)
        {
            foreach (var sample in frame.Actors)
            {
                if (sample.MaxHp <= largestMaxHp
                    || !actorsById.TryGetValue(sample.StableActorId, out var actor)
                    || !string.Equals(actor.ObjectKind, "BattleNpc", StringComparison.Ordinal)
                    || (actor.OwnerId != 0 && playerOwnerIds.Contains(actor.OwnerId)))
                {
                    continue;
                }

                largestMaxHp = sample.MaxHp;
                result = actor;
            }
        }

        return result;
    }
}
