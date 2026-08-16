using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Core.Tests;

public sealed class WaymarkTimelineTests
{
    [Fact]
    public void RecordsInitialPlacementMovementAndClearWithoutUnchangedFrames()
    {
        var builder = new WaymarkTimelineBuilder();
        var waymarks = CreateObservations();

        Assert.True(builder.Observe(0, waymarks));
        Assert.False(builder.Observe(100, waymarks));

        waymarks[0] = new WaymarkObservation(WaymarkId.A, true, 10, 20, 30);
        Assert.True(builder.Observe(200, waymarks));

        waymarks[0] = new WaymarkObservation(WaymarkId.A, true, 11, 21, 31);
        Assert.True(builder.Observe(300, waymarks));

        waymarks[0] = new WaymarkObservation(WaymarkId.A, false, 11, 21, 31);
        Assert.True(builder.Observe(400, waymarks));
        Assert.False(builder.Observe(500, waymarks));

        Assert.Equal(4, builder.Frames.Count);
        Assert.False(builder.Frames[0].Waymarks[0].Active);
        Assert.Equal(
            new WaymarkState { Id = WaymarkId.A, Active = true, X = 10, Y = 20, Z = 30 },
            builder.Frames[1].Waymarks[0]);
        Assert.Equal(11, builder.Frames[2].Waymarks[0].X);
        Assert.False(builder.Frames[3].Waymarks[0].Active);
    }

    [Fact]
    public void JsonRoundTripPreservesAllEightWaymarks()
    {
        var builder = new WaymarkTimelineBuilder();
        var waymarks = CreateObservations();
        waymarks[0] = new WaymarkObservation(WaymarkId.A, true, 1.25f, 2.5f, 3.75f);
        waymarks[7] = new WaymarkObservation(WaymarkId.Four, true, -4, -5, -6);
        Assert.True(builder.Observe(100, waymarks));
        var record = CreateRecord(builder.Frames.ToArray());

        var json = CaptureJson.Serialize(record);
        var loaded = CaptureJson.Deserialize(json);

        Assert.Contains("\"id\":\"a\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"four\"", json, StringComparison.Ordinal);
        Assert.Single(loaded.WaymarkFrames);
        Assert.Equal(record.WaymarkFrames[0].Waymarks, loaded.WaymarkFrames[0].Waymarks);
    }

    [Fact]
    public void RejectsNonFinitePositionAndDuplicateIds()
    {
        var builder = new WaymarkTimelineBuilder();
        var invalid = CreateObservations();
        invalid[0] = invalid[0] with { X = float.NaN };
        Assert.Throws<ArgumentException>(() => builder.Observe(0, invalid));

        var waymarks = CreateObservations();
        waymarks[1] = waymarks[1] with { Id = WaymarkId.A };
        Assert.Throws<ArgumentException>(() => builder.Observe(0, waymarks));
    }

    [Fact]
    public void AllInactiveWaymarksAreAValidSafeBaseline()
    {
        var builder = new WaymarkTimelineBuilder();

        Assert.True(builder.Observe(0, CreateObservations()));
        var record = CreateRecord(builder.Frames.ToArray());

        PullRecordValidator.Validate(record);
        Assert.All(record.WaymarkFrames[0].Waymarks, waymark => Assert.False(waymark.Active));
    }

    private static WaymarkObservation[] CreateObservations() =>
        Enum.GetValues<WaymarkId>()
            .Select(id => new WaymarkObservation(id, false, 0, 0, 0))
            .ToArray();

    private static PullRecord CreateRecord(WaymarkFrame[] waymarkFrames) => new()
    {
        CaptureId = Guid.Parse("24232763-73ca-48d2-ab06-d06dd68bbf87"),
        StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
        EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:01Z"),
        TerritoryType = 1,
        MapId = 2,
        Instance = 0,
        Actors = [],
        Frames = [],
        WaymarkFrames = waymarkFrames,
    };
}
