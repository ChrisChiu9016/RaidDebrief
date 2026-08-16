using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ActionEffectRecordTests
{
    [Fact]
    public void DecoderDistinguishesKindsAndDecodesLargeAmountsAndFlags()
    {
        Assert.Equal(ActionEffectKind.Damage, ActionEffectDecoder.Classify(3));
        Assert.Equal(ActionEffectKind.Damage, ActionEffectDecoder.Classify(5));
        Assert.Equal(ActionEffectKind.Heal, ActionEffectDecoder.Classify(4));
        Assert.Equal(ActionEffectKind.Miss, ActionEffectDecoder.Classify(1));
        Assert.Equal(ActionEffectKind.Other, ActionEffectDecoder.Classify(14));

        Assert.Equal(0x1_1234u, ActionEffectDecoder.DecodeAmount(3, 1, 0x40, 0x1234));
        Assert.Null(ActionEffectDecoder.DecodeAmount(1, 0, 0, 0));
        Assert.True(ActionEffectDecoder.DecodeCritical(3, 0x20, 0));
        Assert.True(ActionEffectDecoder.DecodeCritical(4, 0, 0x20));
        Assert.True(ActionEffectDecoder.DecodeDirectHit(3, 0x40));
        Assert.False(ActionEffectDecoder.DecodeDirectHit(4, 0x40));
    }

    [Fact]
    public void DecoderRedirectsSourceActorHealsFromPacketTargetToCaster()
    {
        var actionEffect = CreateRecord().ActionEffects[0];
        var packetTarget = actionEffect.Targets[1];
        var sourceActorHeal = packetTarget.Entries[0] with
        {
            Param0 = ActionEffectDecoder.SourceActorHealFlag,
        };

        Assert.True(ActionEffectDecoder.IsSourceActorHeal(
            sourceActorHeal.RawType,
            sourceActorHeal.Param0));
        Assert.Equal(
            actionEffect.SourceStableActorId,
            ActionEffectDecoder.ResolveTargetStableActorId(
                actionEffect,
                packetTarget,
                sourceActorHeal));
        Assert.Equal(
            packetTarget.TargetStableActorId,
            ActionEffectDecoder.ResolveTargetStableActorId(
                actionEffect,
                packetTarget,
                packetTarget.Entries[0]));
        Assert.False(ActionEffectDecoder.IsSourceActorHeal(
            ActionEffectDecoder.DamageType,
            ActionEffectDecoder.SourceActorHealFlag));
    }

    [Fact]
    public void RoundTripPreservesMultiTargetAndMultiEntryActionEffects()
    {
        var source = CreateRecord();

        var json = CaptureJson.Serialize(source);
        var loaded = CaptureJson.Deserialize(json);

        Assert.Contains("\"kind\":\"damage\"", json, StringComparison.Ordinal);
        Assert.Single(loaded.ActionEffects);
        Assert.Equal(2, loaded.ActionEffects[0].Targets.Length);
        Assert.Equal(2, loaded.ActionEffects[0].Targets[0].Entries.Length);
        Assert.Equal(0x1_1234u, loaded.ActionEffects[0].Targets[0].Entries[0].Amount);
        Assert.Equal(ActionEffectKind.Miss, loaded.ActionEffects[0].Targets[0].Entries[1].Kind);
        Assert.Equal(ActionEffectKind.Heal, loaded.ActionEffects[0].Targets[1].Entries[0].Kind);
        Assert.Equal(3, loaded.ActionEffects[0].Targets[1].TargetStableActorId);
    }

    [Fact]
    public void ValidatorAllowsRepeatedNativeTargetSlots()
    {
        var source = CreateRecord();
        var actionEffect = source.ActionEffects[0];
        source.ActionEffects[0] = actionEffect with
        {
            Targets =
            [
                actionEffect.Targets[0],
                actionEffect.Targets[1] with
                {
                    TargetObjectId = actionEffect.Targets[0].TargetObjectId,
                    TargetStableActorId = actionEffect.Targets[0].TargetStableActorId,
                },
            ],
        };

        PullRecordValidator.Validate(source);

        Assert.Equal(
            source.ActionEffects[0].Targets[0].TargetObjectId,
            source.ActionEffects[0].Targets[1].TargetObjectId);
        Assert.Equal(ActionEffectKind.Damage, source.ActionEffects[0].Targets[0].Entries[0].Kind);
        Assert.Equal(ActionEffectKind.Heal, source.ActionEffects[0].Targets[1].Entries[0].Kind);
    }

    [Fact]
    public void ValidatorRejectsDecodedFieldsThatDoNotMatchRawEntry()
    {
        var source = CreateRecord();
        source.ActionEffects[0].Targets[0].Entries[0] =
            source.ActionEffects[0].Targets[0].Entries[0] with { Amount = 1 };

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(source));

        Assert.Contains("decoded raw fields", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PullRecord CreateRecord()
    {
        var damage = CreateEntry(0, 3, 0x60, 0, 0, 1, 0x40, 0x1234);
        var miss = CreateEntry(1, 1, 0, 0, 0, 0, 0, 0);
        var heal = CreateEntry(0, 4, 0, 0x20, 0, 0, 0, 1500);

        return new PullRecord
        {
            CaptureId = Guid.Parse("0db0125a-11e5-4cca-ad7d-1bd13711104f"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:01Z"),
            TerritoryType = 1,
            MapId = 2,
            Instance = 3,
            Actors =
            [
                CreateActor(1, 0x1000),
                CreateActor(2, 0x2000),
                CreateActor(3, 0x3000),
            ],
            Frames = [],
            ActionEffects =
            [
                new ActionEffectRecord
                {
                    TimestampMilliseconds = 250,
                    GlobalSequence = 100,
                    ActionId = 42,
                    ActionType = 1,
                    SourceObjectId = 0x1000,
                    SourceStableActorId = 1,
                    AnimationTargetObjectId = 0x2000,
                    Targets =
                    [
                        new ActionEffectTargetRecord
                        {
                            TargetObjectId = 0x2000,
                            TargetStableActorId = 2,
                            Entries = [damage, miss],
                        },
                        new ActionEffectTargetRecord
                        {
                            TargetObjectId = 0x3000,
                            TargetStableActorId = 3,
                            Entries = [heal],
                        },
                    ],
                },
            ],
        };
    }

    private static ActionEffectEntryRecord CreateEntry(
        byte index,
        byte rawType,
        byte param0,
        byte param1,
        byte param2,
        byte param3,
        byte param4,
        ushort value) => new()
        {
            Index = index,
            Kind = ActionEffectDecoder.Classify(rawType),
            RawType = rawType,
            Param0 = param0,
            Param1 = param1,
            Param2 = param2,
            Param3 = param3,
            Param4 = param4,
            Value = value,
            Amount = ActionEffectDecoder.DecodeAmount(rawType, param3, param4, value),
            IsCritical = ActionEffectDecoder.DecodeCritical(rawType, param0, param1),
            IsDirectHit = ActionEffectDecoder.DecodeDirectHit(rawType, param0),
        };

    private static ActorRecord CreateActor(int stableActorId, ulong gameObjectId) => new()
    {
        StableActorId = stableActorId,
        Name = $"Actor {stableActorId}",
        ObjectKind = "Player",
        EntityId = (uint)gameObjectId,
        GameObjectId = gameObjectId,
        BaseId = 0,
        ClassJobId = 0,
        Level = 100,
    };
}
