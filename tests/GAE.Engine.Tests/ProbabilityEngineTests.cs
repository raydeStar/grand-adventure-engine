using GAE.Core.Models;
using GAE.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Engine.Tests;

public class ProbabilityEngineTests
{
    private readonly ProbabilityEngine _dice;

    public ProbabilityEngineTests()
    {
        _dice = new ProbabilityEngine(NullLogger<ProbabilityEngine>.Instance, seed: 42);
    }

    [Fact]
    public void Roll_SimpleD20_ReturnsValidRange()
    {
        var roll = _dice.Roll("1d20", "test");
        Assert.InRange(roll.Total, 1, 20);
        Assert.Single(roll.IndividualRolls);
        Assert.Equal("1d20", roll.Expression);
    }

    [Fact]
    public void Roll_2d6Plus3_ReturnsValidRange()
    {
        var roll = _dice.Roll("2d6+3", "test");
        Assert.InRange(roll.Total, 5, 15);
        Assert.Equal(2, roll.IndividualRolls.Length);
        Assert.Equal(3, roll.Modifier);
    }

    [Fact]
    public void Roll_WithNegativeModifier_HandlesCorrectly()
    {
        var roll = _dice.Roll("1d20-2", "test");
        Assert.Equal(-2, roll.Modifier);
    }

    [Fact]
    public void Roll_InvalidExpression_ReturnsZero()
    {
        var roll = _dice.Roll("invalid", "test");
        Assert.Equal(0, roll.Total);
    }

    [Fact]
    public void RollStat_Returns3To18()
    {
        for (int i = 0; i < 100; i++)
        {
            int stat = _dice.RollStat();
            Assert.InRange(stat, 3, 18);
        }
    }

    [Fact]
    public void RollStatArray_ReturnsSixValues()
    {
        var stats = _dice.RollStatArray();
        Assert.Equal(6, stats.Length);
        foreach (var stat in stats)
            Assert.InRange(stat, 3, 18);
    }

    [Fact]
    public void RollStatArray_IsDescending()
    {
        var stats = _dice.RollStatArray();
        for (int i = 1; i < stats.Length; i++)
            Assert.True(stats[i] <= stats[i - 1]);
    }

    [Fact]
    public void RollAttack_IncludesModifier()
    {
        var roll = _dice.RollAttack(3);
        Assert.Equal(3, roll.Modifier);
        Assert.Equal("Attack roll", roll.Purpose);
    }

    [Fact]
    public void DeterministicSeed_ProducesSameResults()
    {
        var dice1 = new ProbabilityEngine(NullLogger<ProbabilityEngine>.Instance, seed: 123);
        var dice2 = new ProbabilityEngine(NullLogger<ProbabilityEngine>.Instance, seed: 123);

        var roll1 = dice1.Roll("1d20", "test");
        var roll2 = dice2.Roll("1d20", "test");

        Assert.Equal(roll1.Total, roll2.Total);
    }
}
