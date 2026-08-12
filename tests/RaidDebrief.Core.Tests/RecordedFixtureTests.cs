using System.Security.Cryptography;
using System.Text.Json;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class RecordedFixtureTests
{
    private const string CaptureId = "6fe1b80f-567a-41a3-8912-6d013c137aa7";
    private const string ExpectedSha256 = "0a2aa6d519a5a602b04df8347e9985e43dec145527aa0ee071e43d7b26b194e4";
    private const long ExpectedByteLength = 37_652_192;

    private static readonly string FixtureDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "testdata",
        "recorded");

    [Fact]
    public void EightPlayerDutyCompleteFixtureMatchesReviewedBaseline()
    {
        var fixturePath = Path.Combine(FixtureDirectory, $"{CaptureId}.json");
        Assert.Equal(ExpectedByteLength, new FileInfo(fixturePath).Length);
        Assert.Equal(ExpectedSha256, ComputeSha256(fixturePath));

        var json = File.ReadAllText(fixturePath);
        Assert.DoesNotContain("contentId", json, StringComparison.OrdinalIgnoreCase);

        var record = CaptureJson.Deserialize(json);
        Assert.Equal(Guid.Parse(CaptureId), record.CaptureId);
        Assert.Equal(CaptureSchema.CurrentVersion, record.SchemaVersion);
        Assert.Equal(CaptureFeatures.None, record.Features);
        Assert.Equal(1229u, record.TerritoryType);
        Assert.Equal(926u, record.MapId);
        Assert.Equal(223_202, record.Frames[^1].TimestampMilliseconds);
        Assert.Equal(88, record.Actors.Length);
        Assert.Equal(2_233, record.Frames.Length);
        Assert.Equal(1_495, record.Events.Length);
        Assert.Equal(1_432, record.ActionEffects.Length);
        Assert.Single(record.WaymarkFrames);

        var players = record.Actors
            .Where(actor => actor.ObjectKind == "Pc")
            .OrderBy(actor => actor.StableActorId)
            .ToArray();
        Assert.Equal(8, players.Length);
        Assert.Equal(
            Enumerable.Range(1, 8).Select(index => $"Player {index}"),
            players.Select(player => player.Name));
        Assert.All(players, player =>
        {
            Assert.NotEqual(0u, player.EntityId);
            Assert.NotEqual(0xE0000000u, player.EntityId);
        });
        Assert.Contains(record.Actors, actor => actor.ObjectKind == "BattleNpc");

        AssertStrictlyIncreasing(record.Frames.Select(frame => frame.TimestampMilliseconds));
        AssertNondecreasing(record.Events.Select(observedEvent => observedEvent.TimestampMilliseconds));
        AssertNondecreasing(record.ActionEffects.Select(actionEffect => actionEffect.TimestampMilliseconds));

        var dutyCompletedTimestamp = Assert.Single(
            record.Events,
            observedEvent => observedEvent.Type == ObservedEventType.DutyCompleted).TimestampMilliseconds;
        var playerIds = players.Select(player => player.StableActorId).ToHashSet();
        Assert.All(
            record.Frames.Where(frame => frame.TimestampMilliseconds <= dutyCompletedTimestamp),
            frame => Assert.Equal(8, frame.Actors.Count(actor => playerIds.Contains(actor.StableActorId))));
    }

    [Fact]
    public void ReplayResolversAreStableAtRecordedPullTimestamps()
    {
        var fixturePath = Path.Combine(FixtureDirectory, $"{CaptureId}.json");
        var record = CaptureJson.Load(fixturePath);
        var resolver = new ActorStateResolver(record);
        var states = new ResolvedActorState[resolver.ActorCount];

        Assert.Equal(0, resolver.ResolveAll(0, states));

        var middleCount = resolver.ResolveAll(100_000, states);
        Assert.Equal(8, CountPlayers(states.AsSpan(0, middleCount)));
        var middleSnapshot = states.AsSpan(0, middleCount).ToArray();

        resolver.ResolveAll(200_000, states);
        var repeatedMiddleCount = resolver.ResolveAll(100_000, states);
        Assert.Equal(middleCount, repeatedMiddleCount);
        Assert.Equal(middleSnapshot, states.AsSpan(0, repeatedMiddleCount).ToArray());

        var endCount = resolver.ResolveAll(223_202, states);
        Assert.Equal(3, CountPlayers(states.AsSpan(0, endCount)));

        var timeline = new ReplayTimeline(record);
        Assert.Equal(1_495, timeline.Count);
        var timelineEntries = timeline.Events.ToArray();
        for (var index = 1; index < timelineEntries.Length; index++)
        {
            var previous = timelineEntries[index - 1];
            var current = timelineEntries[index];
            Assert.True(
                current.TimestampMilliseconds > previous.TimestampMilliseconds
                || (current.TimestampMilliseconds == previous.TimestampMilliseconds
                    && current.OriginalRecordedIndex > previous.OriginalRecordedIndex));
        }

        Assert.Equal(
            31,
            timelineEntries.Count(entry => entry.ObservedEvent.Type == ObservedEventType.Death));
        Assert.DoesNotContain(
            timelineEntries,
            entry => entry.ObservedEvent.Type == ObservedEventType.AliveTransition);
        var middleEvents = timeline.GetEventsThrough(100_000).ToArray();
        timeline.GetEventsThrough(223_202);
        Assert.Equal(middleEvents, timeline.GetEventsThrough(100_000).ToArray());

        var waymarks = new WaymarkStateResolver(record);
        Assert.Equal(0, waymarks.Resolve(0).Length);
        var initialWaymarks = waymarks.Resolve(1).ToArray();
        Assert.Equal(8, initialWaymarks.Length);
        Assert.All(initialWaymarks, waymark => Assert.False(waymark.Active));
        waymarks.Resolve(223_202);
        Assert.Equal(initialWaymarks, waymarks.Resolve(1).ToArray());

        var projection = ArenaProjection.FromPullRecord(record);
        Assert.Equal(ArenaBoundsKind.GenericObservedField, projection.BoundsKind);
        Assert.Equal(40, projection.Bounds.Width);
        Assert.Equal(projection.Bounds.Width, projection.Bounds.Depth);
        Assert.True(projection.Bounds.MinX <= projection.ObservedBounds.MinX);
        Assert.True(projection.Bounds.MinZ <= projection.ObservedBounds.MinZ);
        Assert.True(projection.Bounds.MaxX >= projection.ObservedBounds.MaxX);
        Assert.True(projection.Bounds.MaxZ >= projection.ObservedBounds.MaxZ);
        var sceneBuilder = new ArenaSceneBuilder(record, projection);
        var scene = sceneBuilder.CreateScene();
        sceneBuilder.Build(100_000, scene);
        var arenaActors = scene.Actors.ToArray();
        Assert.Equal(10, arenaActors.Length);
        Assert.Equal(8, arenaActors.Count(actor => actor.Kind == ArenaActorMarkerKind.Player));
        Assert.Equal(2, arenaActors.Count(actor => actor.Kind == ArenaActorMarkerKind.BattleNpc));
        Assert.Equal(0, scene.Waymarks.Length);

        sceneBuilder.Build(200_000, scene);
        sceneBuilder.Build(100_000, scene);
        Assert.Equal(arenaActors, scene.Actors.ToArray());
    }

    [Fact]
    public void ProvenancePinsTheByteForByteReviewedSource()
    {
        var provenancePath = Path.Combine(FixtureDirectory, $"{CaptureId}.provenance.json");
        using var document = JsonDocument.Parse(File.ReadAllText(provenancePath));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("provenanceSchemaVersion").GetInt32());
        Assert.Equal($"{CaptureId}.json", root.GetProperty("fixtureFile").GetString());
        Assert.Equal(CaptureId, root.GetProperty("sourceCaptureId").GetString());
        Assert.Equal(ExpectedSha256, root.GetProperty("sourceSha256").GetString());
        Assert.Equal(ExpectedSha256, root.GetProperty("fixtureSha256").GetString());
        Assert.Equal(ExpectedByteLength, root.GetProperty("sourceByteLength").GetInt64());
        Assert.Equal("none-byte-for-byte-copy", root.GetProperty("transformation").GetString());

        var privacyReview = root.GetProperty("privacyReview");
        Assert.Equal("Pull-local Player N aliases", privacyReview.GetProperty("playerNames").GetString());
        Assert.Equal(0, privacyReview.GetProperty("contentIdFields").GetInt32());
        Assert.Equal(0, privacyReview.GetProperty("invalidPlayerEntityIds").GetInt32());
    }

    private static int CountPlayers(ReadOnlySpan<ResolvedActorState> states)
    {
        var count = 0;
        foreach (ref readonly var state in states)
        {
            if (state.Actor.ObjectKind == "Pc")
            {
                count++;
            }
        }

        return count;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void AssertStrictlyIncreasing(IEnumerable<long> timestamps)
    {
        using var enumerator = timestamps.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            Assert.True(enumerator.Current > previous);
            previous = enumerator.Current;
        }
    }

    private static void AssertNondecreasing(IEnumerable<long> timestamps)
    {
        using var enumerator = timestamps.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            Assert.True(enumerator.Current >= previous);
            previous = enumerator.Current;
        }
    }
}
