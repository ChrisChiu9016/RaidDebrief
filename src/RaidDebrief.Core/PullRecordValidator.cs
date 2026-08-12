namespace RaidDebrief.Core;

public static class PullRecordValidator
{
    public static void Validate(PullRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.SchemaVersion != CaptureSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported capture schema version {record.SchemaVersion}; expected {CaptureSchema.CurrentVersion}.");
        }

        if (record.CaptureId == Guid.Empty)
        {
            throw new InvalidDataException("Capture ID must not be empty.");
        }

        if (record.EndedAtUtc < record.StartedAtUtc)
        {
            throw new InvalidDataException("Capture end time precedes its start time.");
        }

        if (record.Actors is null)
        {
            throw new InvalidDataException("Capture actors are missing.");
        }

        if (record.Frames is null)
        {
            throw new InvalidDataException("Capture frames are missing.");
        }

        if (record.Events is null)
        {
            throw new InvalidDataException("Capture events are missing.");
        }

        if (record.WaymarkFrames is null)
        {
            throw new InvalidDataException("Capture Waymark frames are missing.");
        }

        if (record.ActionEffects is null)
        {
            throw new InvalidDataException("Capture Action Effects are missing.");
        }

        if (record.TargetMarkerFrames is null)
        {
            throw new InvalidDataException("Capture Target Marker frames are missing.");
        }

        var actorIds = new HashSet<int>();
        var gameObjectIds = new HashSet<ulong>();
        var actorObjectIds = new Dictionary<int, ulong>();
        var partyIndices = new HashSet<int>();
        foreach (var actor in record.Actors)
        {
            if (actor is null)
            {
                throw new InvalidDataException("Capture contains a null actor.");
            }

            if (actor.StableActorId <= 0 || !actorIds.Add(actor.StableActorId))
            {
                throw new InvalidDataException($"Duplicate or invalid stable actor ID {actor.StableActorId}.");
            }

            if (actor.GameObjectId == 0 || !gameObjectIds.Add(actor.GameObjectId))
            {
                throw new InvalidDataException($"Duplicate or invalid game object ID {actor.GameObjectId}.");
            }

            actorObjectIds.Add(actor.StableActorId, actor.GameObjectId);
            if (actor.PartyIndex is { } partyIndex
                && (partyIndex < 0 || partyIndex >= 24 || !partyIndices.Add(partyIndex)))
            {
                throw new InvalidDataException(
                    $"Actor {actor.StableActorId} has an invalid or duplicate party index {partyIndex}.");
            }
        }

        long previousTimestamp = -1;
        foreach (var frame in record.Frames)
        {
            if (frame is null)
            {
                throw new InvalidDataException("Capture contains a null position frame.");
            }

            if (frame.TimestampMilliseconds < 0 || frame.TimestampMilliseconds <= previousTimestamp)
            {
                throw new InvalidDataException("Capture frame timestamps must be non-negative and strictly increasing.");
            }

            if (frame.Actors is null)
            {
                throw new InvalidDataException("Capture frame actors are missing.");
            }

            previousTimestamp = frame.TimestampMilliseconds;
            var sampledActorIds = new HashSet<int>();
            foreach (var actor in frame.Actors)
            {
                if (actor is null)
                {
                    throw new InvalidDataException("Capture frame contains a null actor sample.");
                }

                if (!actorIds.Contains(actor.StableActorId) || !sampledActorIds.Add(actor.StableActorId))
                {
                    throw new InvalidDataException(
                        $"Frame contains an unknown or duplicate stable actor ID {actor.StableActorId}.");
                }

                if (!float.IsFinite(actor.X)
                    || !float.IsFinite(actor.Y)
                    || !float.IsFinite(actor.Z)
                    || !float.IsFinite(actor.Rotation))
                {
                    throw new InvalidDataException("Actor position and rotation values must be finite.");
                }

                if (!float.IsFinite(actor.HitboxRadius) || actor.HitboxRadius < 0)
                {
                    throw new InvalidDataException(
                        $"Actor {actor.StableActorId} hitbox radius must be finite and non-negative.");
                }

                if (actor.CurrentHp > actor.MaxHp)
                {
                    throw new InvalidDataException(
                        $"Actor {actor.StableActorId} current HP exceeds maximum HP.");
                }
            }
        }

        long previousEventTimestamp = -1;
        var uniqueEvents = new HashSet<ObservedEvent>();
        foreach (var observedEvent in record.Events)
        {
            if (observedEvent is null)
            {
                throw new InvalidDataException("Capture contains a null observed event.");
            }

            if (observedEvent.TimestampMilliseconds < previousEventTimestamp)
            {
                throw new InvalidDataException("Observed event timestamps must not move backward.");
            }

            if (!Enum.IsDefined(observedEvent.Type) || !Enum.IsDefined(observedEvent.Source))
            {
                throw new InvalidDataException("Capture contains an unknown observed event type or source.");
            }

            if (observedEvent.Source != ExpectedSource(observedEvent.Type))
            {
                throw new InvalidDataException(
                    $"Observed event {observedEvent.Type} has source {observedEvent.Source} instead of {ExpectedSource(observedEvent.Type)}.");
            }

            if (RequiresActor(observedEvent.Type)
                && (observedEvent.StableActorId is not { } stableActorId || !actorIds.Contains(stableActorId)))
            {
                throw new InvalidDataException(
                    $"Observed event {observedEvent.Type} requires a known stable actor ID.");
            }

            if (observedEvent.Type is ObservedEventType.CastStarted
                    or ObservedEventType.CastEnded
                    or ObservedEventType.CastInterrupted
                && observedEvent.ActionId is not > 0)
            {
                throw new InvalidDataException($"Observed event {observedEvent.Type} requires an action ID.");
            }

            if (observedEvent.Type is ObservedEventType.StatusGained
                    or ObservedEventType.StatusRefreshed
                    or ObservedEventType.StatusLost
                && observedEvent.StatusId is not > 0)
            {
                throw new InvalidDataException($"Observed event {observedEvent.Type} requires a status ID.");
            }

            if (observedEvent.CurrentCastTime is { } currentCastTime
                && (!float.IsFinite(currentCastTime) || currentCastTime < 0))
            {
                throw new InvalidDataException("Observed cast progress must be finite and non-negative.");
            }

            if (observedEvent.TotalCastTime is { } totalCastTime
                && (!float.IsFinite(totalCastTime) || totalCastTime <= 0))
            {
                throw new InvalidDataException("Observed total cast time must be finite and positive.");
            }

            if (observedEvent.Type == ObservedEventType.CastStarted
                && (record.Features & CaptureFeatures.CastTiming) != 0
                && (observedEvent.CurrentCastTime is not { } recordedCurrentCastTime
                    || observedEvent.TotalCastTime is not { } recordedTotalCastTime
                    || recordedCurrentCastTime > recordedTotalCastTime + 0.2f))
            {
                throw new InvalidDataException(
                    "CastStarted requires coherent current and total cast time when CastTiming is recorded.");
            }

            if (observedEvent.StatusRemainingTime is { } remainingTime
                && (!float.IsFinite(remainingTime) || remainingTime < 0))
            {
                throw new InvalidDataException("Observed status remaining time must be finite and non-negative.");
            }

            if (observedEvent.Type is ObservedEventType.StatusGained or ObservedEventType.StatusRefreshed
                && (record.Features & CaptureFeatures.StatusTiming) != 0
                && observedEvent.StatusRemainingTime is null)
            {
                throw new InvalidDataException(
                    $"{observedEvent.Type} requires remaining time when StatusTiming is recorded.");
            }

            if (observedEvent.Type == ObservedEventType.InCombatChanged && observedEvent.State is null)
            {
                throw new InvalidDataException("InCombatChanged requires the observed combat state.");
            }

            if (!uniqueEvents.Add(observedEvent))
            {
                throw new InvalidDataException("Capture contains an exactly duplicated observed event.");
            }

            previousEventTimestamp = observedEvent.TimestampMilliseconds;
        }

        ValidateActionEffects(record.ActionEffects, actorObjectIds);

        long previousWaymarkTimestamp = -1;
        WaymarkFrame? previousWaymarkFrame = null;
        Span<bool> seenWaymarks = stackalloc bool[8];
        foreach (var frame in record.WaymarkFrames)
        {
            seenWaymarks.Clear();
            if (frame is null)
            {
                throw new InvalidDataException("Capture contains a null Waymark frame.");
            }

            if (frame.TimestampMilliseconds < 0 || frame.TimestampMilliseconds <= previousWaymarkTimestamp)
            {
                throw new InvalidDataException(
                    "Waymark frame timestamps must be non-negative and strictly increasing.");
            }

            if (frame.Waymarks is null || frame.Waymarks.Length != 8)
            {
                throw new InvalidDataException("A Waymark frame must contain exactly eight markers.");
            }

            foreach (var waymark in frame.Waymarks)
            {
                if (waymark is null)
                {
                    throw new InvalidDataException("Waymark frame contains a null marker.");
                }

                var waymarkIndex = (int)waymark.Id;
                if (!Enum.IsDefined(waymark.Id)
                    || waymarkIndex < 0
                    || waymarkIndex >= seenWaymarks.Length
                    || seenWaymarks[waymarkIndex])
                {
                    throw new InvalidDataException($"Waymark ID {waymark.Id} is invalid or duplicated.");
                }

                if (!float.IsFinite(waymark.X)
                    || !float.IsFinite(waymark.Y)
                    || !float.IsFinite(waymark.Z))
                {
                    throw new InvalidDataException($"Waymark {waymark.Id} position must be finite.");
                }

                seenWaymarks[waymarkIndex] = true;
            }

            if (previousWaymarkFrame is not null && WaymarksEqual(previousWaymarkFrame.Waymarks, frame.Waymarks))
            {
                throw new InvalidDataException("Consecutive Waymark frames must describe a state change.");
            }

            previousWaymarkTimestamp = frame.TimestampMilliseconds;
            previousWaymarkFrame = frame;
        }

        ValidateTargetMarkers(record.TargetMarkerFrames, actorObjectIds);
    }

    private static void ValidateTargetMarkers(
        TargetMarkerFrame[] frames,
        IReadOnlyDictionary<int, ulong> actorObjectIds)
    {
        long previousTimestamp = -1;
        TargetMarkerFrame? previousFrame = null;
        Span<bool> seenMarkers = stackalloc bool[TargetMarkerTimelineBuilder.MarkerCount];
        foreach (var frame in frames)
        {
            seenMarkers.Clear();
            if (frame is null)
            {
                throw new InvalidDataException("Capture contains a null Target Marker frame.");
            }

            if (frame.TimestampMilliseconds < 0 || frame.TimestampMilliseconds <= previousTimestamp)
            {
                throw new InvalidDataException(
                    "Target Marker frame timestamps must be non-negative and strictly increasing.");
            }

            if (frame.Markers is null
                || frame.Markers.Length != TargetMarkerTimelineBuilder.MarkerCount)
            {
                throw new InvalidDataException(
                    $"A Target Marker frame must contain exactly {TargetMarkerTimelineBuilder.MarkerCount} markers.");
            }

            foreach (var marker in frame.Markers)
            {
                if (marker is null)
                {
                    throw new InvalidDataException("Target Marker frame contains a null marker.");
                }

                var markerIndex = (int)marker.Id;
                if (!Enum.IsDefined(marker.Id)
                    || markerIndex < 0
                    || markerIndex >= seenMarkers.Length
                    || seenMarkers[markerIndex])
                {
                    throw new InvalidDataException(
                        $"Target Marker ID {marker.Id} is invalid or duplicated.");
                }

                if (marker.TargetObjectId == 0 && marker.TargetStableActorId is not null)
                {
                    throw new InvalidDataException(
                        $"Inactive Target Marker {marker.Id} cannot reference a stable actor.");
                }

                if (marker.TargetStableActorId is { } stableActorId
                    && (!actorObjectIds.TryGetValue(stableActorId, out var expectedObjectId)
                        || expectedObjectId != marker.TargetObjectId))
                {
                    throw new InvalidDataException(
                        $"Target Marker {marker.Id} stable actor ID does not match its game object ID.");
                }

                seenMarkers[markerIndex] = true;
            }

            if (previousFrame is not null
                && TargetMarkersEqual(previousFrame.Markers, frame.Markers))
            {
                throw new InvalidDataException(
                    "Consecutive Target Marker frames must describe a state change.");
            }

            previousTimestamp = frame.TimestampMilliseconds;
            previousFrame = frame;
        }
    }

    private static void ValidateActionEffects(
        ActionEffectRecord[] actionEffects,
        IReadOnlyDictionary<int, ulong> actorObjectIds)
    {
        long previousTimestamp = -1;
        Span<bool> entryIndices = stackalloc bool[8];
        foreach (var actionEffect in actionEffects)
        {
            if (actionEffect is null)
            {
                throw new InvalidDataException("Capture contains a null Action Effect.");
            }

            if (actionEffect.TimestampMilliseconds < previousTimestamp)
            {
                throw new InvalidDataException("Action Effect timestamps must not move backward.");
            }

            if (actionEffect.ActionId == 0 || actionEffect.SourceObjectId == 0)
            {
                throw new InvalidDataException("Action Effects require action and source object IDs.");
            }

            ValidateActorAssociation(
                actionEffect.SourceStableActorId,
                actionEffect.SourceObjectId,
                actorObjectIds,
                "source");

            if (actionEffect.Targets is null
                || actionEffect.Targets.Length is < 1 or > 32)
            {
                throw new InvalidDataException("An Action Effect must contain between one and 32 targets.");
            }

            foreach (var target in actionEffect.Targets)
            {
                entryIndices.Clear();
                if (target is null || target.TargetObjectId == 0)
                {
                    throw new InvalidDataException(
                        "An Action Effect contains a null or invalid target.");
                }

                ValidateActorAssociation(
                    target.TargetStableActorId,
                    target.TargetObjectId,
                    actorObjectIds,
                    "target");

                if (target.Entries is null || target.Entries.Length > 8)
                {
                    throw new InvalidDataException(
                        "An Action Effect target must contain no more than eight entries.");
                }

                foreach (var entry in target.Entries)
                {
                    if (entry is null
                        || entry.Index >= entryIndices.Length
                        || entryIndices[entry.Index]
                        || entry.RawType == ActionEffectDecoder.NothingType
                        || !Enum.IsDefined(entry.Kind))
                    {
                        throw new InvalidDataException(
                            "An Action Effect contains a null, invalid, or duplicate entry.");
                    }

                    if (entry.Kind != ActionEffectDecoder.Classify(entry.RawType)
                        || entry.Amount != ActionEffectDecoder.DecodeAmount(
                            entry.RawType,
                            entry.Param3,
                            entry.Param4,
                            entry.Value)
                        || entry.IsCritical != ActionEffectDecoder.DecodeCritical(
                            entry.RawType,
                            entry.Param0,
                            entry.Param1)
                        || entry.IsDirectHit != ActionEffectDecoder.DecodeDirectHit(
                            entry.RawType,
                            entry.Param0))
                    {
                        throw new InvalidDataException(
                            "An Action Effect entry does not match its decoded raw fields.");
                    }

                    entryIndices[entry.Index] = true;
                }
            }

            previousTimestamp = actionEffect.TimestampMilliseconds;
        }
    }

    private static void ValidateActorAssociation(
        int? stableActorId,
        ulong gameObjectId,
        IReadOnlyDictionary<int, ulong> actorObjectIds,
        string role)
    {
        if (stableActorId is not { } actorId)
        {
            return;
        }

        if (!actorObjectIds.TryGetValue(actorId, out var expectedObjectId)
            || expectedObjectId != gameObjectId)
        {
            throw new InvalidDataException(
                $"Action Effect {role} stable actor ID does not match its game object ID.");
        }
    }

    private static bool RequiresActor(ObservedEventType type) => type is
        ObservedEventType.CastStarted
        or ObservedEventType.CastEnded
        or ObservedEventType.CastInterrupted
        or ObservedEventType.Death
        or ObservedEventType.AliveTransition
        or ObservedEventType.StatusGained
        or ObservedEventType.StatusRefreshed
        or ObservedEventType.StatusLost
        or ObservedEventType.ActorSpawned
        or ObservedEventType.ActorDespawned;

    private static ObservedEventSource ExpectedSource(ObservedEventType type) => type switch
    {
        ObservedEventType.CastStarted
            or ObservedEventType.CastEnded
            or ObservedEventType.CastInterrupted => ObservedEventSource.PolledCastState,
        ObservedEventType.StatusGained
            or ObservedEventType.StatusRefreshed
            or ObservedEventType.StatusLost => ObservedEventSource.PolledStatusState,
        ObservedEventType.InCombatChanged => ObservedEventSource.PolledConditionState,
        ObservedEventType.DutyStarted
            or ObservedEventType.DutyWiped
            or ObservedEventType.DutyRecommenced
            or ObservedEventType.DutyCompleted => ObservedEventSource.DutyState,
        _ => ObservedEventSource.PolledActorState,
    };

    private static bool WaymarksEqual(WaymarkState[] left, WaymarkState[] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }
    private static bool TargetMarkersEqual(TargetMarkerState[] left, TargetMarkerState[] right)
    {
        for (var index = 0; index < left.Length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

}
