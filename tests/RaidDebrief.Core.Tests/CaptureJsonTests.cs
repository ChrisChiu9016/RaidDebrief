using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class CaptureJsonTests
{
    [Fact]
    public void RoundTripPreservesSchemaActorsFramesAndCadence()
    {
        var source = CreateRecord([0, 100, 200]);

        var json = CaptureJson.Serialize(source);
        var loaded = CaptureJson.Deserialize(json);

        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Equal(CaptureSchema.CurrentVersion, loaded.SchemaVersion);
        Assert.Equal(CaptureFeatures.Current, loaded.Features);
        Assert.Single(loaded.Actors);
        Assert.Equal(3, loaded.Frames.Length);
        Assert.Equal(100, PullRecordMetrics.AverageSampleIntervalMilliseconds(loaded));
        Assert.Equal(source.Actors[0], loaded.Actors[0]);
        Assert.Equal(source.Frames[2].Actors[0], loaded.Frames[2].Actors[0]);
        Assert.True(loaded.Frames[2].Actors[0].IsOmnidirectional);
    }
    [Fact]
    public void RoundTripPreservesRecordedActionNames()
    {
        var source = CreateRecord([0]) with
        {
            Features = CaptureFeatures.Current | CaptureFeatures.ActionNameSnapshot,
            ActionNames =
            [
                new RecordedActionName
                {
                    ActionId = 49_890,
                    Name = "Recorded Boss Cast",
                    Language = "English",
                    Source = ActionNameSource.RuntimeRsv,
                },
            ],
        };

        var loaded = CaptureJson.Deserialize(CaptureJson.Serialize(source));

        var actionName = Assert.Single(loaded.ActionNames);
        Assert.Equal(49_890u, actionName.ActionId);
        Assert.Equal("Recorded Boss Cast", actionName.Name);
        Assert.Equal("English", actionName.Language);
        Assert.Equal(ActionNameSource.RuntimeRsv, actionName.Source);
    }

    [Fact]
    public void RoundTripPreservesBarrierPercentageAndLegacyJsonRemainsUnknown()
    {
        var source = CreateRecord([0]) with
        {
            Features = CaptureFeatures.Current | CaptureFeatures.BarrierState,
            Frames =
            [
                CreateRecord([0]).Frames[0] with
                {
                    Actors =
                    [
                        CreateRecord([0]).Frames[0].Actors[0] with
                        {
                            BarrierPercentage = 17,
                        },
                    ],
                },
            ],
        };

        var loaded = CaptureJson.Deserialize(CaptureJson.Serialize(source));
        Assert.True((loaded.Features & CaptureFeatures.BarrierState) != 0);
        Assert.Equal((byte)17, loaded.Frames[0].Actors[0].BarrierPercentage);

        var legacy = CaptureJson.Deserialize(CaptureJson.Serialize(CreateRecord([0])));
        Assert.True((legacy.Features & CaptureFeatures.BarrierState) == 0);
        Assert.Equal((byte)0, legacy.Frames[0].Actors[0].BarrierPercentage);
    }

    [Fact]
    public void LegacyJsonWithoutActionNamesLoadsAsEmpty()
    {
        var json = CaptureJson.Serialize(CreateRecord([0]));
        json = json.Replace(
            "  \"actionNames\": [],\n",
            string.Empty,
            StringComparison.Ordinal);

        var loaded = CaptureJson.Deserialize(json);

        Assert.Empty(loaded.ActionNames);
        Assert.Equal(CaptureFeatures.Current, loaded.Features);
    }

    [Fact]
    public void RejectsUnresolvedRecordedActionName()
    {
        var record = CreateRecord([0]) with
        {
            Features = CaptureFeatures.Current | CaptureFeatures.ActionNameSnapshot,
            ActionNames =
            [
                new RecordedActionName
                {
                    ActionId = 49_890,
                    Name = "_rsv_49890_-1_1_0_0",
                    Language = "English",
                    Source = ActionNameSource.RuntimeRsv,
                },
            ],
        };

        var exception = Assert.Throws<InvalidDataException>(
            () => PullRecordValidator.Validate(record));

        Assert.Contains("unresolved", exception.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void LegacyAllWithoutMarkerFramesDoesNotClaimTargetMarkerCapture()
    {
        var legacy = CreateRecord([0]) with
        {
            Features = CaptureFeatures.All,
        };

        var loaded = CaptureJson.Deserialize(CaptureJson.Serialize(legacy));

        Assert.Equal(
            CaptureFeatures.ActorOwnerId | CaptureFeatures.HitboxRadius,
            loaded.Features);
    }

    [Fact]
    public void AtomicSaveCanBeLoadedOffline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RaidDebrief.Core.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "offline-capture.json");

        try
        {
            CaptureJson.Save(path, CreateRecord([0, 101, 201]));
            var loaded = CaptureJson.Load(path);

            Assert.Equal(3, loaded.Frames.Length);
            Assert.Equal(100.5, PullRecordMetrics.AverageSampleIntervalMilliseconds(loaded));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void RejectsTimestampThatMovesBackward()
    {
        var record = CreateRecord([100, 99]);

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(record));

        Assert.Contains("strictly increasing", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void RejectsNonFinitePosition(float position)
    {
        var record = CreateRecord([0]);
        record.Frames[0].Actors[0] = record.Frames[0].Actors[0] with { X = position };

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(record));

        Assert.Contains("finite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsBarrierPercentageAboveOneHundred()
    {
        // Stacked shields routinely exceed maximum health. Rejecting them dropped the whole
        // actor sample, which left holes in the replay for the shielded party member.
        var record = CreateRecord([0]);
        record.Frames[0].Actors[0] = record.Frames[0].Actors[0] with
        {
            BarrierPercentage = 180,
        };

        PullRecordValidator.Validate(record);

        Assert.Equal(180, record.Frames[0].Actors[0].BarrierPercentage);
    }

    [Fact]
    public void RejectsCurrentHpAboveMaximumHp()
    {
        var record = CreateRecord([0]);
        record.Frames[0].Actors[0] = record.Frames[0].Actors[0] with { CurrentHp = 101, MaxHp = 100 };

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(record));

        Assert.Contains("exceeds", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void RejectsInvalidHitboxRadius(float hitboxRadius)
    {
        var record = CreateRecord([0]);
        record.Frames[0].Actors[0] = record.Frames[0].Actors[0] with { HitboxRadius = hitboxRadius };

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(record));

        Assert.Contains("hitbox radius", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateActorRegistrationForOneGameObject()
    {
        var source = CreateRecord([0]);
        var record = source with
        {
            Actors =
            [
                source.Actors[0],
                source.Actors[0] with { StableActorId = 2 },
            ],
        };

        var exception = Assert.Throws<InvalidDataException>(() => PullRecordValidator.Validate(record));

        Assert.Contains("game object ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreAssemblyDoesNotReferenceDalamud()
    {
        var referencedAssemblies = typeof(PullRecord).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, assembly => assembly.Name?.Contains("Dalamud", StringComparison.Ordinal) == true);
    }

    private static PullRecord CreateRecord(long[] timestamps)
    {
        var actor = new ActorRecord
        {
            StableActorId = 1,
            Name = "Synthetic Actor",
            ObjectKind = "Player",
            EntityId = 0x10000001,
            GameObjectId = 0xE0000001,
            BaseId = 123,
            ClassJobId = 21,
            Level = 100,
        };

        return new PullRecord
        {
            Features = CaptureFeatures.Current,
            CaptureId = Guid.Parse("bc672017-ef2a-4f29-bb42-2e815f94b62e"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:30Z"),
            TerritoryType = 1234,
            MapId = 5678,
            Instance = 1,
            Actors = [actor],
            Frames = timestamps.Select(timestamp => new PositionFrame
            {
                TimestampMilliseconds = timestamp,
                Actors =
                [
                    new ActorStateSample
                    {
                        StableActorId = actor.StableActorId,
                        X = 1.25f,
                        Y = 2.5f,
                        Z = 3.75f,
                        Rotation = 0.5f,
                        HitboxRadius = 4.25f,
                        CurrentHp = 90,
                        MaxHp = 100,
                        IsDead = false,
                        IsTargetable = true,
                        IsOmnidirectional = true,
                    },
                ],
            }).ToArray(),
        };
    }
}
