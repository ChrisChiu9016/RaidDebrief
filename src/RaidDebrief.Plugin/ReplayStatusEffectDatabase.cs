using System;
using System.Collections.Generic;

namespace RaidDebrief.Plugin;

[Flags]
internal enum ReplayStatusEffectKind
{
    None = 0,
    DamageTakenReduction = 1 << 0,
    DamageDealtReduction = 1 << 1,
    Barrier = 1 << 2,
    HealingOverTime = 1 << 3,
    DefenseIncrease = 1 << 4,
    Invulnerability = 1 << 5,
}

internal readonly record struct ReplayStatusDescription(
    uint StatusId,
    string Description);

internal sealed class ReplayStatusEffectDatabase
{
    private const uint HaimaStatusId = 2612;
    private const uint PanhaimaStatusId = 2613;

    private readonly Dictionary<uint, ReplayStatusEffectKind> effects;
    private readonly uint[] statusIds;

    public ReplayStatusEffectDatabase(IEnumerable<ReplayStatusDescription> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        this.effects = new Dictionary<uint, ReplayStatusEffectKind>();
        var classifiedStatusIds = new List<uint>();
        foreach (var status in statuses)
        {
            if (status.StatusId == 0)
            {
                continue;
            }

            var kind = ClassifyDescription(status.Description);
            if (kind == ReplayStatusEffectKind.None
                || IsCollapsedBarrierBase(status.StatusId))
            {
                continue;
            }

            if (this.effects.TryAdd(status.StatusId, kind))
            {
                classifiedStatusIds.Add(status.StatusId);
            }
            else
            {
                this.effects[status.StatusId] |= kind;
            }
        }

        this.statusIds = [.. classifiedStatusIds];
    }

    public int Count => this.statusIds.Length;

    public ReadOnlySpan<uint> StatusIds => this.statusIds;

    public bool ShouldDisplayForPlayer(uint statusId, bool showHealingOverTime)
    {
        if (!this.effects.TryGetValue(statusId, out var kind))
        {
            return false;
        }

        const ReplayStatusEffectKind mitigationKinds =
            ReplayStatusEffectKind.DamageTakenReduction
            | ReplayStatusEffectKind.Barrier
            | ReplayStatusEffectKind.DefenseIncrease
            | ReplayStatusEffectKind.Invulnerability;
        return (kind & mitigationKinds) != 0
            || (showHealingOverTime
                && (kind & ReplayStatusEffectKind.HealingOverTime) != 0);
    }

    public bool ShouldDisplayForBoss(uint statusId) =>
        this.effects.TryGetValue(statusId, out var kind)
        && (kind & ReplayStatusEffectKind.DamageDealtReduction) != 0;

    public bool IsHealingOverTime(uint statusId) =>
        this.effects.TryGetValue(statusId, out var kind)
        && (kind & ReplayStatusEffectKind.HealingOverTime) != 0;

    private static bool IsCollapsedBarrierBase(uint statusId) =>
        statusId is HaimaStatusId or PanhaimaStatusId;

    internal static ReplayStatusEffectKind ClassifyDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return ReplayStatusEffectKind.None;
        }

        var kind = ReplayStatusEffectKind.None;
        if (Contains(description, "damage taken")
            && (Contains(description, "is reduced")
                || Contains(description, "are reduced")
                || Contains(description, "reducing damage taken")))
        {
            kind |= ReplayStatusEffectKind.DamageTakenReduction;
        }

        if (Contains(description, "damage dealt is reduced")
            || Contains(description, "physical and magic damage are reduced"))
        {
            kind |= ReplayStatusEffectKind.DamageDealtReduction;
        }

        if (Contains(description, "magic defense")
            && (Contains(description, "is increased")
                || Contains(description, "are increased")))
        {
            kind |= ReplayStatusEffectKind.DefenseIncrease;
        }

        if (Contains(description, "impervious to most attacks")
            || Contains(description, "unable to be ko'd by most attacks")
            || (Contains(description, "most attacks")
                && (Contains(description, "cannot reduce")
                    || Contains(description, "will not reduce"))
                && Contains(description, "hp")
                && (Contains(description, "less than 1")
                    || Contains(description, "below 1"))))
        {
            kind |= ReplayStatusEffectKind.Invulnerability;
        }

        if ((Contains(description, "barrier")
                && (Contains(description, "damage")
                    || Contains(description, "absorbed")
                    || Contains(description, "created")
                    || Contains(description, "reapplied")))
            || Contains(description, "nullifying damage"))
        {
            kind |= ReplayStatusEffectKind.Barrier;
        }

        if (Contains(description, "regenerating hp")
            || Contains(description, "regeneration of hp")
            || Contains(description, "gradually restoring hp")
            || Contains(description, "hp is gradually restored")
            || Contains(description, "hp is restored over time")
            || Contains(description, "restoring hp over time")
            || Contains(description, "restores hp over time"))
        {
            kind |= ReplayStatusEffectKind.HealingOverTime;
        }

        return kind;
    }

    private static bool Contains(string value, string phrase) =>
        value.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
