namespace RaidDebrief.Core;

public readonly record struct ReplayTimelineEntry(
    int OriginalRecordedIndex,
    ObservedEvent ObservedEvent)
{
    public long TimestampMilliseconds => this.ObservedEvent.TimestampMilliseconds;
}

public sealed class ReplayTimeline
{
    private readonly ReplayTimelineEntry[] entries;

    public ReplayTimeline(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        this.entries = new ReplayTimelineEntry[record.Events.Length];
        for (var index = 0; index < record.Events.Length; index++)
        {
            this.entries[index] = new ReplayTimelineEntry(index, record.Events[index]);
        }

        Array.Sort(this.entries, static (left, right) =>
        {
            var timestampComparison = left.TimestampMilliseconds.CompareTo(right.TimestampMilliseconds);
            return timestampComparison != 0
                ? timestampComparison
                : left.OriginalRecordedIndex.CompareTo(right.OriginalRecordedIndex);
        });
    }

    public int Count => this.entries.Length;

    public ReadOnlySpan<ReplayTimelineEntry> Events => this.entries;

    public ReadOnlySpan<ReplayTimelineEntry> GetEventsThrough(long timestampMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        return this.entries.AsSpan(0, this.FindFirstAfter(timestampMilliseconds));
    }

    public ReadOnlySpan<ReplayTimelineEntry> GetEventsAt(long timestampMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        var start = this.FindFirstAtOrAfter(timestampMilliseconds);
        var end = this.FindFirstAfter(timestampMilliseconds);
        return this.entries.AsSpan(start, end - start);
    }

    public ReadOnlySpan<ReplayTimelineEntry> GetEventsInRange(
        long startTimestampMilliseconds,
        long endTimestampMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startTimestampMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(endTimestampMilliseconds);
        if (endTimestampMilliseconds < startTimestampMilliseconds)
        {
            throw new ArgumentException("Timeline range end must not precede its start.");
        }

        var start = this.FindFirstAtOrAfter(startTimestampMilliseconds);
        var end = this.FindFirstAfter(endTimestampMilliseconds);
        return this.entries.AsSpan(start, end - start);
    }

    private int FindFirstAtOrAfter(long timestampMilliseconds)
    {
        var lower = 0;
        var upper = this.entries.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (this.entries[middle].TimestampMilliseconds < timestampMilliseconds)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower;
    }

    private int FindFirstAfter(long timestampMilliseconds)
    {
        var lower = 0;
        var upper = this.entries.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (this.entries[middle].TimestampMilliseconds <= timestampMilliseconds)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower;
    }
}
