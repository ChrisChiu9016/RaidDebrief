using Xunit;

namespace RaidDebrief.Plugin.Tests;

public sealed class ReplayStatusEffectDatabaseTests
{
    [Theory]
    [InlineData("Damage taken is reduced.", (int)ReplayStatusEffectKind.DamageTakenReduction)]
    [InlineData("Healing magic potency is increased while damage taken by party members is reduced.", (int)ReplayStatusEffectKind.DamageTakenReduction)]
    [InlineData("Damage dealt is reduced.", (int)ReplayStatusEffectKind.DamageDealtReduction)]
    [InlineData("Physical damage dealt is reduced while magic damage dealt is reduced by a lesser amount.", (int)ReplayStatusEffectKind.DamageDealtReduction)]
    [InlineData("Physical and magic damage are reduced.", (int)ReplayStatusEffectKind.DamageDealtReduction)]
    [InlineData("Magic defense and healing magic potency are increased.", (int)ReplayStatusEffectKind.DefenseIncrease)]
    [InlineData("Impervious to most attacks.", (int)ReplayStatusEffectKind.Invulnerability)]
    [InlineData("Most attacks cannot reduce your HP to less than 1.", (int)ReplayStatusEffectKind.Invulnerability)]
    [InlineData("Unable to be KO'd by most attacks.", (int)ReplayStatusEffectKind.Invulnerability)]
    [InlineData("Most attacks will not reduce HP below 1.", (int)ReplayStatusEffectKind.Invulnerability)]
    [InlineData("A magicked barrier is nullifying damage.", (int)ReplayStatusEffectKind.Barrier)]
    [InlineData("When the barrier is completely absorbed, a new barrier is created.", (int)ReplayStatusEffectKind.Barrier)]
    [InlineData("Regenerating HP over time.", (int)ReplayStatusEffectKind.HealingOverTime)]
    [InlineData("Gradually restoring HP.", (int)ReplayStatusEffectKind.HealingOverTime)]
    [InlineData("Damage dealt is increased.", (int)ReplayStatusEffectKind.None)]
    [InlineData("HP recovery via healing actions is increased.", (int)ReplayStatusEffectKind.None)]
    public void ClassifiesEnglishLuminaDescription(
        string description,
        int expected)
    {
        Assert.Equal(
            (ReplayStatusEffectKind)expected,
            ReplayStatusEffectDatabase.ClassifyDescription(description));
    }

    [Fact]
    public void CompositeDescriptionRetainsMitigationWhenHotIsHidden()
    {
        const uint statusId = 1;
        var database = new ReplayStatusEffectDatabase(
        [
            new(
                statusId,
                "Damage taken is reduced. HP is restored over time when the effect expires."),
        ]);

        Assert.True(database.ShouldDisplayForPlayer(statusId, showHealingOverTime: false));
        Assert.True(database.IsHealingOverTime(statusId));
    }

    [Fact]
    public void HaimaAndPanhaimaShowOnlyTheirRecordedReserveStacks()
    {
        var database = new ReplayStatusEffectDatabase(
        [
            new(2612, "A magicked barrier is nullifying damage."),
            new(2613, "A magicked barrier is nullifying damage."),
            new(2642, "When the barrier is completely absorbed, a new barrier is created."),
            new(2643, "When the barrier is completely absorbed, a new barrier is created."),
        ]);

        Assert.False(database.ShouldDisplayForPlayer(2612, showHealingOverTime: true));
        Assert.False(database.ShouldDisplayForPlayer(2613, showHealingOverTime: true));
        Assert.True(database.ShouldDisplayForPlayer(2642, showHealingOverTime: false));
        Assert.True(database.ShouldDisplayForPlayer(2643, showHealingOverTime: false));
    }

    [Fact]
    public void TargetKindPreventsPlayerDamageDownAndBossBarrierFalsePositives()
    {
        var database = new ReplayStatusEffectDatabase(
        [
            new(1, "Damage dealt is reduced."),
            new(2, "A magicked barrier is nullifying damage."),
            new(3, "Regenerating HP over time."),
        ]);

        Assert.False(database.ShouldDisplayForPlayer(1, showHealingOverTime: true));
        Assert.True(database.ShouldDisplayForBoss(1));
        Assert.True(database.ShouldDisplayForPlayer(2, showHealingOverTime: false));
        Assert.False(database.ShouldDisplayForBoss(2));
        Assert.False(database.ShouldDisplayForPlayer(3, showHealingOverTime: false));
        Assert.True(database.ShouldDisplayForPlayer(3, showHealingOverTime: true));
        Assert.False(database.ShouldDisplayForBoss(3));
    }

    [Fact]
    public void PlayerDefenseIncreaseAndTankInvulnerabilityAreDisplayed()
    {
        var database = new ReplayStatusEffectDatabase(
        [
            new(317, "Magic defense and healing magic potency are increased."),
            new(82, "Impervious to most attacks."),
            new(409, "Most attacks cannot reduce your HP to less than 1."),
            new(810, "Unable to be KO'd by most attacks."),
            new(811, "Most attacks will not reduce HP below 1."),
            new(1836, "Impervious to most attacks."),
            new(3255, "Most attacks cannot reduce your HP to less than 1."),
        ]);

        foreach (var statusId in new uint[] { 317, 82, 409, 810, 811, 1836, 3255 })
        {
            Assert.True(database.ShouldDisplayForPlayer(
                statusId,
                showHealingOverTime: false));
            Assert.False(database.ShouldDisplayForBoss(statusId));
        }
    }
}
