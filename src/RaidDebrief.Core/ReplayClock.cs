namespace RaidDebrief.Core;

public sealed class ReplayClock
{
    public ReplayClock(long durationMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationMilliseconds);
        this.DurationMilliseconds = durationMilliseconds;
    }

    public long DurationMilliseconds { get; }

    public long CurrentTimeMilliseconds { get; private set; }

    public bool IsPlaying { get; private set; }

    public void Play()
    {
        if (this.CurrentTimeMilliseconds < this.DurationMilliseconds)
        {
            this.IsPlaying = true;
        }
    }

    public void Pause() => this.IsPlaying = false;

    public void Seek(long timestampMilliseconds)
    {
        this.CurrentTimeMilliseconds = Math.Clamp(timestampMilliseconds, 0, this.DurationMilliseconds);
        if (this.CurrentTimeMilliseconds == this.DurationMilliseconds)
        {
            this.IsPlaying = false;
        }
    }

    public void Advance(long elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        if (!this.IsPlaying || elapsedMilliseconds == 0)
        {
            return;
        }

        var remainingMilliseconds = this.DurationMilliseconds - this.CurrentTimeMilliseconds;
        if (elapsedMilliseconds >= remainingMilliseconds)
        {
            this.CurrentTimeMilliseconds = this.DurationMilliseconds;
            this.IsPlaying = false;
            return;
        }

        this.CurrentTimeMilliseconds += elapsedMilliseconds;
    }
}
