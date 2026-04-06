using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>
/// A registered monster/enemy template. Used by the dungeon generator to populate
/// combat encounters with level-appropriate enemies.
/// </summary>
public class MonsterTemplate : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    /// <summary>Personality text used for narrator flavour.</summary>
    public string Personality { get; set; } = "Aggressive and feral. Attacks anything that moves.";

    /// <summary>Minimum player level this monster is appropriate for.</summary>
    public int MinLevel { get; set; } = 1;

    /// <summary>Maximum player level this monster is appropriate for.</summary>
    public int MaxLevel { get; set; } = 3;

    /// <summary>Whether this is a boss-tier creature.</summary>
    public bool IsBoss { get; set; }

    // ── Base Stats (scaled by dungeon generator) ──

    public int BaseHp { get; set; } = 15;
    public int BaseAttack { get; set; } = 3;
    public int BaseDefense { get; set; } = 10;
    public string DamageDice { get; set; } = "1d6";

    /// <summary>Rarity: common, uncommon, rare, elite.</summary>
    public string Rarity { get; set; } = "common";

    /// <summary>Tags for filtering (e.g. "undead", "beast", "humanoid", "demon").</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Item template IDs that this monster might drop.</summary>
    public List<string> LootTableIds { get; set; } = [];

    /// <summary>Gold range this monster drops.</summary>
    public int GoldMin { get; set; } = 5;
    public int GoldMax { get; set; } = 25;

    /// <summary>Create an Npc instance from this template, scaled to the given level.</summary>
    public Npc ToNpc(int level, bool bossScale = false)
    {
        // Scale stats based on level within the monster's range
        var levelFactor = MaxLevel > MinLevel
            ? (float)(level - MinLevel) / (MaxLevel - MinLevel)
            : 0.5f;
        levelFactor = Math.Clamp(levelFactor, 0f, 1f);

        var hp = (int)(BaseHp * (1f + levelFactor * 0.8f));
        var atk = (int)(BaseAttack * (1f + levelFactor * 0.6f));
        var def = (int)(BaseDefense * (1f + levelFactor * 0.4f));

        if (bossScale)
        {
            hp = (int)(hp * 1.8);
            atk = (int)(atk * 1.4);
            def += 2;
        }

        return new Npc
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = Name,
            Personality = bossScale
                ? "Territorial and merciless. Guards the deepest chambers with lethal intent."
                : Personality,
            IsHostile = true,
            Level = level,
            Hp = hp,
            MaxHp = hp,
            AttackBonus = atk,
            Defense = def,
            DamageDice = DamageDice,
            LootTable = [],
        };
    }
}
