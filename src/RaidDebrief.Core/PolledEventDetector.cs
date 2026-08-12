namespace RaidDebrief.Core;

public sealed class PolledEventDetector
{
    private const float CastCompletionToleranceSeconds = 0.15f;
    private const float StatusRefreshToleranceSeconds = 0.35f;
    private readonly Dictionary<ulong, ActorState> actorStates = new();
    private long frameNumber;
    private bool hasBaseline;
    private bool previousInCombat;

    public void ObserveFrame(
        long timestampMilliseconds,
        ReadOnlySpan<PolledActorObservation> actors,
        bool inCombat,
        List<ObservedEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (timestampMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestampMilliseconds),
                timestampMilliseconds,
                "Event timestamps must be non-negative.");
        }

        this.frameNumber++;
        foreach (ref readonly var actor in actors)
        {
            if (actor.StableActorId <= 0 || actor.GameObjectId == 0)
            {
                throw new ArgumentException("Polled actors require valid stable and game object IDs.", nameof(actors));
            }

            if (!this.actorStates.TryGetValue(actor.GameObjectId, out var previous))
            {
                previous = new ActorState();
                this.actorStates.Add(actor.GameObjectId, previous);
                if (this.hasBaseline)
                {
                    AddActorEvent(
                        destination,
                        timestampMilliseconds,
                        ObservedEventType.ActorSpawned,
                        actor.StableActorId);
                }

                AddInitialStateEvents(destination, timestampMilliseconds, actor);
                previous.Update(actor, this.frameNumber);
                continue;
            }

            if (previous.LastSeenFrame == this.frameNumber)
            {
                throw new ArgumentException($"Game object ID {actor.GameObjectId} occurs twice in one frame.", nameof(actors));
            }

            if (!previous.IsPresent)
            {
                AddActorEvent(
                    destination,
                    timestampMilliseconds,
                    ObservedEventType.ActorSpawned,
                    actor.StableActorId);
                AddInitialStateEvents(destination, timestampMilliseconds, actor);
                previous.Update(actor, this.frameNumber);
                continue;
            }

            DetectCastTransition(destination, timestampMilliseconds, previous, actor);
            DetectLifeTransition(destination, timestampMilliseconds, previous, actor);
            DetectStatusTransitions(destination, timestampMilliseconds, previous, actor);
            previous.Update(actor, this.frameNumber);
        }

        if (this.hasBaseline)
        {
            foreach (var previous in this.actorStates.Values)
            {
                if (previous.IsPresent && previous.LastSeenFrame != this.frameNumber)
                {
                    AddActorEvent(
                        destination,
                        timestampMilliseconds,
                        ObservedEventType.ActorDespawned,
                        previous.StableActorId);
                    previous.IsPresent = false;
                }
            }

            if (inCombat != this.previousInCombat)
            {
                destination.Add(new ObservedEvent
                {
                    TimestampMilliseconds = timestampMilliseconds,
                    Type = ObservedEventType.InCombatChanged,
                    Source = ObservedEventSource.PolledConditionState,
                    State = inCombat,
                });
            }
        }

        this.previousInCombat = inCombat;
        this.hasBaseline = true;
    }
    private static void AddInitialStateEvents(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        in PolledActorObservation actor)
    {
        if (actor.IsCasting && actor.CastActionId != 0)
        {
            AddCastStartedEvent(destination, timestampMilliseconds, actor);
        }

        foreach (ref readonly var status in actor.Statuses.Span)
        {
            if (status.StatusId != 0)
            {
                AddStatusEvent(
                    destination,
                    timestampMilliseconds,
                    ObservedEventType.StatusGained,
                    actor.StableActorId,
                    status);
            }
        }
    }


    private static void DetectCastTransition(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        ActorState previous,
        in PolledActorObservation current)
    {
        var castChanged = previous.IsCasting
            && (!current.IsCasting || previous.CastActionId != current.CastActionId);
        if (castChanged)
        {
            var completed = previous.TotalCastTime > 0
                && previous.CurrentCastTime + CastCompletionToleranceSeconds >= previous.TotalCastTime;
            destination.Add(new ObservedEvent
            {
                TimestampMilliseconds = timestampMilliseconds,
                Type = completed ? ObservedEventType.CastEnded : ObservedEventType.CastInterrupted,
                Source = ObservedEventSource.PolledCastState,
                StableActorId = previous.StableActorId,
                ActionId = previous.CastActionId,
                RelatedObjectId = previous.CastTargetGameObjectId == 0
                    ? null
                    : previous.CastTargetGameObjectId,
            });
        }

        if (current.IsCasting && (!previous.IsCasting || previous.CastActionId != current.CastActionId))
        {
            AddCastStartedEvent(destination, timestampMilliseconds, current);
        }
    }

    private static void AddCastStartedEvent(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        in PolledActorObservation current)
    {
        destination.Add(new ObservedEvent
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = ObservedEventType.CastStarted,
            Source = ObservedEventSource.PolledCastState,
            StableActorId = current.StableActorId,
            ActionId = current.CastActionId,
            RelatedObjectId = current.CastTargetGameObjectId == 0
                ? null
                : current.CastTargetGameObjectId,
            CurrentCastTime = float.IsFinite(current.CurrentCastTime) && current.CurrentCastTime >= 0
                ? current.CurrentCastTime
                : null,
            TotalCastTime = float.IsFinite(current.TotalCastTime) && current.TotalCastTime > 0
                ? current.TotalCastTime
                : null,
        });
    }

    private static void DetectLifeTransition(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        ActorState previous,
        in PolledActorObservation current)
    {
        if (current.IsDead == previous.IsDead)
        {
            return;
        }

        destination.Add(new ObservedEvent
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = current.IsDead ? ObservedEventType.Death : ObservedEventType.AliveTransition,
            Source = ObservedEventSource.PolledActorState,
            StableActorId = current.StableActorId,
        });
    }

    private static void DetectStatusTransitions(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        ActorState previous,
        in PolledActorObservation current)
    {
        var currentStatuses = current.Statuses.Span;
        foreach (ref readonly var status in currentStatuses)
        {
            if (status.StatusId == 0)
            {
                continue;
            }

            var previousIndex = FindByIdentity(previous.Statuses, status);
            if (previousIndex < 0)
            {
                AddStatusEvent(
                    destination,
                    timestampMilliseconds,
                    ObservedEventType.StatusGained,
                    current.StableActorId,
                    status);
                continue;
            }

            var previousStatus = previous.Statuses[previousIndex];
            if (status.RemainingTime > previousStatus.RemainingTime + StatusRefreshToleranceSeconds
                || status.Param != previousStatus.Param)
            {
                AddStatusEvent(
                    destination,
                    timestampMilliseconds,
                    ObservedEventType.StatusRefreshed,
                    current.StableActorId,
                    status);
            }
        }

        foreach (var status in previous.Statuses)
        {
            if (FindByIdentity(currentStatuses, status) < 0)
            {
                AddStatusEvent(
                    destination,
                    timestampMilliseconds,
                    ObservedEventType.StatusLost,
                    current.StableActorId,
                    status);
            }
        }
    }

    private static int FindByIdentity(
        ReadOnlySpan<PolledStatusObservation> statuses,
        in PolledStatusObservation expected)
    {
        for (var index = 0; index < statuses.Length; index++)
        {
            if (statuses[index].HasSameIdentity(expected))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindByIdentity(
        List<PolledStatusObservation> statuses,
        in PolledStatusObservation expected)
    {
        for (var index = 0; index < statuses.Count; index++)
        {
            if (statuses[index].HasSameIdentity(expected))
            {
                return index;
            }
        }

        return -1;
    }

    private static void AddActorEvent(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        ObservedEventType type,
        int stableActorId)
    {
        destination.Add(new ObservedEvent
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = ObservedEventSource.PolledActorState,
            StableActorId = stableActorId,
        });
    }

    private static void AddStatusEvent(
        List<ObservedEvent> destination,
        long timestampMilliseconds,
        ObservedEventType type,
        int stableActorId,
        PolledStatusObservation status)
    {
        destination.Add(new ObservedEvent
        {
            TimestampMilliseconds = timestampMilliseconds,
            Type = type,
            Source = ObservedEventSource.PolledStatusState,
            StableActorId = stableActorId,
            StatusId = status.StatusId,
            RelatedObjectId = status.SourceObjectId == 0 ? null : status.SourceObjectId,
            StatusRemainingTime = status.RemainingTime,
            StatusParam = status.Param,
        });
    }

    private sealed class ActorState
    {
        public int StableActorId { get; private set; }

        public bool IsPresent { get; set; }

        public long LastSeenFrame { get; private set; }

        public bool IsDead { get; private set; }

        public bool IsCasting { get; private set; }

        public uint CastActionId { get; private set; }

        public ulong CastTargetGameObjectId { get; private set; }

        public float CurrentCastTime { get; private set; }

        public float TotalCastTime { get; private set; }

        public List<PolledStatusObservation> Statuses { get; } = new(8);

        public void Update(in PolledActorObservation actor, long frameNumber)
        {
            this.StableActorId = actor.StableActorId;
            this.IsPresent = true;
            this.LastSeenFrame = frameNumber;
            this.IsDead = actor.IsDead;
            this.IsCasting = actor.IsCasting;
            this.CastActionId = actor.CastActionId;
            this.CastTargetGameObjectId = actor.CastTargetGameObjectId;
            this.CurrentCastTime = actor.CurrentCastTime;
            this.TotalCastTime = actor.TotalCastTime;
            this.Statuses.Clear();
            foreach (ref readonly var status in actor.Statuses.Span)
            {
                if (status.StatusId != 0 && FindByIdentity(this.Statuses, status) < 0)
                {
                    this.Statuses.Add(status);
                }
            }
        }
    }
}
