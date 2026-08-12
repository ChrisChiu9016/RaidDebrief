using System;
using System.Buffers.Binary;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class TargetCircleResourceTests
{
    [Fact]
    public void TargetCircleIsEmbeddedAtItsExpectedDimensions()
    {
        var assembly = typeof(ReplayWindow).Assembly;
        Assert.Contains(
            ReplayWindow.TargetCircleResourceName,
            assembly.GetManifestResourceNames(),
            StringComparer.Ordinal);

        using var stream = Assert.IsAssignableFrom<System.IO.Stream>(
            assembly.GetManifestResourceStream(ReplayWindow.TargetCircleResourceName));
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);

        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], header[..8].ToArray());
        Assert.Equal(480, BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
        Assert.Equal(682, BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    [Fact]
    public void OmnidirectionalTargetRingIsEmbeddedAtItsExpectedDimensions()
    {
        var assembly = typeof(ReplayWindow).Assembly;
        Assert.Contains(
            ReplayWindow.OmnidirectionalTargetRingResourceName,
            assembly.GetManifestResourceNames(),
            StringComparer.Ordinal);

        using var stream = Assert.IsAssignableFrom<System.IO.Stream>(
            assembly.GetManifestResourceStream(ReplayWindow.OmnidirectionalTargetRingResourceName));
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);

        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], header[..8].ToArray());
        Assert.Equal(480, BinaryPrimitives.ReadInt32BigEndian(header[16..20]));
        Assert.Equal(480, BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }
}
