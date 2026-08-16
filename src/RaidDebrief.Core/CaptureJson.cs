using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidDebrief.Core;

public static class CaptureJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(PullRecord record)
    {
        PullRecordValidator.Validate(record);
        return JsonSerializer.Serialize(record, SerializerOptions);
    }

    public static PullRecord Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        PullRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PullRecord>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Capture JSON is invalid.", exception);
        }

        return CompleteDeserialization(record);
    }

    private static PullRecord CompleteDeserialization(PullRecord? record)
    {
        if (record is null)
        {
            throw new InvalidDataException("Capture JSON did not contain a pull record.");
        }
        record = UpgradeLegacyTargetMarkerOrder(record);
        PullRecordValidator.Validate(record);
        return record;
    }

    private static PullRecord UpgradeLegacyTargetMarkerOrder(PullRecord record)
    {
        if ((record.Features & CaptureFeatures.TargetMarkers) == 0
            || (record.Features & CaptureFeatures.TargetMarkerCanonicalOrder) != 0
            || record.TargetMarkerFrames is null)
        {
            return record;
        }

        if (record.TargetMarkerFrames.Length == 0)
        {
            return record with
            {
                Features = record.Features & ~CaptureFeatures.TargetMarkers,
            };
        }

        var upgradedFrames = new TargetMarkerFrame[record.TargetMarkerFrames.Length];
        for (var frameIndex = 0; frameIndex < record.TargetMarkerFrames.Length; frameIndex++)
        {
            var frame = record.TargetMarkerFrames[frameIndex];
            if (frame?.Markers is not { Length: TargetMarkerTimelineBuilder.MarkerCount } markers)
            {
                return record;
            }

            var upgradedMarkers = new TargetMarkerState[markers.Length];
            foreach (var marker in markers)
            {
                if (marker is null
                    || (int)marker.Id < 0
                    || (int)marker.Id >= TargetMarkerTimelineBuilder.MarkerCount)
                {
                    return record;
                }

                var markerId = TargetMarkerNativeSlotOrder.GetMarkerId((int)marker.Id);
                upgradedMarkers[(int)markerId] = marker with { Id = markerId };
            }

            upgradedFrames[frameIndex] = frame with { Markers = upgradedMarkers };
        }

        return record with
        {
            Features = record.Features | CaptureFeatures.TargetMarkerCanonicalOrder,
            TargetMarkerFrames = upgradedFrames,
        };
    }

    public static void Save(string path, PullRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(record);
        if (!path.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Compressed Capture paths must end with .json.gz.",
                nameof(path));
        }

        PullRecordValidator.Validate(record);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Capture path has no parent directory.");
        var temporaryPath = fullPath + ".tmp";

        Directory.CreateDirectory(directory);
        try
        {
            using (var output = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            using (var compressed = new GZipStream(
                output,
                CompressionLevel.Optimal,
                leaveOpen: false))
            {
                JsonSerializer.Serialize(compressed, record, SerializerOptions);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static PullRecord Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (path.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.OpenRead(fullPath);
            using var compressed = new GZipStream(input, CompressionMode.Decompress);
            return Deserialize(compressed, compressed: true);
        }

        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Capture paths must end with .json or .json.gz.",
                nameof(path));
        }

        using var json = File.OpenRead(fullPath);
        return Deserialize(json, compressed: false);
    }

    private static PullRecord Deserialize(Stream json, bool compressed)
    {
        PullRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PullRecord>(json, SerializerOptions);
            if (compressed)
            {
                json.CopyTo(Stream.Null);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Capture JSON is invalid.", exception);
        }
        catch (InvalidDataException exception) when (compressed)
        {
            throw new InvalidDataException("Capture GZip data is invalid.", exception);
        }

        return CompleteDeserialization(record);
    }
}

public static class PullRecordMetrics
{
    public static double AverageSampleIntervalMilliseconds(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Frames.Length < 2)
        {
            return 0;
        }

        return (double)(record.Frames[^1].TimestampMilliseconds - record.Frames[0].TimestampMilliseconds)
            / (record.Frames.Length - 1);
    }
}
