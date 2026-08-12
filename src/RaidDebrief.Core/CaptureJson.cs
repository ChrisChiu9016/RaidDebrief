using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RaidDebrief.Core;

public static class CaptureJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
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

        PullRecord record;
        try
        {
            record = JsonSerializer.Deserialize<PullRecord>(json, SerializerOptions)
                ?? throw new InvalidDataException("Capture JSON did not contain a pull record.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Capture JSON is invalid.", exception);
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
        WriteAtomic(path, Serialize(record), overwrite: true);
    }


    internal static void WriteAtomic(string path, string content, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Capture path has no parent directory.");
        var temporaryPath = fullPath + ".tmp";

        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, overwrite);
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
        return Deserialize(File.ReadAllText(path, Encoding.UTF8));
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
