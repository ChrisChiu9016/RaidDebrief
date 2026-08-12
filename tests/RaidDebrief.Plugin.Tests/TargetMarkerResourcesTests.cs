using System;
using System.Buffers.Binary;
using System.Linq;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class TargetMarkerResourcesTests
{
    [Fact]
    public void EveryTargetMarkerHasAnEmbeddedPng()
    {
        var assembly = typeof(TargetMarkerResources).Assembly;
        var resources = assembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        var markerIds = Enum.GetValues<TargetMarkerId>();

        Assert.Equal(TargetMarkerTimelineBuilder.MarkerCount, markerIds.Length);
        var header = new byte[24];
        foreach (var markerId in markerIds)
        {
            var resourceName = TargetMarkerResources.GetManifestResourceName(markerId);
            Assert.Contains(resourceName, resources);

            using var stream = Assert.IsAssignableFrom<System.IO.Stream>(
                assembly.GetManifestResourceStream(resourceName));
            Array.Clear(header);
            stream.ReadExactly(header);
            Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], header[..8].ToArray());
            Assert.True(BinaryPrimitives.ReadInt32BigEndian(header[16..20]) > 0);
            Assert.True(BinaryPrimitives.ReadInt32BigEndian(header[20..24]) > 0);
        }
    }

    [Fact]
    public void NativeMarkerSlotsMapLegacyAndAppendedAttackMarkersInGameOrder()
    {
        var markerIds = Enumerable.Range(0, TargetMarkerTimelineBuilder.MarkerCount)
            .Select(TargetMarkerReader.GetMarkerIdForNativeSlot)
            .ToArray();

        Assert.Equal(
            [
                TargetMarkerId.Attack1,
                TargetMarkerId.Attack2,
                TargetMarkerId.Attack3,
                TargetMarkerId.Attack4,
                TargetMarkerId.Attack5,
                TargetMarkerId.Bind1,
                TargetMarkerId.Bind2,
                TargetMarkerId.Bind3,
                TargetMarkerId.Stop1,
                TargetMarkerId.Stop2,
                TargetMarkerId.Square,
                TargetMarkerId.Circle,
                TargetMarkerId.Plus,
                TargetMarkerId.Triangle,
                TargetMarkerId.Attack6,
                TargetMarkerId.Attack7,
                TargetMarkerId.Attack8,
            ],
            markerIds);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TargetMarkerReader.GetMarkerIdForNativeSlot(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TargetMarkerReader.GetMarkerIdForNativeSlot(TargetMarkerTimelineBuilder.MarkerCount));
    }
}
