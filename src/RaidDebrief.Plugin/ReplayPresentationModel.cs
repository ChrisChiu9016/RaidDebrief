using System;
using System.Collections.Generic;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal readonly record struct ReplayDeathItem(
    ActorRecord Actor,
    DeathEventCorrelation Correlation);

internal sealed class ReplayPresentationModel
{
    private readonly Dictionary<int, ReplayDeathItem> deathsByRecordedIndex;

    private ReplayPresentationModel(
        DebriefSummary summary,
        ActorRecord[] partyActors,
        ActorRecord? bossActor,
        ReplayDeathItem[] deaths)
    {
        this.Summary = summary;
        this.PartyActors = partyActors;
        this.BossActor = bossActor;
        this.Deaths = deaths;
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
            deaths.ToArray());
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
