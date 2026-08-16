using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin.Services;
using RaidDebrief.Core;
using Serilog;
using Serilog.Events;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class PullHistoryStoreTests
{
    [Fact]
    public void PersistsValidatedAutomaticPullAndIndexesDutyGroup()
    {
        var directory = CreateDirectory();
        try
        {
            var record = CreateRecord(Guid.Parse("3f68c986-fb57-4482-b081-d9e3cd54c32e"));
            var store = new PullHistoryStore(directory, new FakePluginLog());

            Assert.True(store.TryEnqueue(record));
            store.Dispose();

            var entry = Assert.Single(store.Entries);
            Assert.Equal(record.CaptureId, entry.CaptureId);
            Assert.Equal(record.DutyRun!.DutyRunId, entry.DutyRunId);
            Assert.Equal("AAC Heavyweight M4 · 2026-08-16 15:43:26", entry.DutyRunName);
            Assert.Equal(1, entry.PullOrdinalWithinDutyRun);
            Assert.Equal(25f, entry.FinalBossHpPercentage);
            Assert.True(entry.CompressedBytes > 0);
            Assert.True(File.Exists(Path.Combine(store.HistoryDirectory, entry.RelativeFilePath)));
            Assert.True(File.Exists(store.IndexPath));

            using var index = JsonDocument.Parse(File.ReadAllText(store.IndexPath));
            var indexedPull = Assert.Single(index.RootElement.GetProperty("pulls").EnumerateArray());
            Assert.Equal(record.CaptureId, indexedPull.GetProperty("captureId").GetGuid());
            Assert.Equal(entry.DutyRunName, indexedPull.GetProperty("dutyRunName").GetString());

            var loaded = CaptureJson.Load(
                Path.Combine(store.HistoryDirectory, entry.RelativeFilePath));
            Assert.Equal(record.DutyRun, loaded.DutyRun);
            Assert.Equal(CaptureMode.AutomaticPull, loaded.CaptureMode);
            Assert.Equal(PullEndReason.DutyWiped, loaded.EndReason);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void IgnoresDeveloperManualCapture()
    {
        var directory = CreateDirectory();
        try
        {
            var manual = CreateRecord(Guid.NewGuid()) with
            {
                Features = CaptureFeatures.Current,
                CaptureMode = CaptureMode.ManualDeveloper,
                DutyRun = null,
                EndReason = null,
            };
            var store = new PullHistoryStore(directory, new FakePluginLog());

            Assert.False(store.TryEnqueue(manual));
            store.Dispose();

            Assert.Empty(store.Entries);
            Assert.False(File.Exists(store.IndexPath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RebuildsIndexFromOrphanedAutomaticPullFile()
    {
        var directory = CreateDirectory();
        try
        {
            var record = CreateRecord(Guid.Parse("d2ef83a1-dbb8-4f89-b5dc-71d59c19912c"));
            var capturePath = Path.Combine(
                directory,
                "history",
                "2026-08-16",
                $"{record.CaptureId:D}.json.gz");
            CaptureJson.Save(capturePath, record);

            var store = new PullHistoryStore(directory, new FakePluginLog());
            store.Dispose();

            var recovered = Assert.Single(store.Entries);
            Assert.Equal(record.CaptureId, recovered.CaptureId);
            Assert.Equal(record.DutyRun!.DutyRunId, recovered.DutyRunId);
            Assert.True(File.Exists(store.IndexPath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RecoversMovedCaptureAfterDroppingMissingIndexEntry()
    {
        var directory = CreateDirectory();
        try
        {
            var record = CreateRecord(Guid.Parse("adbc0fc9-f253-48d0-a4a6-02d74e2f90b5"));
            var firstStore = new PullHistoryStore(directory, new FakePluginLog());
            Assert.True(firstStore.TryEnqueue(record));
            firstStore.Dispose();

            var indexedEntry = Assert.Single(firstStore.Entries);
            var indexedPath = Path.Combine(firstStore.HistoryDirectory, indexedEntry.RelativeFilePath);
            var movedDirectory = Path.Combine(firstStore.HistoryDirectory, "recovered");
            Directory.CreateDirectory(movedDirectory);
            var movedPath = Path.Combine(movedDirectory, Path.GetFileName(indexedPath));
            File.Move(indexedPath, movedPath);

            var recoveredStore = new PullHistoryStore(directory, new FakePluginLog());
            recoveredStore.Dispose();

            var recoveredEntry = Assert.Single(recoveredStore.Entries);
            Assert.Equal(record.CaptureId, recoveredEntry.CaptureId);
            Assert.Equal(
                "recovered/" + Path.GetFileName(movedPath),
                recoveredEntry.RelativeFilePath);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SnapshotGroupsNewestDutyRunsAndPullsAndLoadsSelection()
    {
        var directory = CreateDirectory();
        try
        {
            var first = CreateRecord(Guid.Parse("ad59fb39-37c8-4033-b7b1-228ef8c93881"));
            var second = CreateRecord(Guid.Parse("e5693a20-faf8-4b63-b908-69354e498f1a")) with
            {
                StartedAtUtc = first.StartedAtUtc.AddMinutes(2),
                EndedAtUtc = first.EndedAtUtc.AddMinutes(2),
                DutyRun = first.DutyRun! with { PullOrdinalWithinDutyRun = 2 },
            };
            var newerRunEnteredAtUtc = first.DutyRun!.DutyEnteredAtUtc.AddHours(1);
            var newerRun = CreateRecord(Guid.Parse("7c1b01b7-e856-4c84-bf96-19cc535d83be")) with
            {
                StartedAtUtc = newerRunEnteredAtUtc.AddMinutes(1),
                EndedAtUtc = newerRunEnteredAtUtc.AddMinutes(2),
                DutyRun = first.DutyRun with
                {
                    DutyRunId = Guid.Parse("45235e11-4832-4475-a261-2a1903139068"),
                    DutyRunName = "AAC Heavyweight M4 · 2026-08-16 16:43:26",
                    DutyEnteredAtUtc = newerRunEnteredAtUtc,
                },
            };
            var store = new PullHistoryStore(directory, new FakePluginLog());
            Assert.True(store.TryEnqueue(second));
            Assert.True(store.TryEnqueue(newerRun));
            Assert.True(store.TryEnqueue(first));
            store.Dispose();

            var snapshot = store.GetSnapshot();
            Assert.True(snapshot.IsReady);
            Assert.Equal(3, snapshot.PullCount);
            Assert.Equal(2, snapshot.Groups.Length);
            Assert.Equal(newerRun.DutyRun!.DutyRunId, snapshot.Groups[0].DutyRunId);
            Assert.Equal(
                [second.CaptureId, first.CaptureId],
                snapshot.Groups[1].Pulls.Select(entry => entry.CaptureId));

            var loaded = store.Load(second.CaptureId);
            Assert.Equal(second.CaptureId, loaded.CaptureId);
            Assert.Equal(2, loaded.DutyRun!.PullOrdinalWithinDutyRun);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void MigratesVersionOneIndexAndBackfillsFinalBossHp()
    {
        var directory = CreateDirectory();
        try
        {
            var record = CreateRecord(Guid.Parse("7055426d-df6a-4450-bf87-fc073292dc41"));
            var firstStore = new PullHistoryStore(directory, new FakePluginLog());
            Assert.True(firstStore.TryEnqueue(record));
            firstStore.Dispose();

            var root = JsonNode.Parse(File.ReadAllText(firstStore.IndexPath))!.AsObject();
            root["schemaVersion"] = 1;
            root["pulls"]!.AsArray()[0]!.AsObject().Remove("finalBossHpPercentage");
            File.WriteAllText(firstStore.IndexPath, root.ToJsonString());

            var migratedStore = new PullHistoryStore(directory, new FakePluginLog());
            migratedStore.Dispose();

            var migratedEntry = Assert.Single(migratedStore.Entries);
            Assert.Equal(25f, migratedEntry.FinalBossHpPercentage);
            using var migratedIndex = JsonDocument.Parse(File.ReadAllText(migratedStore.IndexPath));
            Assert.Equal(
                PullHistoryIndex.CurrentSchemaVersion,
                migratedIndex.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                25f,
                Assert.Single(migratedIndex.RootElement.GetProperty("pulls").EnumerateArray())
                    .GetProperty("finalBossHpPercentage")
                    .GetSingle());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static PullRecord CreateRecord(Guid captureId)
    {
        var enteredAtUtc = DateTimeOffset.Parse("2026-08-16T07:43:26Z");
        var actor = new ActorRecord
        {
            StableActorId = 1,
            Name = "Player 1",
            ObjectKind = "Pc",
            EntityId = 0x1000_0001,
            GameObjectId = 0x1000_0001,
            BaseId = 0,
            ClassJobId = 35,
            Level = 100,
        };
        var boss = new ActorRecord
        {
            StableActorId = 2,
            Name = "Raid Boss",
            ObjectKind = "BattleNpc",
            EntityId = 0x4000_0001,
            GameObjectId = 0x4000_0001,
            BaseId = 1,
            ClassJobId = 0,
            Level = 100,
        };

        return new PullRecord
        {
            Features = CaptureFeatures.Current | CaptureFeatures.DutyRunIdentity,
            CaptureId = captureId,
            StartedAtUtc = enteredAtUtc.AddMinutes(1),
            EndedAtUtc = enteredAtUtc.AddMinutes(2),
            TerritoryType = 1_229,
            MapId = 926,
            Instance = 0,
            CaptureMode = CaptureMode.AutomaticPull,
            DutyRun = new DutyPullIdentity
            {
                DutyRunId = Guid.Parse("02329be0-b841-44b5-b53a-20014141f3cc"),
                ContentFinderConditionId = 1_003,
                DutyName = "AAC Heavyweight M4",
                DutyRunName = "AAC Heavyweight M4 · 2026-08-16 15:43:26",
                DutyEnteredAtUtc = enteredAtUtc,
                PullOrdinalWithinDutyRun = 1,
            },
            EndReason = PullEndReason.DutyWiped,
            Actors = [actor, boss],
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
                            X = 100,
                            Y = 0,
                            Z = 100,
                            Rotation = 0,
                            HitboxRadius = 0.5f,
                            CurrentHp = 100,
                            MaxHp = 100,
                            IsDead = false,
                            IsTargetable = true,
                        },
                        new ActorStateSample
                        {
                            StableActorId = 2,
                            X = 100,
                            Y = 0,
                            Z = 90,
                            Rotation = 0,
                            HitboxRadius = 5,
                            CurrentHp = 250,
                            MaxHp = 1_000,
                            IsDead = false,
                            IsTargetable = true,
                        },
                    ],
                },
            ],
        };
    }

    private static string CreateDirectory() =>
        Path.Combine(
            Path.GetTempPath(),
            "RaidDebrief.Plugin.Tests",
            Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakePluginLog : IPluginLog
    {
        public ILogger Logger => null!;
        public LogEventLevel MinimumLogLevel { get; set; }
        public void Fatal(string messageTemplate, params object[] values) { }
        public void Fatal(Exception? exception, string messageTemplate, params object[] values) { }
        public void Error(string messageTemplate, params object[] values) { }
        public void Error(Exception? exception, string messageTemplate, params object[] values) { }
        public void Warning(string messageTemplate, params object[] values) { }
        public void Warning(Exception? exception, string messageTemplate, params object[] values) { }
        public void Information(string messageTemplate, params object[] values) { }
        public void Information(Exception? exception, string messageTemplate, params object[] values) { }
        public void Info(string messageTemplate, params object[] values) { }
        public void Info(Exception? exception, string messageTemplate, params object[] values) { }
        public void Debug(string messageTemplate, params object[] values) { }
        public void Debug(Exception? exception, string messageTemplate, params object[] values) { }
        public void Verbose(string messageTemplate, params object[] values) { }
        public void Verbose(Exception? exception, string messageTemplate, params object[] values) { }
        public void Write(
            LogEventLevel level,
            Exception? exception,
            string messageTemplate,
            params object[] values)
        {
        }
    }
}
