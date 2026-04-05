namespace GAE.Engine.Configuration;

public class GameRulesConfig
{
    public CharacterCreationConfig CharacterCreation { get; set; } = new();
    public Dictionary<string, StatConfig> Stats { get; set; } = new();
    public CombatConfig Combat { get; set; } = new();
    public SkillCheckConfig SkillChecks { get; set; } = new();
    public RestConfig Rest { get; set; } = new();
    public DeathConfig Death { get; set; } = new();
    public LootConfig Loot { get; set; } = new();
    public LevelingConfig Leveling { get; set; } = new();
}

public class CharacterCreationConfig
{
    public string StatMethod { get; set; } = "4d6_drop_lowest";
    public int[] StandardArray { get; set; } = [15, 14, 13, 12, 10, 8];
    public int FlatValue { get; set; } = 10;
    public string StartingHpFormula { get; set; } = "base + con_mod";
    public string StartingMpFormula { get; set; } = "base + int_mod";
    public int StartingGold { get; set; } = 50;
    public int StartingLevel { get; set; } = 5;
    public List<string> StartingItems { get; set; } = [];
}

public class StatConfig
{
    public int Base { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public string Display { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public class CombatConfig
{
    public string AttackRoll { get; set; } = "d20 + {stat_mod}";
    public string MeleeStat { get; set; } = "str";
    public string RangedStat { get; set; } = "dex";
    public string MagicStat { get; set; } = "int";
    public string DamageFormula { get; set; } = "{weapon_dice} + {stat_mod}";
    public int CriticalThreshold { get; set; } = 20;
    public int CriticalMultiplier { get; set; } = 2;
    public int FumbleThreshold { get; set; } = 1;
    public int BaseDefense { get; set; } = 10;
    public string DefenseFormula { get; set; } = "base_defense + dex_mod + {armor_value}";
    public string InitiativeFormula { get; set; } = "d20 + dex_mod";

    /// <summary>
    /// Level-based proficiency bonus added to attack rolls.
    /// Formula: floor(Level / ProficiencyScaleLevel) + ProficiencyBaseBonus.
    /// Default: +2 base, +1 per 4 levels (Level 5 = +3, Level 9 = +4).
    /// </summary>
    public int ProficiencyBaseBonus { get; set; } = 2;
    public int ProficiencyScaleLevel { get; set; } = 4;
}

public class SkillCheckConfig
{
    public string Formula { get; set; } = "d20 + {stat_mod}";
    public Dictionary<string, int> DifficultyClasses { get; set; } = new();
    public Dictionary<string, string> StatMapping { get; set; } = new();
    public Dictionary<string, SocialSkillConfig> SocialSkills { get; set; } = new();
}

public class SocialSkillConfig
{
    public string Stat { get; set; } = "cha";
    public string? AltStat { get; set; }
    public List<string> Keywords { get; set; } = [];
}

public class RestConfig
{
    public string ShortRestHpRecovery { get; set; } = "d8 + con_mod";
    public string LongRestHpRecovery { get; set; } = "max";
    public string LongRestMpRecovery { get; set; } = "max";
    public string ShortRestMpRecovery { get; set; } = "d4 + int_mod";
}

public class DeathConfig
{
    public string AtZeroHp { get; set; } = "unconscious";
    public int DeathSaveThreshold { get; set; } = 10;
    public int DeathSaveFailuresToDie { get; set; } = 3;
    public int DeathSaveSuccessesToStabilize { get; set; } = 3;
}

public class LootConfig
{
    public double EnemyDropChance { get; set; } = 0.6;
    public string GoldDropRange { get; set; } = "1d20 + {enemy_level * 2}";
}

/// <summary>
/// Level progression configuration. Uses a simple percentage scaling model:
/// XP to next level = BaseXpPerLevel * Level.
/// HP/MP scale by a percentage bonus per level above 1.
/// </summary>
public class LevelingConfig
{
    /// <summary>XP required per level = BaseXpPerLevel * currentLevel. E.g., level 5→6 = 100*5 = 500 XP.</summary>
    public int BaseXpPerLevel { get; set; } = 100;

    /// <summary>Maximum achievable level.</summary>
    public int MaxLevel { get; set; } = 20;

    /// <summary>Percentage bonus to MaxHP per level above 1. 0.10 = +10% per level.</summary>
    public double HpScalePerLevel { get; set; } = 0.10;

    /// <summary>Percentage bonus to MaxMP per level above 1. 0.10 = +10% per level.</summary>
    public double MpScalePerLevel { get; set; } = 0.10;
}
