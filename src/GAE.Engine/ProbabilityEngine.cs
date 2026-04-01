using System.Text.RegularExpressions;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

public partial class ProbabilityEngine : IProbabilityEngine
{
    private readonly Random _random;
    private readonly ILogger<ProbabilityEngine> _logger;

    public ProbabilityEngine(ILogger<ProbabilityEngine> logger, int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
        _logger = logger;
    }

    public DiceRoll Roll(string expression, string purpose = "")
    {
        var match = DiceRegex().Match(expression.Trim());
        if (!match.Success)
        {
            _logger.LogWarning("Invalid dice expression: {Expression}", expression);
            return new DiceRoll { Expression = expression, Purpose = purpose, Total = 0 };
        }

        int count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;
        int sides = int.Parse(match.Groups["sides"].Value);
        int modifier = 0;

        if (match.Groups["mod"].Success)
        {
            string modStr = match.Groups["mod"].Value;
            modifier = int.Parse(modStr);
        }

        var rolls = new int[count];
        for (int i = 0; i < count; i++)
            rolls[i] = _random.Next(1, sides + 1);

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
        var rolls = Enumerable.Range(0, 4).Select(_ => _random.Next(1, 7)).OrderDescending().ToArray();
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

    [GeneratedRegex(@"^(?<count>\d+)?d(?<sides>\d+)(?:\s*(?<mod>[+-]\s*\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiceRegex();
}
