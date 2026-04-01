using GAE.Core.Models;

namespace GAE.Core.Interfaces;

public interface IProbabilityEngine
{
    DiceRoll Roll(string expression, string purpose = "");
    int RollStat();
    int[] RollStatArray();
    DiceRoll RollAttack(int modifier);
    DiceRoll RollDamage(string damageDice, int modifier);
    DiceRoll RollSkillCheck(string skill, int statModifier);
    DiceRoll RollInitiative(int dexModifier);
}
