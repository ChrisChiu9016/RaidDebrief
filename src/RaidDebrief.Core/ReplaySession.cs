namespace RaidDebrief.Core;

public sealed class ReplaySession
{
    private readonly ArenaSceneBuilder sceneBuilder;
    private readonly Dictionary<int, ActorRecord> actorsById;

    public ReplaySession(PullRecord record)
        : this(record, ArenaProjection.FromPullRecord(record))
    {
    }

    public ReplaySession(PullRecord record, ArenaProjection projection)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(projection);

        this.Record = record;
        this.Timeline = new ReplayTimeline(record);
        this.actorsById = new Dictionary<int, ActorRecord>(record.Actors.Length);
        foreach (var actor in record.Actors)
        {
            this.actorsById.Add(actor.StableActorId, actor);
        }
        this.Clock = new ReplayClock(PullTiming.CalculateDurationMilliseconds(record));
        this.Projection = projection;
        this.sceneBuilder = new ArenaSceneBuilder(record, projection);
        this.Scene = this.sceneBuilder.CreateScene();
        this.sceneBuilder.Build(0, this.Scene);
    }

    public PullRecord Record { get; }

    public ReplayClock Clock { get; }

    public ReplayTimeline Timeline { get; }

    public ArenaProjection Projection { get; }

    public ArenaRenderScene Scene { get; }

    public long DurationMilliseconds => this.Clock.DurationMilliseconds;

    public long CurrentTimeMilliseconds => this.Clock.CurrentTimeMilliseconds;

    public bool IsPlaying => this.Clock.IsPlaying;

    public ReadOnlySpan<ReplayTimelineEntry> EventsThroughCurrentTime =>
        this.Timeline.GetEventsThrough(this.CurrentTimeMilliseconds);


    public bool TryGetActor(int stableActorId, out ActorRecord actor) =>
        this.actorsById.TryGetValue(stableActorId, out actor!);


    public void Play() => this.Clock.Play();

    public void Pause() => this.Clock.Pause();

    public void Seek(long timestampMilliseconds)
    {
        var previousTimestamp = this.CurrentTimeMilliseconds;
        this.Clock.Seek(timestampMilliseconds);
        if (this.CurrentTimeMilliseconds != previousTimestamp)
        {
            this.RebuildScene();
        }
    }

    public void Advance(long elapsedMilliseconds)
    {
        var previousTimestamp = this.CurrentTimeMilliseconds;
        this.Clock.Advance(elapsedMilliseconds);
        if (this.CurrentTimeMilliseconds != previousTimestamp)
        {
            this.RebuildScene();
        }
    }


    private void RebuildScene() => this.sceneBuilder.Build(this.CurrentTimeMilliseconds, this.Scene);
}
