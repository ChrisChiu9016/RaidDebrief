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

    [Fact]
    public void RoleOrderGroupsRolesAndSplitsTanksAndHealersBySustainStyle()
    {
        // WAR DRK PLD GNB | AST WHM SCH SGE | melee | physical ranged | casters
        uint[] expected = [21, 32, 19, 37, 33, 24, 28, 40, 39, 38, 35];
        var actual = expected
            .OrderBy(JobIconResources.GetRoleOrder)
            .ToArray();

        Assert.Equal(expected, actual);

        // Self-sustain tanks precede mitigation tanks.
        Assert.True(JobIconResources.GetRoleOrder(32) < JobIconResources.GetRoleOrder(19));
        // Pure healers precede barrier healers.
        Assert.True(JobIconResources.GetRoleOrder(24) < JobIconResources.GetRoleOrder(28));
        // Every healer still outranks every DPS, and every tank every healer.
        Assert.True(JobIconResources.GetRoleOrder(40) < JobIconResources.GetRoleOrder(20));
        Assert.True(JobIconResources.GetRoleOrder(37) < JobIconResources.GetRoleOrder(33));

        foreach (var classJobId in JobIconResources.SupportedClassJobIds)
        {
            Assert.True(JobIconResources.GetRoleOrder(classJobId) < JobIconResources.UnknownRoleOrder);
        }

        Assert.Equal(JobIconResources.UnknownRoleOrder, JobIconResources.GetRoleOrder(0));
    }
}
