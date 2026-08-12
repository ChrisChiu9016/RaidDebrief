namespace RaidDebrief.Core;

public sealed class WaymarkStateResolver
{
    private readonly WaymarkFrame[] frames;

    public WaymarkStateResolver(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        this.frames = record.WaymarkFrames;
    }

    public int FrameCount => this.frames.Length;

    public ReadOnlySpan<WaymarkState> Resolve(long timestampMilliseconds)
    {
        return this.TryResolveFrame(timestampMilliseconds, out var frame)
            ? frame.Waymarks
            : ReadOnlySpan<WaymarkState>.Empty;
    }

    public bool TryResolveFrame(long timestampMilliseconds, out WaymarkFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMilliseconds);
        var index = this.FindLastFrameAtOrBefore(timestampMilliseconds);
        if (index < 0)
        {
            frame = null!;
            return false;
        }

        frame = this.frames[index];
        return true;
    }

    private int FindLastFrameAtOrBefore(long timestampMilliseconds)
    {
        var lower = 0;
        var upper = this.frames.Length;
        while (lower < upper)
        {
            var middle = lower + ((upper - lower) / 2);
            if (this.frames[middle].TimestampMilliseconds <= timestampMilliseconds)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        return lower - 1;
    }
}
