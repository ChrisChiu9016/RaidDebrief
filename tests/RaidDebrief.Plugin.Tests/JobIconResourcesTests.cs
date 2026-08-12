using System;
using System.Linq;
using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class JobIconResourcesTests
{
    [Fact]
    public void EverySupportedJobHasAnEmbeddedPng()
    {
        var manifestResources = typeof(JobIconResources).Assembly
            .GetManifestResourceNames()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var classJobId in JobIconResources.SupportedClassJobIds)
        {
            var resourceName = JobIconResources.GetManifestResourceName(classJobId);
            Assert.NotNull(resourceName);
            Assert.Contains(resourceName, manifestResources);
            Assert.NotNull(JobIconResources.GetAbbreviation(classJobId));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(36)]
    [InlineData(43)]
    public void UnsupportedJobsHaveNoIcon(uint classJobId)
    {
        Assert.Null(JobIconResources.GetManifestResourceName(classJobId));
        Assert.Null(JobIconResources.GetAbbreviation(classJobId));
    }
}
