using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayPresentationModelTests
{
    [Fact]
    public void OrdersPartyByRoleThenPartyIndexAndPicksLargestUnownedBattleNpc()
    {
        // PartyIndex order would be second(0), first(1), tank(2), melee(3).
        // Role order must hoist the tank and healer above both melee jobs.
        var first = CreateActor(1, "Player 1", "Pc", 100, 39, partyIndex: 1);
        var second = CreateActor(2, "Player 2", "Pc", 200, 24, partyIndex: 0);
        var tank = CreateActor(6, "Player 3", "Pc", 600, 21, partyIndex: 2);
        var melee = CreateActor(7, "Player 4", "Pc", 700, 20, partyIndex: 3);
        var add = CreateActor(3, "Add", "BattleNpc", 300, 0);
        var boss = CreateActor(
            4,
            "Boss",
            "BattleNpc",
            400,
            0,
            ownerId: 0xE0000000);
        var pet = CreateActor(
            5,
            "Pet",
            "BattleNpc",
            500,
            0,
            ownerId: first.GameObjectId);
        var record = new PullRecord
        {
            Features = CaptureFeatures.PartyMembership,
            CaptureId = Guid.NewGuid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
            TerritoryType = 1,
            MapId = 1,
            Instance = 0,
            Actors = [first, second, add, boss, pet, tank, melee],
            Frames =
            [
                new PositionFrame
                {
                    TimestampMilliseconds = 0,
                    Actors =
                    [
                        CreateSample(1, 100),
                        CreateSample(2, 100),
                        CreateSample(3, 1_000),
                        CreateSample(4, 10_000),
                        CreateSample(5, 100_000),
                        CreateSample(6, 100),
                        CreateSample(7, 100),
                    ],
                },
            ],
        };

        var presentation = ReplayPresentationModel.Create(record);

        // WAR tank, WHM healer, then the two melee ordered by PartyIndex.
        Assert.Equal([6, 2, 1, 7], presentation.PartyActors.Select(actor => actor.StableActorId));
        Assert.Equal(4, presentation.BossActor?.StableActorId);
        Assert.Empty(presentation.Deaths);
    }

    [Fact]
    public void HealthChangesArePastOnlyWindowedCappedAndClassified()
    {
        var player = CreateActor(1, "Player 1", "Pc", 100, 39, partyIndex: 0);
        var boss = CreateActor(4, "Boss", "BattleNpc", 400, 0);
        var record = new PullRecord
        {
            Features = CaptureFeatures.ActionEffectCapture,
            CaptureId = Guid.NewGuid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
            TerritoryType = 1,
            MapId = 1,
            Instance = 0,
            Actors = [player, boss],
            Frames =
            [
                new PositionFrame
                {
                    TimestampMilliseconds = 0,
                    Actors = [CreateSample(1, 100)],
                },
            ],
            ActionEffects =
            [
                CreateHealthChange(100, 1, 1001, ActionEffectKind.Damage, 101),
                CreateHealthChange(200, 2, 1002, ActionEffectKind.Damage, 102),
                CreateHealthChange(300, 3, 1003, ActionEffectKind.Damage, 103),
                CreateHealthChange(400, 4, 1004, ActionEffectKind.Damage, 104),
                CreateHealthChange(500, 5, 2005, ActionEffectKind.Heal, 777),
                CreateHealthChange(600, 6, 1006, ActionEffectKind.Damage, 106),
                CreateHealthChange(700, 7, 1007, ActionEffectKind.Damage, 107),
                CreateHealthChange(800, 8, 1008, ActionEffectKind.Damage, 108),
                CreateHealthChange(900, 9, 1009, ActionEffectKind.Damage, 109),
                CreateHealthChange(1_000, 10, 1010, ActionEffectKind.Damage, 110),
                CreateHealthChange(1_100, 11, 1011, ActionEffectKind.Damage, 111),
                CreateHealthChange(1_200, 12, 1012, ActionEffectKind.Damage, 112),
            ],
        };
        var presentation = ReplayPresentationModel.Create(record);

        var all = presentation.HealthChanges.GetChangesInWindow(1, 650).ToArray();
        Assert.Equal(
            [1001u, 1002u, 1003u, 1004u, 2005u, 1006u],
            all.Select(change => change.ActionId));
        Assert.Equal(
            [
                1001u,
                1002u,
                1003u,
                1004u,
                2005u,
                1006u,
                1007u,
                1008u,
                1009u,
                1010u,
                1011u,
                1012u,
            ],
            presentation.HealthChanges
                .GetChangesInWindow(1, 1_200)
                .ToArray()
                .Select(change => change.ActionId));
        var latest = presentation.HealthChanges.GetRecentChanges(1, 650).ToArray();
        Assert.Equal(
            [1002u, 1003u, 1004u, 2005u, 1006u],
            latest.Select(change => change.ActionId));
        Assert.Equal(
            [
                ActionEffectKind.Damage,
                ActionEffectKind.Damage,
                ActionEffectKind.Damage,
                ActionEffectKind.Heal,
                ActionEffectKind.Damage,
            ],
            latest.Select(change => change.Kind));
        Assert.Equal(777u, latest[3].Amount);
        Assert.Equal(
            [1001u, 1002u],
            presentation.HealthChanges
                .GetRecentChanges(1, 250)
                .ToArray()
                .Select(change => change.ActionId));
        Assert.Equal(
            1012u,
            Assert.Single(
                presentation.HealthChanges
                    .GetRecentChanges(1, 11_200)
                    .ToArray()).ActionId);
        Assert.Empty(presentation.HealthChanges.GetRecentChanges(1, 11_201).ToArray());
        Assert.Empty(presentation.HealthChanges.GetRecentChanges(999, 650).ToArray());
        Assert.Empty(presentation.HealthChanges.GetChangesInWindow(999, 650).ToArray());
    }

    [Theory]
    [InlineData(
        ReplayHealthChangeIndex.BloodbathStatusId,
        ReplayHealthChangeIndex.BloodbathActionId)]
    [InlineData(
        ReplayHealthChangeIndex.BloodwhettingStatusId,
        ReplayHealthChangeIndex.BloodwhettingActionId)]
    public void ActiveSelfHealUsesCasterAsTargetAndActiveStatusAsSource(
        uint statusId,
        uint expectedActionId)
    {
        var player = CreateActor(1, "Player 1", "Pc", 100, 39, partyIndex: 0);
        var boss = CreateActor(4, "Boss", "BattleNpc", 400, 0);
        var record = new PullRecord
        {
            Features = CaptureFeatures.ActionEffectCapture,
            CaptureId = Guid.NewGuid(),
            StartedAtUtc = DateTimeOffset.UtcNow,
            EndedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
            TerritoryType = 1,
            MapId = 1,
            Instance = 0,
            Actors = [player, boss],
            Frames =
            [
                new PositionFrame
                {
                    TimestampMilliseconds = 0,
                    Actors = [CreateSample(1, 100)],
                },
            ],
            Events =
            [
                new ObservedEvent
                {
                    TimestampMilliseconds = 100,
                    Type = ObservedEventType.StatusGained,
                    Source = ObservedEventSource.PolledStatusState,
                    StableActorId = 1,
                    StatusId = statusId,
                },
                new ObservedEvent
                {
                    TimestampMilliseconds = 300,
                    Type = ObservedEventType.StatusLost,
                    Source = ObservedEventSource.PolledStatusState,
                    StableActorId = 1,
                    StatusId = statusId,
                },
            ],
            ActionEffects =
            [
                CreateSourceActorHeal(200, 1, 1234, 500, 250),
                CreateSourceActorHeal(400, 2, 4321, 600, 300),
            ],
        };

        var presentation = ReplayPresentationModel.Create(record);
        var playerChanges = presentation.HealthChanges.GetRecentChanges(1, 400).ToArray();
        var bossChanges = presentation.HealthChanges.GetRecentChanges(4, 400).ToArray();

        Assert.Equal(
            [expectedActionId, 4321u],
            playerChanges.Select(change => change.ActionId));
        Assert.All(playerChanges, change => Assert.Equal(ActionEffectKind.Heal, change.Kind));
        Assert.Equal([1234u, 4321u], bossChanges.Select(change => change.ActionId));
        Assert.All(bossChanges, change => Assert.Equal(ActionEffectKind.Damage, change.Kind));
    }

    private static ActorRecord CreateActor(
        int stableActorId,
        string name,
        string objectKind,
        ulong gameObjectId,
        uint classJobId,
        int? partyIndex = null,
        ulong ownerId = 0) =>
        new()
        {
            StableActorId = stableActorId,
            Name = name,
            ObjectKind = objectKind,
            EntityId = (uint)gameObjectId,
            GameObjectId = gameObjectId,
            OwnerId = ownerId,
            BaseId = stableActorId == 4 ? 1u : 0u,
            ClassJobId = classJobId,
            PartyIndex = partyIndex,
            Level = 100,
        };

    private static ActionEffectRecord CreateHealthChange(
        long timestampMilliseconds,
        uint globalSequence,
        uint actionId,
        ActionEffectKind kind,
        ushort amount) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            GlobalSequence = globalSequence,
            ActionId = actionId,
            ActionType = 1,
            SourceObjectId = 400,
            SourceStableActorId = 4,
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
                            Kind = kind,
                            RawType = kind == ActionEffectKind.Damage
                                ? ActionEffectDecoder.DamageType
                                : ActionEffectDecoder.HealType,
                            Param0 = 0,
                            Param1 = 0,
                            Param2 = 0,
                            Param3 = 0,
                            Param4 = 0,
                            Value = amount,
                            Amount = amount,
                            IsCritical = false,
                            IsDirectHit = false,
                        },
                    ],
                },
            ],
        };


    private static ActionEffectRecord CreateSourceActorHeal(
        long timestampMilliseconds,
        uint globalSequence,
        uint actionId,
        ushort damage,
        ushort healing) =>
        new()
        {
            TimestampMilliseconds = timestampMilliseconds,
            GlobalSequence = globalSequence,
            ActionId = actionId,
            ActionType = 1,
            SourceObjectId = 100,
            SourceStableActorId = 1,
            Targets =
            [
                new ActionEffectTargetRecord
                {
                    TargetObjectId = 400,
                    TargetStableActorId = 4,
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
                            Value = damage,
                            Amount = damage,
                            IsCritical = false,
                            IsDirectHit = false,
                        },
                        new ActionEffectEntryRecord
                        {
                            Index = 1,
                            Kind = ActionEffectKind.Heal,
                            RawType = ActionEffectDecoder.HealType,
                            Param0 = ActionEffectDecoder.SourceActorHealFlag,
                            Param1 = 0,
                            Param2 = 0,
                            Param3 = 0,
                            Param4 = 0,
                            Value = healing,
                            Amount = healing,
                            IsCritical = false,
                            IsDirectHit = false,
                        },
                    ],
                },
            ],
        };
    private static ActorStateSample CreateSample(int stableActorId, uint maxHp) =>
        new()
        {
            StableActorId = stableActorId,
            X = 0,
            Y = 0,
            Z = 0,
            Rotation = 0,
            CurrentHp = maxHp,
            MaxHp = maxHp,
            IsDead = false,
            IsTargetable = true,
        };
}
