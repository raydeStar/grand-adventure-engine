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

    // ── Untrusted dice expressions ──────────────────────────────────────
    // Damage dice reach the engine from AI-generated content, YAML seeds and admin imports, so a
    // malformed expression must degrade to a zero roll rather than throw or exhaust memory.

    [Theory]
    [InlineData("1d6 + 3")]
    [InlineData("2d6 +4")]
    [InlineData("1d8- 2")]
    public void Roll_ModifierWithWhitespace_ParsesInsteadOfThrowing(string expression)
    {
        var roll = _dice.Roll(expression, "test");

        Assert.NotEmpty(roll.IndividualRolls);
        Assert.NotEqual(0, roll.Modifier);
    }

    [Fact]
    public void Roll_ModifierWithWhitespace_MatchesCompactSpelling()
    {
        var spaced = _dice.Roll("1d6 + 3", "test");
        var compact = _dice.Roll("1d6+3", "test");

        Assert.Equal(compact.Modifier, spaced.Modifier);
    }

    [Theory]
    [InlineData("99999999999d6")]   // count overflows an int
    [InlineData("1d99999999999")]   // sides overflows an int
    [InlineData("2000000000d6")]    // count would size a multi-gigabyte array
    [InlineData("1d0")]             // a zero-sided die is not a die
    [InlineData("1d6+99999999999")] // modifier overflows an int
    public void Roll_OutOfRangeExpression_ReturnsZeroRollWithoutThrowing(string expression)
    {
        var roll = _dice.Roll(expression, "test");

        Assert.Equal(0, roll.Total);
        Assert.Empty(roll.IndividualRolls);
        Assert.Equal(expression, roll.Expression);
    }

    [Fact]
    public void Roll_ConcurrentCalls_StayInRange()
    {
        // The engine is registered as a singleton and hit by concurrent requests, so the
        // underlying generator must be thread-safe.
        var unseeded = new ProbabilityEngine(NullLogger<ProbabilityEngine>.Instance);

        var totals = new int[2000];
        Parallel.For(0, totals.Length, i => totals[i] = unseeded.Roll("1d20", "concurrent").Total);

        Assert.All(totals, total => Assert.InRange(total, 1, 20));
    }
}
