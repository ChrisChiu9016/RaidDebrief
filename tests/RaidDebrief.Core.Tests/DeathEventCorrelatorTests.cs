using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class DeathEventCorrelatorTests
{
    [Fact]
    public void CorrelatesOrderedDamageWithVirtualHpAndOverkill()
    {
        var record = CreateRecord(
            CaptureFeatures.ActionEffectCapture,
            [
                CreateDamage(100, 10, 1001, 400),
                CreateDamage(200, 11, 1002, 700),
            ]);

        var correlation = Assert.Single(new DeathEventCorrelator().Analyze(record));

        Assert.Equal(CorrelationConfidence.High, correlation.Confidence);
        Assert.Equal(1002u, correlation.KillingBlowCandidate?.ActionId);
        Assert.Equal(700u, correlation.KillingBlowCandidate?.Amount);
        Assert.Equal(600u, correlation.EstimatedHpBeforeHit);
        Assert.Equal(100u, correlation.EstimatedOverkill);
        Assert.Equal(2, correlation.LastHits.Length);
        Assert.True(
            (correlation.Evidence & DeathCorrelationEvidence.VirtualHpCrossedZero) != 0);
        Assert.Equal(DeathEventCorrelation.CurrentAlgorithmVersion, correlation.AlgorithmVersion);
    }

    [Fact]
    public void ReportsUnavailableWhenNoIncomingDamageWasRecorded()
    {
        var correlation = Assert.Single(
            new DeathEventCorrelator().Analyze(CreateRecord(CaptureFeatures.None, [])));

        Assert.Equal(CorrelationConfidence.Unavailable, correlation.Confidence);
        Assert.Null(correlation.KillingBlowCandidate);
        Assert.True(
            (correlation.Limitations & DeathCorrelationLimitations.ActionEffectCaptureUnavailable) != 0);
    }

    [Fact]
    public void BarrierStandingAtDeathJoinsTheAbsorbablePoolAndShrinksOverkill()
    {
        // 1,000 HP plus a 30% barrier absorbs 1,300 before the 1,400 blow overkills by 100.
        var correlation = Assert.Single(
            new DeathEventCorrelator().Analyze(
                CreateBarrierRecord(barrierPercentage: 30, statusLostRemaining: 12f, damage: 1_400)));

        Assert.Equal(BarrierDisposition.Present, correlation.Barrier.Disposition);
        Assert.True(correlation.Barrier.StoodAgainstTheKillingBlow);
        Assert.Equal(300u, correlation.Barrier.AmountAtDeath);
        Assert.Equal(1_000u, correlation.EstimatedHpBeforeHit);
        Assert.Equal(1_300u, correlation.EstimatedEffectivePoolBeforeHit);
        Assert.Equal(100u, correlation.EstimatedOverkill);
        Assert.True(
            (correlation.Limitations & DeathCorrelationLimitations.BarrierAbsorptionUnverified) != 0);
    }

    [Fact]
    public void StatusDurationRemainingDoesNotChangeTheBarrierVerdict()
    {
        // Dying strips every status at once, so the duration left on a lost status carries
        // no information about the barrier. Identical captures that differ only in that
        // duration must produce identical results.
        var shortRemaining = Assert.Single(
            new DeathEventCorrelator().Analyze(
                CreateBarrierRecord(barrierPercentage: 30, statusLostRemaining: 0.04f, damage: 1_400)));
        var longRemaining = Assert.Single(
            new DeathEventCorrelator().Analyze(
                CreateBarrierRecord(barrierPercentage: 30, statusLostRemaining: 24f, damage: 1_400)));
        var noStatusLoss = Assert.Single(
            new DeathEventCorrelator().Analyze(
                CreateBarrierRecord(barrierPercentage: 30, statusLostRemaining: null, damage: 1_400)));

        Assert.Equal(shortRemaining.Barrier, longRemaining.Barrier);
        Assert.Equal(shortRemaining.Barrier, noStatusLoss.Barrier);
        Assert.Equal(BarrierDisposition.Present, shortRemaining.Barrier.Disposition);
        Assert.Equal(100u, shortRemaining.EstimatedOverkill);
        Assert.Equal(100u, longRemaining.EstimatedOverkill);
        Assert.Equal(100u, noStatusLoss.EstimatedOverkill);
    }

    [Fact]
    public void NoBarrierOnTheLastLivingSampleLeavesThePoolAtHealth()
    {
        var correlation = Assert.Single(
            new DeathEventCorrelator().Analyze(
                CreateBarrierRecord(barrierPercentage: 0, statusLostRemaining: 12f, damage: 1_400)));

        Assert.Equal(BarrierDisposition.None, correlation.Barrier.Disposition);
        Assert.Equal(0u, correlation.Barrier.AmountAtDeath);
        Assert.Equal(1_000u, correlation.EstimatedEffectivePoolBeforeHit);
        Assert.Equal(400u, correlation.EstimatedOverkill);
        Assert.True(
            (correlation.Limitations & DeathCorrelationLimitations.BarrierAbsorptionUnverified) == 0);
    }

    [Fact]
    public void CapturesWithoutBarrierStateReportTheBarrierAsNotRecorded()
    {
        var correlation = Assert.Single(
            new DeathEventCorrelator().Analyze(
                CreateRecord(
                    CaptureFeatures.ActionEffectCapture,
                    [CreateDamage(100, 10, 1001, 1_400)])));

        Assert.Equal(BarrierDisposition.NotRecorded, correlation.Barrier.Disposition);
        Assert.Equal(0u, correlation.Barrier.AmountAtDeath);
        Assert.Equal(
            correlation.EstimatedHpBeforeHit,
            correlation.EstimatedEffectivePoolBeforeHit);
    }

    [Fact]
    public void UsesLastLivingHpAndEffectPositionWhenHpObservationLags()
    {
        var record = CreateRecord(
            CaptureFeatures.ActionEffectCapture,
            [
                CreateDamage(100, 10, 1001, 400),
                CreateDamage(200, 11, 1002, 700),
            ]);
        var delayedLiving = CreateFrame(250, 600, false);
        record = record with
        {
            Frames = [record.Frames[0], delayedLiving, record.Frames[1]],
        };

        var correlation = Assert.Single(new DeathEventCorrelator().Analyze(record));

        Assert.Equal(1002u, correlation.KillingBlowCandidate?.ActionId);
        Assert.Equal(600u, correlation.EstimatedHpBeforeHit);
        Assert.Equal(100u, correlation.EstimatedOverkill);
    }

    [Fact]
    public void IncludesEffectsBeforeLastLivingFrameTimestamp()
    {
        var record = CreateRecord(
            CaptureFeatures.ActionEffectCapture,
            [CreateDamage(100, 10, 1001, 1_400)]);
        var delayedLiving = CreateFrame(250, 1_000, false);
        record = record with
        {
            Frames = [record.Frames[0], delayedLiving, record.Frames[1]],
        };

        var correlation = Assert.Single(new DeathEventCorrelator().Analyze(record));

        Assert.Equal(1001u, correlation.KillingBlowCandidate?.ActionId);
        Assert.Equal(1_000u, correlation.EstimatedHpBeforeHit);
        Assert.Equal(400u, correlation.EstimatedOverkill);
    }

    private static PullRecord CreateBarrierRecord(
        byte barrierPercentage,
        float? statusLostRemaining,
        uint damage)
    {
        var record = CreateRecord(
            CaptureFeatures.ActionEffectCapture | CaptureFeatures.BarrierState,
            [CreateDamage(100, 10, 1001, damage)]);
        var living = record.Frames[0].Actors[0] with { BarrierPercentage = barrierPercentage };
        var events = statusLostRemaining is { } remaining
            ? new[]
            {
                record.Events[0],
                new ObservedEvent
                {
                    TimestampMilliseconds = 300,
                    Type = ObservedEventType.StatusLost,
                    Source = ObservedEventSource.PolledStatusState,
                    StableActorId = 1,
                    StatusId = 297,
                    StatusRemainingTime = remaining,
                },
            }
            : record.Events;
        return record with
        {
            Frames = [record.Frames[0] with { Actors = [living] }, record.Frames[1]],
            Events = events,
        };
    }

    private static PullRecord CreateRecord(
        CaptureFeatures features,
        ActionEffectRecord[] actionEffects) =>
        new()
        {
            Features = features,
            CaptureId = Guid.NewGuid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
            TerritoryType = 1,
            MapId = 1,
            Instance = 0,
            Actors =
            [
                new ActorRecord
                {
                    StableActorId = 1,
                    Name = "Player 1",
                    ObjectKind = "Pc",
                    EntityId = 10,
                    GameObjectId = 100,
                    BaseId = 0,
                    ClassJobId = 39,
                    Level = 100,
                },
                new ActorRecord
                {
                    StableActorId = 2,
                    Name = "Boss",
                    ObjectKind = "BattleNpc",
                    EntityId = 20,
                    GameObjectId = 200,
                    BaseId = 1,
                    ClassJobId = 0,
                    Level = 100,
                },
            ],
            Frames =
            [
                CreateFrame(0, 1_000, false),
                CreateFrame(300, 0, true),
            ],
            Events =
            [
                new ObservedEvent
                {
                    TimestampMilliseconds = 300,
                    Type = ObservedEventType.Death,
                    Source = ObservedEventSource.PolledActorState,
                    StableActorId = 1,
                },
            ],
            ActionEffects = actionEffects,
        };

    private static PositionFrame CreateFrame(long timestamp, uint hp, bool isDead) =>
        new()
        {
            TimestampMilliseconds = timestamp,
            Actors =
            [
                new ActorStateSample
                {
                    StableActorId = 1,
                    X = 0,
                    Y = 0,
                    Z = 0,
                    Rotation = 0,
                    CurrentHp = hp,
                    MaxHp = 1_000,
                    IsDead = isDead,
                    IsTargetable = true,
                },
            ],
        };

    private static ActionEffectRecord CreateDamage(
        long timestamp,
        uint sequence,
        uint actionId,
        uint amount) =>
        new()
        {
            TimestampMilliseconds = timestamp,
            GlobalSequence = sequence,
            ActionId = actionId,
            ActionType = 1,
            SourceObjectId = 200,
            SourceStableActorId = 2,
            Targets =
            [
                new ActionEffectTargetRecord
                {
                    TargetObjectId = 100,
                    TargetStableActorId = 1,
                    Entries =
                    [
                        new ActionEffectEntryRecord
                        {
                            Index = 0,
                            Kind = ActionEffectKind.Damage,
                            RawType = ActionEffectDecoder.DamageType,
                            Param0 = 0,
                            Param1 = 0,
                            Param2 = 0,
                            Param3 = 0,
                            Param4 = 0,
                            Value = (ushort)amount,
                            Amount = amount,
                            IsCritical = false,
                            IsDirectHit = false,
                        },
                    ],
                },
            ],
        };
}
