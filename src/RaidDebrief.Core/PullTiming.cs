namespace RaidDebrief.Core;

public static class PullTiming
{
    public static long CalculateDurationMilliseconds(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var durationMilliseconds = 0L;
        foreach (var frame in record.Frames)
        {
            durationMilliseconds = Math.Max(durationMilliseconds, frame.TimestampMilliseconds);
        }

        foreach (var observedEvent in record.Events)
        {
            durationMilliseconds = Math.Max(durationMilliseconds, observedEvent.TimestampMilliseconds);
        }

        foreach (var waymarkFrame in record.WaymarkFrames)
        {
            durationMilliseconds = Math.Max(durationMilliseconds, waymarkFrame.TimestampMilliseconds);
        }

        foreach (var actionEffect in record.ActionEffects)
        {
            durationMilliseconds = Math.Max(durationMilliseconds, actionEffect.TimestampMilliseconds);
        }

        foreach (var targetMarkerFrame in record.TargetMarkerFrames)
        {
            durationMilliseconds = Math.Max(
                durationMilliseconds,
                targetMarkerFrame.TimestampMilliseconds);
        }

        var wallClockDuration = record.EndedAtUtc - record.StartedAtUtc;
        if (wallClockDuration <= TimeSpan.Zero)
        {
            return durationMilliseconds;
        }

        var wallClockMilliseconds = wallClockDuration.TotalMilliseconds;
        if (wallClockMilliseconds >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return Math.Max(durationMilliseconds, (long)Math.Ceiling(wallClockMilliseconds));
    }
}
