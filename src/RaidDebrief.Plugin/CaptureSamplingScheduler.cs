using System;

namespace RaidDebrief.Plugin;

internal readonly record struct CaptureSamplePreparation(
    bool ShouldSample,
    long TimestampMilliseconds,
    long Gaps);

internal sealed class CaptureSamplingScheduler
{
    private readonly long intervalMilliseconds;
    private long nextSampleAtMilliseconds;
    private long lastObservedTimestampMilliseconds = -1;
    private long? lastCommittedTimestampMilliseconds;
    private CaptureSamplePreparation? pending;

    public CaptureSamplingScheduler(long intervalMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMilliseconds);
        this.intervalMilliseconds = intervalMilliseconds;
    }

    public CaptureSamplePreparation Prepare(long timestampMilliseconds)
    {
        if (timestampMilliseconds < 0
            || timestampMilliseconds < this.lastObservedTimestampMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampMilliseconds),
                timestampMilliseconds,
                "Sampling timestamps must be non-negative and monotonic.");
        }

        if (this.pending is not null)
        {
            throw new InvalidOperationException(
                "The pending sample must be committed or cancelled before preparing another sample.");
        }

        this.lastObservedTimestampMilliseconds = timestampMilliseconds;
        if (timestampMilliseconds < this.nextSampleAtMilliseconds)
        {
            return default;
        }

        var gaps = this.lastCommittedTimestampMilliseconds is { } previousTimestamp
            ? Math.Max(
                0,
                ((timestampMilliseconds - previousTimestamp + (this.intervalMilliseconds / 2))
                    / this.intervalMilliseconds) - 1)
            : 0;
        var preparation = new CaptureSamplePreparation(
            true,
            timestampMilliseconds,
            gaps);
        this.pending = preparation;
        return preparation;
    }

    public void Commit(in CaptureSamplePreparation preparation)
    {
        this.RequirePending(preparation);
        this.lastCommittedTimestampMilliseconds = preparation.TimestampMilliseconds;
        this.nextSampleAtMilliseconds =
            ((preparation.TimestampMilliseconds / this.intervalMilliseconds) + 1)
            * this.intervalMilliseconds;
        this.pending = null;
    }

    public void Cancel(in CaptureSamplePreparation preparation)
    {
        this.RequirePending(preparation);
        this.pending = null;
    }

    public void Reset()
    {
        this.nextSampleAtMilliseconds = 0;
        this.lastObservedTimestampMilliseconds = -1;
        this.lastCommittedTimestampMilliseconds = null;
        this.pending = null;
    }

    private void RequirePending(in CaptureSamplePreparation preparation)
    {
        if (!preparation.ShouldSample
            || this.pending is not { } pendingPreparation
            || pendingPreparation != preparation)
        {
            throw new InvalidOperationException("The sample preparation is not pending.");
        }
    }
}

internal readonly record struct FrameworkScanDecision(
    bool ShouldScan,
    CaptureSamplePreparation ProbePreparation);

internal sealed class FrameworkScanCoordinator
{
    private readonly CaptureSamplingScheduler probeScheduler;

    public FrameworkScanCoordinator(long intervalMilliseconds)
    {
        this.probeScheduler = new CaptureSamplingScheduler(intervalMilliseconds);
    }

    public FrameworkScanDecision Decide(
        bool captureSampleRequested,
        bool captureIsRecording,
        bool probeRefreshEnabled,
        long timestampMilliseconds)
    {
        if (captureSampleRequested)
        {
            this.probeScheduler.Reset();
            return new FrameworkScanDecision(true, default);
        }

        if (captureIsRecording || !probeRefreshEnabled)
        {
            this.probeScheduler.Reset();
            return default;
        }

        var preparation = this.probeScheduler.Prepare(timestampMilliseconds);
        return new FrameworkScanDecision(preparation.ShouldSample, preparation);
    }

    public void CompleteProbeScan(in FrameworkScanDecision decision)
    {
        if (decision.ProbePreparation.ShouldSample)
        {
            this.probeScheduler.Commit(decision.ProbePreparation);
        }
    }

    public void CancelProbeScan(in FrameworkScanDecision decision)
    {
        if (decision.ProbePreparation.ShouldSample)
        {
            this.probeScheduler.Cancel(decision.ProbePreparation);
        }
    }
}
