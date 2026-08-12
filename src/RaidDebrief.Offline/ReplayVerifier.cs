using System.Diagnostics;
using RaidDebrief.Core;
using RaidDebrief.UI;

namespace RaidDebrief.Offline;

internal static class ReplayVerifier
{
    private const double MaximumAverageSeekMilliseconds = 2;
    private const double MaximumAllocatedBytesPerSeek = 64;
    private const long SeekStride = 104_729;

    public static ReplayVerificationReport Verify(PullRecord record, int seekIterations)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seekIterations);

        var replay = new ReplaySession(record);
        var renderer = new SvgArenaRenderer();
        var forwardHashes = new ulong[record.Frames.Length];
        var fullReplayChecksum = 14_695_981_039_346_656_037UL;
        var renderedSvgCount = 0;

        for (var index = 0; index < record.Frames.Length; index++)
        {
            var timestamp = record.Frames[index].TimestampMilliseconds;
            replay.Seek(timestamp);
            if (replay.Scene.TimestampMilliseconds != timestamp)
            {
                throw new InvalidDataException("Replay scene timestamp diverged from a recorded frame timestamp.");
            }

            var sceneHash = ComputeStateHash(replay);
            forwardHashes[index] = sceneHash;
            fullReplayChecksum = Add(fullReplayChecksum, sceneHash);
            ValidateSvg(renderer.Render(replay.Scene));
            renderedSvgCount++;
        }

        for (var index = record.Frames.Length - 1; index >= 0; index--)
        {
            replay.Seek(record.Frames[index].TimestampMilliseconds);
            if (ComputeStateHash(replay) != forwardHashes[index])
            {
                throw new InvalidDataException(
                    $"Reverse scrub diverged at frame index {index} " +
                    $"({record.Frames[index].TimestampMilliseconds} ms).");
            }
        }

        replay.Seek(replay.DurationMilliseconds);
        ValidateSvg(renderer.Render(replay.Scene));
        renderedSvgCount++;
        if (replay.EventsThroughCurrentTime.Length != replay.Timeline.Count)
        {
            throw new InvalidDataException("Replay end does not expose the complete recorded Timeline.");
        }

        var milestones = ResolveMilestones(record, replay);
        var playbackAdvanceCount = PlayToEnd(replay);
        var performance = MeasureScrubbing(replay, seekIterations);
        if (performance.AverageSeekMilliseconds > MaximumAverageSeekMilliseconds)
        {
            throw new InvalidDataException(
                $"Average seek time {performance.AverageSeekMilliseconds:F6} ms exceeds " +
                $"the {MaximumAverageSeekMilliseconds:F3} ms verification ceiling.");
        }

        if (performance.AllocatedBytesPerSeek > MaximumAllocatedBytesPerSeek)
        {
            throw new InvalidDataException(
                $"Seek allocation {performance.AllocatedBytesPerSeek:F3} bytes exceeds " +
                $"the {MaximumAllocatedBytesPerSeek:F3} byte verification ceiling.");
        }

        return new ReplayVerificationReport(
            record.CaptureId,
            replay.DurationMilliseconds,
            record.Actors.Length,
            record.Frames.Length,
            record.Events.Length,
            record.WaymarkFrames.Length,
            record.ActionEffects.Length,
            renderedSvgCount,
            playbackAdvanceCount,
            fullReplayChecksum,
            milestones,
            performance.SeekIterations,
            performance.ElapsedMilliseconds,
            performance.AverageSeekMilliseconds,
            performance.AllocatedBytes,
            performance.AllocatedBytesPerSeek,
            performance.Checksum,
            MaximumAverageSeekMilliseconds,
            MaximumAllocatedBytesPerSeek);
    }

    private static ReplayMilestone[] ResolveMilestones(PullRecord record, ReplaySession replay)
    {
        var requests = new List<MilestoneRequest>(9)
        {
            new("start", 0),
        };
        if (record.Frames.Length > 0)
        {
            requests.Add(new MilestoneRequest("firstFrame", record.Frames[0].TimestampMilliseconds));
        }

        AddFirstEvent(requests, record, ObservedEventType.DutyStarted);
        AddFirstEvent(requests, record, ObservedEventType.Death);
        AddFirstEvent(requests, record, ObservedEventType.AliveTransition);
        AddFirstEvent(requests, record, ObservedEventType.ActorSpawned);
        AddFirstEvent(requests, record, ObservedEventType.ActorDespawned);
        AddFirstEvent(requests, record, ObservedEventType.DutyCompleted);
        requests.Add(new MilestoneRequest("end", replay.DurationMilliseconds));

        var results = new ReplayMilestone[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            replay.Seek(request.TimestampMilliseconds);
            var players = 0;
            var battleNpcs = 0;
            var deadActors = 0;
            foreach (ref readonly var actor in replay.Scene.Actors)
            {
                if (actor.Kind == ArenaActorMarkerKind.Player)
                {
                    players++;
                }
                else
                {
                    battleNpcs++;
                }

                if (actor.IsDead)
                {
                    deadActors++;
                }
            }

            results[index] = new ReplayMilestone(
                request.Name,
                request.TimestampMilliseconds,
                replay.Scene.Actors.Length,
                players,
                battleNpcs,
                deadActors,
                replay.Scene.Waymarks.Length,
                replay.EventsThroughCurrentTime.Length,
                ComputeStateHash(replay));
        }

        return results;
    }

    private static void AddFirstEvent(
        ICollection<MilestoneRequest> requests,
        PullRecord record,
        ObservedEventType type)
    {
        foreach (var observedEvent in record.Events)
        {
            if (observedEvent.Type == type)
            {
                requests.Add(new MilestoneRequest(type.ToString(), observedEvent.TimestampMilliseconds));
                return;
            }
        }
    }

    private static int PlayToEnd(ReplaySession replay)
    {
        replay.Seek(0);
        replay.Play();
        var advanceCount = 0;
        while (replay.IsPlaying)
        {
            replay.Advance(100);
            advanceCount++;
        }

        if (replay.CurrentTimeMilliseconds != replay.DurationMilliseconds
            || replay.Scene.TimestampMilliseconds != replay.DurationMilliseconds
            || replay.EventsThroughCurrentTime.Length != replay.Timeline.Count)
        {
            throw new InvalidDataException("Play-through did not stop at the complete Pull end state.");
        }

        return advanceCount;
    }

    private static SeekPerformance MeasureScrubbing(ReplaySession replay, int seekIterations)
    {
        var timestampRange = replay.DurationMilliseconds == long.MaxValue
            ? long.MaxValue
            : replay.DurationMilliseconds + 1;
        var checksum = 14_695_981_039_346_656_037UL;
        for (var index = 0; index < Math.Min(1_000, seekIterations); index++)
        {
            replay.Seek((index * SeekStride) % timestampRange);
            checksum = Add(checksum, ComputeStateHash(replay));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < seekIterations; index++)
        {
            replay.Seek((index * SeekStride) % timestampRange);
            checksum = Add(checksum, ComputeStateHash(replay));
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        return new SeekPerformance(
            seekIterations,
            elapsed.TotalMilliseconds,
            elapsed.TotalMilliseconds / seekIterations,
            allocatedBytes,
            (double)allocatedBytes / seekIterations,
            checksum);
    }

    private static void ValidateSvg(string svg)
    {
        if (!svg.StartsWith("<svg", StringComparison.Ordinal)
            || !svg.EndsWith("</svg>", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Offline renderer returned an invalid SVG document boundary.");
        }
    }

    private static ulong ComputeStateHash(ReplaySession replay)
    {
        var hash = Add(14_695_981_039_346_656_037UL, replay.CurrentTimeMilliseconds);
        foreach (ref readonly var actor in replay.Scene.Actors)
        {
            hash = Add(hash, actor.Actor.StableActorId);
            hash = Add(hash, (int)actor.Kind);
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.WorldX));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.WorldY));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.WorldZ));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.Facing.X));
            hash = Add(hash, BitConverter.SingleToInt32Bits(actor.Facing.Y));
            hash = Add(hash, actor.CurrentHp);
            hash = Add(hash, actor.MaxHp);
            hash = Add(hash, actor.IsDead ? 1 : 0);
            hash = Add(hash, actor.IsTargetable ? 1 : 0);
        }

        foreach (ref readonly var waymark in replay.Scene.Waymarks)
        {
            hash = Add(hash, (int)waymark.Id);
            hash = Add(hash, BitConverter.SingleToInt32Bits(waymark.WorldX));
            hash = Add(hash, BitConverter.SingleToInt32Bits(waymark.WorldY));
            hash = Add(hash, BitConverter.SingleToInt32Bits(waymark.WorldZ));
        }

        return Add(hash, replay.EventsThroughCurrentTime.Length);
    }

    private static ulong Add(ulong hash, ulong value)
    {
        hash ^= value;
        return hash * 1_099_511_628_211UL;
    }

    private static ulong Add(ulong hash, long value)
    {
        hash ^= unchecked((ulong)value);
        return hash * 1_099_511_628_211UL;
    }

    private readonly record struct MilestoneRequest(string Name, long TimestampMilliseconds);

    private readonly record struct SeekPerformance(
        int SeekIterations,
        double ElapsedMilliseconds,
        double AverageSeekMilliseconds,
        long AllocatedBytes,
        double AllocatedBytesPerSeek,
        ulong Checksum);
}

internal sealed record ReplayVerificationReport(
    Guid CaptureId,
    long DurationMilliseconds,
    int ActorCount,
    int FrameCount,
    int EventCount,
    int WaymarkFrameCount,
    int ActionEffectCount,
    int RenderedSvgCount,
    int PlaybackAdvanceCount,
    ulong FullReplayChecksum,
    ReplayMilestone[] Milestones,
    int SeekIterations,
    double SeekElapsedMilliseconds,
    double AverageSeekMilliseconds,
    long SeekAllocatedBytes,
    double AllocatedBytesPerSeek,
    ulong SeekChecksum,
    double MaximumAverageSeekMilliseconds,
    double MaximumAllocatedBytesPerSeek);

internal sealed record ReplayMilestone(
    string Name,
    long TimestampMilliseconds,
    int ActorCount,
    int PlayerCount,
    int BattleNpcCount,
    int DeadActorCount,
    int WaymarkCount,
    int EventCount,
    ulong StateHash);
