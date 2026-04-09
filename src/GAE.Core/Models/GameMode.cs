namespace GAE.Core.Models;

/// <summary>
/// Determines which rule set the player is using. CYOA mode uses radically
/// simpler mechanics — no HP/MP/XP numbers, just a health level, flat
/// inventory, and a choice tree.
/// </summary>
public enum GameMode
{
    /// <summary>Full RPG mode with stats, equipment, combat rolls, XP, and leveling.</summary>
    FullRpg,

    /// <summary>Choose Your Own Adventure — simplified state, narrative choices, no dice.</summary>
    ChooseYourOwnAdventure
}
