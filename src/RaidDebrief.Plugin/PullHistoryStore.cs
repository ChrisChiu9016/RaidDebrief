using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using RaidDebrief.Core;

namespace RaidDebrief.Plugin;

internal sealed record PullHistoryEntry
{
    public required Guid CaptureId { get; init; }
    public required Guid DutyRunId { get; init; }
    public required uint ContentFinderConditionId { get; init; }
    public required string DutyName { get; init; }
    public required string DutyRunName { get; init; }
    public required DateTimeOffset DutyEnteredAtUtc { get; init; }
    public required int PullOrdinalWithinDutyRun { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset EndedAtUtc { get; init; }
    public required PullEndReason EndReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FinalBossHpPercentage { get; init; }
    public required string RelativeFilePath { get; init; }
    public required long CompressedBytes { get; init; }
    public required int CaptureSchemaVersion { get; init; }
}

internal sealed record PullHistoryIndex
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public PullHistoryEntry[] Pulls { get; init; } = [];
}

internal sealed record PullHistoryGroup
{
    public required Guid DutyRunId { get; init; }
    public required uint ContentFinderConditionId { get; init; }
    public required string DutyName { get; init; }
    public required string DutyRunName { get; init; }
    public required DateTimeOffset DutyEnteredAtUtc { get; init; }
    public required PullHistoryEntry[] Pulls { get; init; }
}

internal readonly record struct PullHistorySnapshot(
    long Generation,
    bool IsReady,
    PullHistoryGroup[] Groups,
    int PullCount);

internal sealed class PullHistoryStore : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object gate = new();
    private readonly string historyDirectory;
    private readonly string indexPath;
    private readonly IPluginLog log;
    private readonly BlockingCollection<PullRecord> queue = new(new ConcurrentQueue<PullRecord>());
    private readonly Task worker;
    private List<PullHistoryEntry> entries = [];
    private PullHistorySnapshot snapshot = new(0, false, [], 0);
    private long snapshotGeneration;
    private bool recoveryComplete;
    private bool disposed;

    public PullHistoryStore(string pluginConfigurationDirectory, IPluginLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigurationDirectory);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.historyDirectory = Path.Combine(
            Path.GetFullPath(pluginConfigurationDirectory),
            "history");
        this.indexPath = Path.Combine(this.historyDirectory, "history-index.json");
        this.worker = Task.Run(this.RunWorker);
    }

    public string HistoryDirectory => this.historyDirectory;
    public string IndexPath => this.indexPath;

    public PullHistoryEntry[] Entries
    {
        get
        {
            lock (this.gate)
            {
                return this.entries.ToArray();
            }
        }
    }

    public PullHistorySnapshot GetSnapshot()
    {
        lock (this.gate)
        {
            return this.snapshot;
        }
    }

    public PullRecord Load(Guid captureId)
    {
        PullHistoryEntry entry;
        lock (this.gate)
        {
            entry = this.entries.Find(item => item.CaptureId == captureId)
                ?? throw new FileNotFoundException(
                    $"History does not contain Capture {captureId}.");
        }

        if (!this.TryResolveHistoryPath(entry.RelativeFilePath, out var capturePath)
            || !File.Exists(capturePath))
        {
            throw new FileNotFoundException(
                $"History Capture {captureId} is missing.",
                capturePath);
        }

        var record = CaptureJson.Load(capturePath);
        if (record.CaptureId != captureId
            || record.CaptureMode != CaptureMode.AutomaticPull
            || record.DutyRun?.DutyRunId != entry.DutyRunId)
        {
            throw new InvalidDataException(
                $"History Capture {captureId} does not match its index entry.");
        }

        return record;
    }

    public bool TryEnqueue(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.CaptureMode != CaptureMode.AutomaticPull || record.DutyRun is null)
        {
            return false;
        }

        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.queue.Add(record);
            return true;
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.queue.CompleteAdding();
        }

        this.worker.GetAwaiter().GetResult();
        this.queue.Dispose();
    }

    private void RunWorker()
    {
        try
        {
            this.RecoverIndex();
        }
        catch (Exception exception)
        {
            this.log.Error(
                exception,
                "Raid Debrief History index recovery failed; new Pull archives will still be attempted.");
        }
        finally
        {
            lock (this.gate)
            {
                this.recoveryComplete = true;
                this.RefreshSnapshot();
            }
        }

        foreach (var record in this.queue.GetConsumingEnumerable())
        {
            try
            {
                this.Persist(record);
            }
            catch (Exception exception)
            {
                this.log.Error(
                    exception,
                    "Raid Debrief automatic Pull {CaptureId} History persistence failed.",
                    record.CaptureId);
            }
        }
    }

    private void Persist(PullRecord record)
    {
        var dutyRun = record.DutyRun
            ?? throw new InvalidOperationException("Automatic History Pull has no Duty Run identity.");
        var endReason = record.EndReason
            ?? throw new InvalidOperationException("Automatic History Pull has no end reason.");
        PullRecordValidator.Validate(record);

        var dateDirectory = record.StartedAtUtc.UtcDateTime.ToString(
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);
        var capturePath = Path.Combine(
            this.historyDirectory,
            dateDirectory,
            $"{record.CaptureId:D}.json.gz");
        CaptureJson.Save(capturePath, record);

        var relativePath = Path.GetRelativePath(this.historyDirectory, capturePath)
            .Replace('\\', '/');
        var entry = new PullHistoryEntry
        {
            CaptureId = record.CaptureId,
            DutyRunId = dutyRun.DutyRunId,
            ContentFinderConditionId = dutyRun.ContentFinderConditionId,
            DutyName = dutyRun.DutyName,
            DutyRunName = dutyRun.DutyRunName,
            DutyEnteredAtUtc = dutyRun.DutyEnteredAtUtc,
            PullOrdinalWithinDutyRun = dutyRun.PullOrdinalWithinDutyRun,
            StartedAtUtc = record.StartedAtUtc,
            EndedAtUtc = record.EndedAtUtc,
            EndReason = endReason,
            FinalBossHpPercentage = DebriefAnalyzer.ResolveFinalBossHp(record)?.Percentage,
            RelativeFilePath = relativePath,
            CompressedBytes = new FileInfo(capturePath).Length,
            CaptureSchemaVersion = record.SchemaVersion,
        };

        PullHistoryEntry[] snapshot;
        lock (this.gate)
        {
            var existingIndex = this.entries.FindIndex(item => item.CaptureId == entry.CaptureId);
            if (existingIndex >= 0)
            {
                this.entries[existingIndex] = entry;
            }
            else
            {
                this.entries.Add(entry);
            }

            this.SortEntries();
            this.RefreshSnapshot();
            snapshot = this.entries.ToArray();
        }

        this.SaveIndex(snapshot);
        this.log.Information(
            "Raid Debrief automatic Pull {CaptureId} archived in History group {DutyRunName} as Pull {PullOrdinal}.",
            record.CaptureId,
            dutyRun.DutyRunName,
            dutyRun.PullOrdinalWithinDutyRun);
    }

    private void RecoverIndex()
    {
        var changed = false;
        var loadedEntries = this.LoadIndex(ref changed, out var rebuildMetadata);
        if (rebuildMetadata)
        {
            loadedEntries = [];
        }

        var validEntries = new List<PullHistoryEntry>(loadedEntries.Length);
        var indexedCaptureIds = new HashSet<Guid>();
        foreach (var entry in loadedEntries)
        {
            if (!IsValidIndexEntry(entry))
            {
                changed = true;
                continue;
            }

            if (!this.TryResolveHistoryPath(entry.RelativeFilePath, out var capturePath)
                || !File.Exists(capturePath)
                || !indexedCaptureIds.Add(entry.CaptureId))
            {
                changed = true;
                continue;
            }

            validEntries.Add(entry);
        }

        if (Directory.Exists(this.historyDirectory))
        {
            foreach (var capturePath in Directory.EnumerateFiles(
                this.historyDirectory,
                "*.json.gz",
                SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(capturePath);
                if (fileName.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParse(fileName[..^8], out var fileCaptureId)
                    && indexedCaptureIds.Contains(fileCaptureId))
                {
                    continue;
                }

                PullRecord record;
                try
                {
                    record = CaptureJson.Load(capturePath);
                }
                catch (Exception exception)
                {
                    this.log.Warning(
                        exception,
                        "Raid Debrief ignored unreadable History capture {CapturePath} during recovery.",
                        capturePath);
                    continue;
                }

                if (indexedCaptureIds.Contains(record.CaptureId)
                    || record.CaptureMode != CaptureMode.AutomaticPull
                    || record.DutyRun is not { } dutyRun
                    || record.EndReason is not { } endReason)
                {
                    continue;
                }

                indexedCaptureIds.Add(record.CaptureId);
                validEntries.Add(this.CreateRecoveredEntry(
                    record,
                    dutyRun,
                    endReason,
                    capturePath));
                changed = true;
            }
        }

        lock (this.gate)
        {
            this.entries = validEntries;
            this.SortEntries();
            validEntries = this.entries.ToList();
        }

        if (changed && (validEntries.Count > 0 || File.Exists(this.indexPath)))
        {
            this.SaveIndex(validEntries.ToArray());
        }
    }

    private PullHistoryEntry[] LoadIndex(ref bool changed, out bool rebuildMetadata)
    {
        rebuildMetadata = false;
        if (!File.Exists(this.indexPath))
        {
            return [];
        }

        try
        {
            var index = JsonSerializer.Deserialize<PullHistoryIndex>(
                File.ReadAllText(this.indexPath, Encoding.UTF8),
                SerializerOptions)
                ?? throw new InvalidDataException("History index is empty.");
            if (index.SchemaVersion == 1)
            {
                changed = true;
                rebuildMetadata = true;
            }
            else if (index.SchemaVersion != PullHistoryIndex.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported History index schema {index.SchemaVersion}.");
            }

            return index.Pulls ?? [];
        }
        catch (Exception exception)
        {
            changed = true;
            var backupPath = this.indexPath + ".corrupt-" + DateTimeOffset.UtcNow.ToString(
                "yyyyMMddHHmmssfff",
                CultureInfo.InvariantCulture);
            File.Move(this.indexPath, backupPath, overwrite: true);
            this.log.Warning(
                exception,
                "Raid Debrief moved an unreadable History index to {BackupPath} and will rebuild it.",
                backupPath);
            return [];
        }
    }

    private PullHistoryEntry CreateRecoveredEntry(
        PullRecord record,
        DutyPullIdentity dutyRun,
        PullEndReason endReason,
        string capturePath) =>
        new()
        {
            CaptureId = record.CaptureId,
            DutyRunId = dutyRun.DutyRunId,
            ContentFinderConditionId = dutyRun.ContentFinderConditionId,
            DutyName = dutyRun.DutyName,
            DutyRunName = dutyRun.DutyRunName,
            DutyEnteredAtUtc = dutyRun.DutyEnteredAtUtc,
            PullOrdinalWithinDutyRun = dutyRun.PullOrdinalWithinDutyRun,
            StartedAtUtc = record.StartedAtUtc,
            EndedAtUtc = record.EndedAtUtc,
            EndReason = endReason,
            FinalBossHpPercentage = DebriefAnalyzer.ResolveFinalBossHp(record)?.Percentage,
            RelativeFilePath = Path.GetRelativePath(this.historyDirectory, capturePath)
                .Replace('\\', '/'),
            CompressedBytes = new FileInfo(capturePath).Length,
            CaptureSchemaVersion = record.SchemaVersion,
        };

    private bool TryResolveHistoryPath(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(this.historyDirectory, relativePath));
        var root = Path.GetFullPath(this.historyDirectory) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private void SaveIndex(PullHistoryEntry[] pulls)
    {
        Directory.CreateDirectory(this.historyDirectory);
        var temporaryPath = this.indexPath + ".tmp";
        try
        {
            var index = new PullHistoryIndex { Pulls = pulls };
            var json = JsonSerializer.Serialize(index, SerializerOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, this.indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void RefreshSnapshot()
    {
        var groups = this.entries
            .GroupBy(entry => entry.DutyRunId)
            .Select(group =>
            {
                var pulls = group
                    .OrderByDescending(entry => entry.PullOrdinalWithinDutyRun)
                    .ThenByDescending(entry => entry.StartedAtUtc)
                    .ThenBy(entry => entry.CaptureId)
                    .ToArray();
                var identity = pulls[0];
                return new PullHistoryGroup
                {
                    DutyRunId = group.Key,
                    ContentFinderConditionId = identity.ContentFinderConditionId,
                    DutyName = identity.DutyName,
                    DutyRunName = identity.DutyRunName,
                    DutyEnteredAtUtc = identity.DutyEnteredAtUtc,
                    Pulls = pulls,
                };
            })
            .OrderByDescending(group => group.DutyEnteredAtUtc)
            .ThenBy(group => group.DutyRunId)
            .ToArray();
        this.snapshot = new PullHistorySnapshot(
            ++this.snapshotGeneration,
            this.recoveryComplete,
            groups,
            this.entries.Count);
    }

    private static bool IsValidIndexEntry(PullHistoryEntry? entry) =>
        entry is not null
        && entry.CaptureId != Guid.Empty
        && entry.DutyRunId != Guid.Empty
        && entry.ContentFinderConditionId != 0
        && !string.IsNullOrWhiteSpace(entry.DutyName)
        && !string.IsNullOrWhiteSpace(entry.DutyRunName)
        && entry.PullOrdinalWithinDutyRun > 0
        && entry.EndedAtUtc >= entry.StartedAtUtc
        && entry.DutyEnteredAtUtc <= entry.StartedAtUtc
        && entry.CompressedBytes >= 0
        && (entry.FinalBossHpPercentage is not { } bossHp
            || bossHp is >= 0 and <= 100)
        && entry.CaptureSchemaVersion == CaptureSchema.CurrentVersion;

    private void SortEntries() =>
        this.entries.Sort(static (left, right) =>
        {
            var startedComparison = left.StartedAtUtc.CompareTo(right.StartedAtUtc);
            return startedComparison != 0
                ? startedComparison
                : left.CaptureId.CompareTo(right.CaptureId);
        });
}
