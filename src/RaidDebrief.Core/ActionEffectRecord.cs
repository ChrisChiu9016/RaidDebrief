namespace RaidDebrief.Core;

public enum ActionEffectKind
{
    Damage,
    Heal,
    Miss,
    Other,
}

public sealed record ActionEffectRecord
{
    public required long TimestampMilliseconds { get; init; }

    public required uint GlobalSequence { get; init; }

    public required uint ActionId { get; init; }

    public required byte ActionType { get; init; }

    public required ulong SourceObjectId { get; init; }

    public int? SourceStableActorId { get; init; }

    public ulong? AnimationTargetObjectId { get; init; }
    /// <summary>
    /// Native callback target slots in their observed order. Multiple slots may reference the same object ID.
    /// </summary>
    public required ActionEffectTargetRecord[] Targets { get; init; }
}

public sealed record ActionEffectTargetRecord
{
    public required ulong TargetObjectId { get; init; }

    public int? TargetStableActorId { get; init; }

    public required ActionEffectEntryRecord[] Entries { get; init; }
}

public sealed record ActionEffectEntryRecord
{
    public required byte Index { get; init; }

    public required ActionEffectKind Kind { get; init; }

    public required byte RawType { get; init; }

    public required byte Param0 { get; init; }

    public required byte Param1 { get; init; }

    public required byte Param2 { get; init; }

    public required byte Param3 { get; init; }

    public required byte Param4 { get; init; }

    public required ushort Value { get; init; }

    public uint? Amount { get; init; }

    public required bool IsCritical { get; init; }

    public required bool IsDirectHit { get; init; }
}

public static class ActionEffectDecoder
{
    public const byte NothingType = 0;
    public const byte MissType = 1;
    public const byte DamageType = 3;
    public const byte HealType = 4;
    public const byte BlockedDamageType = 5;
    public const byte ParriedDamageType = 6;
    public const byte SourceActorHealFlag = 0x01;

    public static ActionEffectKind Classify(byte rawType) => rawType switch
    {
        DamageType or BlockedDamageType or ParriedDamageType => ActionEffectKind.Damage,
        HealType => ActionEffectKind.Heal,
        MissType => ActionEffectKind.Miss,
        _ => ActionEffectKind.Other,
    };

    public static uint? DecodeAmount(byte rawType, byte param3, byte param4, ushort value)
    {
        var kind = Classify(rawType);
        if (kind is not (ActionEffectKind.Damage or ActionEffectKind.Heal))
        {
            return null;
        }

        return value + ((param4 & 0x40) != 0 ? (uint)param3 << 16 : 0);
    }

    public static bool DecodeCritical(byte rawType, byte param0, byte param1) => Classify(rawType) switch
    {
        ActionEffectKind.Damage => (param0 & 0x20) != 0,
        ActionEffectKind.Heal => (param1 & 0x20) != 0,
        _ => false,
    };

    public static bool DecodeDirectHit(byte rawType, byte param0) =>
        Classify(rawType) == ActionEffectKind.Damage && (param0 & 0x40) != 0;

    public static bool IsSourceActorHeal(byte rawType, byte param0) =>
        rawType == HealType && (param0 & SourceActorHealFlag) != 0;

    public static int? ResolveTargetStableActorId(
        ActionEffectRecord actionEffect,
        ActionEffectTargetRecord target,
        ActionEffectEntryRecord entry) =>
        IsSourceActorHeal(entry.RawType, entry.Param0)
            ? actionEffect.SourceStableActorId
            : target.TargetStableActorId;
}
