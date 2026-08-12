using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class ReplayClockTests
{
    [Fact]
    public void PlayAdvancePauseAndResumeAreDeterministic()
    {
        var clock = new ReplayClock(durationMilliseconds: 10_000);

        clock.Play();
        clock.Advance(1_250);
        clock.Pause();
        clock.Advance(500);

        Assert.Equal(1_250, clock.CurrentTimeMilliseconds);
        Assert.False(clock.IsPlaying);

        clock.Play();
        clock.Advance(750);

        Assert.Equal(2_000, clock.CurrentTimeMilliseconds);
        Assert.True(clock.IsPlaying);
    }

    [Theory]
    [InlineData(-500, 0)]
    [InlineData(4_000, 4_000)]
    [InlineData(12_000, 10_000)]
    public void SeekClampsToPullBounds(long requestedTimestamp, long expectedTimestamp)
    {
        var clock = new ReplayClock(durationMilliseconds: 10_000);

        clock.Seek(requestedTimestamp);

        Assert.Equal(expectedTimestamp, clock.CurrentTimeMilliseconds);
        Assert.False(clock.IsPlaying);
    }

    [Fact]
    public void ReachingEndStopsPlaybackWithoutOverflow()
    {
        var clock = new ReplayClock(durationMilliseconds: 10_000);
        clock.Seek(9_500);
        clock.Play();

        clock.Advance(long.MaxValue);

        Assert.Equal(10_000, clock.CurrentTimeMilliseconds);
        Assert.False(clock.IsPlaying);

        clock.Seek(2_000);
        Assert.False(clock.IsPlaying);
        clock.Play();
        Assert.True(clock.IsPlaying);
    }

    [Fact]
    public void ZeroDurationCannotPlay()
    {
        var clock = new ReplayClock(durationMilliseconds: 0);

        clock.Play();

        Assert.Equal(0, clock.CurrentTimeMilliseconds);
        Assert.False(clock.IsPlaying);
    }

    [Fact]
    public void RejectsNegativeDurationAndElapsedTime()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayClock(-1));

        var clock = new ReplayClock(durationMilliseconds: 1_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(-1));
    }
}
