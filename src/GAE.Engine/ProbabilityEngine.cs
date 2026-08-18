using System.Text.RegularExpressions;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

public partial class ProbabilityEngine : IProbabilityEngine
{
    // Dice expressions arrive from AI-generated content, YAML seeds and admin imports, so the
    // parser treats every numeric field as untrusted. These ceilings are far above anything the
    // game rules use (the largest live expression is a level-scaled 1d110) while keeping a
    // malformed string from sizing a multi-gigabyte array.
    private const int MaxDiceCount = 100;
    private const int MaxDiceSides = 1000;
    private const int MaxDiceModifier = 10_000;

    // Only set when an explicit seed is supplied (deterministic tests). Production runs on
    // Random.Shared, which is thread-safe — a plain Random instance is not, and this service is
    // registered as a singleton hit by concurrent requests.
    private readonly Random? _seededRandom;
    private readonly object _seededRandomLock = new();
    private readonly ILogger<ProbabilityEngine> _logger;

    public ProbabilityEngine(ILogger<ProbabilityEngine> logger, int? seed = null)
    {
        _seededRandom = seed.HasValue ? new Random(seed.Value) : null;
        _logger = logger;
    }

    /// <summary>
    /// Returns a die result in [1, sides]. Uses the shared thread-safe generator unless this
    /// instance was constructed with an explicit seed, in which case the seeded sequence is
    /// preserved under a lock so deterministic tests stay deterministic.
    /// </summary>
    private int NextDie(int sides)
    {
        if (_seededRandom is null)
            return Random.Shared.Next(1, sides + 1);

        lock (_seededRandomLock)
            return _seededRandom.Next(1, sides + 1);
    }

    public DiceRoll Roll(string expression, string purpose = "")
    {
        expression ??= string.Empty;

        var match = DiceRegex().Match(expression.Trim());
        if (!match.Success)
        {
            _logger.LogWarning("Invalid dice expression: {Expression}", expression);
            return new DiceRoll { Expression = expression, Purpose = purpose, Total = 0 };
        }

        if (!TryReadCount(match.Groups["count"], out int count)
            || !TryReadSides(match.Groups["sides"], out int sides)
            || !TryReadModifier(match.Groups["mod"], out int modifier))
        {
            _logger.LogWarning("Dice expression out of supported range: {Expression}", expression);
            return new DiceRoll { Expression = expression, Purpose = purpose, Total = 0 };
        }

        var rolls = new int[count];
        for (int i = 0; i < count; i++)
            rolls[i] = NextDie(sides);

        int total = rolls.Sum() + modifier;

        var result = new DiceRoll
        {
            Expression = expression,
            IndividualRolls = rolls,
            Modifier = modifier,
            Total = total,
            Purpose = purpose,
            IsCritical = count == 1 && sides == 20 && rolls[0] == 20,
            IsFumble = count == 1 && sides == 20 && rolls[0] == 1
        };

        _logger.LogDebug("Rolled {Expression} = {Rolls} + {Mod} = {Total} ({Purpose})",
            expression, string.Join("+", rolls), modifier, total, purpose);

        return result;
    }

    public int RollStat()
    {
        // 4d6 drop lowest
        var rolls = Enumerable.Range(0, 4).Select(_ => NextDie(6)).OrderDescending().ToArray();
        return rolls.Take(3).Sum();
    }

    public int[] RollStatArray()
        => Enumerable.Range(0, 6).Select(_ => RollStat()).OrderDescending().ToArray();

    public DiceRoll RollAttack(int modifier)
    {
        var roll = Roll("1d20", "Attack roll");
        roll.Modifier = modifier;
        roll.Total = roll.IndividualRolls[0] + modifier;
        return roll;
    }

    public DiceRoll RollDamage(string damageDice, int modifier)
    {
        var roll = Roll(damageDice, "Damage roll");
        roll.Modifier += modifier;
        roll.Total += modifier;
        return roll;
    }

    public DiceRoll RollSkillCheck(string skill, int statModifier)
    {
        var roll = Roll("1d20", $"Skill check: {skill}");
        roll.Modifier = statModifier;
        roll.Total = roll.IndividualRolls[0] + statModifier;
        return roll;
    }

    public DiceRoll RollInitiative(int dexModifier)
    {
        var roll = Roll("1d20", "Initiative");
        roll.Modifier = dexModifier;
        roll.Total = roll.IndividualRolls[0] + dexModifier;
        return roll;
    }

    /// <summary>
    /// Determines the outcome tier for an attack roll vs a target defense.
    /// Tiers: CriticalMiss (nat 1), Miss (below DC), GlancingHit (within 2 of DC), Hit (above DC), CriticalHit (nat 20 or 10+ over DC).
    /// </summary>
    public static RollOutcome DetermineOutcome(DiceRoll roll, int targetDefense)
    {
        if (roll.IsFumble)
            return RollOutcome.CriticalMiss;

        if (roll.IsCritical)
            return RollOutcome.CriticalHit;

        int margin = roll.Total - targetDefense;

        if (margin < 0)
            return RollOutcome.Miss;

        if (margin <= 2)
            return RollOutcome.GlancingHit;

        if (margin >= 10)
            return RollOutcome.CriticalHit;

        return RollOutcome.Hit;
    }

    /// <summary>
    /// Determines the outcome tier for a skill check vs a DC.
    /// </summary>
    public static RollOutcome DetermineSkillOutcome(DiceRoll roll, int dc)
    {
        if (roll.IsFumble)
            return RollOutcome.CriticalMiss;

        if (roll.IsCritical)
            return RollOutcome.CriticalHit;

        int margin = roll.Total - dc;

        if (margin < -5)
            return RollOutcome.CriticalMiss;

        if (margin < 0)
            return RollOutcome.Miss;

        if (margin <= 2)
            return RollOutcome.GlancingHit;

        if (margin >= 10)
            return RollOutcome.CriticalHit;

        return RollOutcome.Hit;
    }

    /// <summary>
    /// Reads the dice count, defaulting to 1 when omitted ("d20"). Rejects values that overflow
    /// an int or exceed <see cref="MaxDiceCount"/> so the roll array stays a sane size.
    /// </summary>
    private static bool TryReadCount(Group group, out int count)
    {
        if (!group.Success)
        {
            count = 1;
            return true;
        }

        return int.TryParse(group.Value, out count) && count >= 1 && count <= MaxDiceCount;
    }

    /// <summary>Reads the die size. A zero-sided die is not a die, so it is rejected.</summary>
    private static bool TryReadSides(Group group, out int sides)
        => int.TryParse(group.Value, out sides) && sides >= 1 && sides <= MaxDiceSides;

    /// <summary>
    /// Reads the trailing modifier. The regex tolerates whitespace around the sign so that the
    /// natural spellings LLMs and content authors produce ("1d6 + 3") parse instead of throwing;
    /// that whitespace is stripped before the numeric conversion.
    /// </summary>
    private static bool TryReadModifier(Group group, out int modifier)
    {
        if (!group.Success)
        {
            modifier = 0;
            return true;
        }

        var compact = group.Value.Replace(" ", string.Empty).Replace("\t", string.Empty);
        return int.TryParse(compact, out modifier) && Math.Abs(modifier) <= MaxDiceModifier;
    }

    [GeneratedRegex(@"^(?<count>\d+)?d(?<sides>\d+)(?:\s*(?<mod>[+-]\s*\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceRegex();
}
