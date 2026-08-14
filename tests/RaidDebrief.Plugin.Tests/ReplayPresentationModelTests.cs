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
