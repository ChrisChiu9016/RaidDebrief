using System.Text.Json.Nodes;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ObservedEventPersistenceTests
{
    [Fact]
    public void RoundTripPreservesEventTypesSourcesAndFields()
    {
        var record = CreateRecord() with
        {
            Events =
            [
                new ObservedEvent
                {
                    TimestampMilliseconds = 100,
                    Type = ObservedEventType.CastStarted,
                    Source = ObservedEventSource.PolledCastState,
                    StableActorId = 1,
                    ActionId = 123,
                    RelatedObjectId = 0x200,
                },
                new ObservedEvent
                {
                    TimestampMilliseconds = 100,
                    Type = ObservedEventType.InCombatChanged,
                    Source = ObservedEventSource.PolledConditionState,
                    State = true,
                },
                new ObservedEvent
                {
                    TimestampMilliseconds = 200,
                    Type = ObservedEventType.DutyWiped,
                    Source = ObservedEventSource.DutyState,
                },
            ],
        };

        var json = CaptureJson.Serialize(record);
        var loaded = CaptureJson.Deserialize(json);

        Assert.Contains("\"type\":\"castStarted\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"dutyState\"", json, StringComparison.Ordinal);
        Assert.Equal(record.Events, loaded.Events);
    }

    [Fact]
    public void LoadsEarlySchemaVersionOneCaptureWithoutAdditiveFields()
    {
        var root = JsonNode.Parse(CaptureJson.Serialize(CreateRecord()))!.AsObject();
        Assert.True(root.Remove("events"));
        Assert.True(root.Remove("waymarkFrames"));
        Assert.True(root.Remove("actionEffects"));

        var loaded = CaptureJson.Deserialize(root.ToJsonString());

        Assert.Empty(loaded.Events);
        Assert.Empty(loaded.WaymarkFrames);
        Assert.Empty(loaded.ActionEffects);
    }

    [Fact]
    public void RejectsExactlyDuplicatedEvent()
    {
        var observedEvent = new ObservedEvent
        {
            TimestampMilliseconds = 100,
            Type = ObservedEventType.Death,
            Source = ObservedEventSource.PolledActorState,
            StableActorId = 1,
        };
        var record = CreateRecord() with { Events = [observedEvent, observedEvent] };

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(record));

        Assert.Contains("duplicated", exception.Message, StringComparison.Ordinal);
    }

    private static PullRecord CreateRecord() => new()
    {
        CaptureId = Guid.Parse("86bde346-f029-4263-ab43-cb7e09e3879f"),
        StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
        EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:01Z"),
        TerritoryType = 1,
        MapId = 2,
        Instance = 0,
        Actors =
        [
            new ActorRecord
            {
                StableActorId = 1,
                Name = "Synthetic Actor",
                ObjectKind = "Pc",
                EntityId = 0x100,
                GameObjectId = 0x100,
                BaseId = 0,
                ClassJobId = 1,
                Level = 100,
            },
        ],
        Frames =
        [
            new PositionFrame
            {
                TimestampMilliseconds = 0,
                Actors =
                [
                    new ActorStateSample
                    {
                        StableActorId = 1,
                        X = 0,
                        Y = 0,
                        Z = 0,
                        Rotation = 0,
                        CurrentHp = 100,
                        MaxHp = 100,
                        IsDead = false,
                        IsTargetable = true,
                    },
                ],
            },
        ],
    };
}
