using System;
using RaidDebrief.Core;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayCaptureFeatureWarningTests
{
    [Fact]
    public void ReplayPresentationCaptureNeedsNoWarning()
    {
        Assert.Null(
            ReplayWindow.BuildCaptureFeatureWarning(CreateRecord(CaptureFeatures.ReplayPresentation)));
    }

    [Fact]
    public void CaptureWithoutOwnerIdWarnsOnlyAboutSummonFiltering()
    {
        var warning = Assert.IsType<string>(
            ReplayWindow.BuildCaptureFeatureWarning(CreateRecord(
                CaptureFeatures.HitboxRadius
                    | CaptureFeatures.TargetMarkers
                    | CaptureFeatures.OmnidirectionalState)));

        Assert.Contains("OwnerID", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Target Circle", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("HitboxRadius", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureWithoutHitboxRadiusWarnsOnlyAboutBossAndAddCircles()
    {
        var warning = Assert.IsType<string>(
            ReplayWindow.BuildCaptureFeatureWarning(CreateRecord(
                CaptureFeatures.ActorOwnerId
                    | CaptureFeatures.TargetMarkers
                    | CaptureFeatures.OmnidirectionalState)));

        Assert.Contains("HitboxRadius", warning, StringComparison.Ordinal);
        Assert.Contains("world-space", warning, StringComparison.Ordinal);
        Assert.Contains("Boss／Add Target Circle", warning, StringComparison.Ordinal);
        Assert.Contains("Player Target Circle 仍使用固定大小", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnerID", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureWithoutEitherFeatureReportsBothLimitations()
    {
        var warning = Assert.IsType<string>(
            ReplayWindow.BuildCaptureFeatureWarning(CreateRecord(CaptureFeatures.None)));

        Assert.Contains("OwnerID", warning, StringComparison.Ordinal);
        Assert.Contains("HitboxRadius", warning, StringComparison.Ordinal);
        Assert.Contains("Target Circle", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyCaptureWarnsThatDynamicOmnidirectionalityIsUnavailable()
    {
        var warning = Assert.IsType<string>(
            ReplayWindow.BuildCaptureFeatureWarning(CreateRecord(CaptureFeatures.All)));

        Assert.Contains("身位無效", warning, StringComparison.Ordinal);
        Assert.Contains("BNpcBase 靜態判定", warning, StringComparison.Ordinal);
    }

    private static PullRecord CreateRecord(CaptureFeatures features) =>
        new()
        {
            Features = features,
            CaptureId = Guid.Parse("2e11e8ee-d270-43c4-9218-aa54ebbd1db5"),
            StartedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:00Z"),
            EndedAtUtc = DateTimeOffset.Parse("2026-08-09T00:00:01Z"),
            TerritoryType = 1,
            MapId = 1,
            Instance = 0,
            Actors = [],
            Frames = [],
        };
}
