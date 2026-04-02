using GAE.Core.Models;

namespace GAE.WikiSync;

public static class WikiTemplates
{
    public static string PlayerPage(PlayerCharacter player)
    {
        var equipment = new List<string>();
        if (player.Equipment.Weapon is not null) equipment.Add($"- **Weapon:** {player.Equipment.Weapon.Name}");
        if (player.Equipment.Armor is not null) equipment.Add($"- **Armor:** {player.Equipment.Armor.Name}");
        if (player.Equipment.Shield is not null) equipment.Add($"- **Shield:** {player.Equipment.Shield.Name}");
        if (player.Equipment.Helmet is not null) equipment.Add($"- **Helmet:** {player.Equipment.Helmet.Name}");
        var equipStr = equipment.Count > 0 ? string.Join("\n", equipment) : "- None";

        var inventory = player.Inventory.Count > 0
            ? string.Join("\n", player.Inventory.Select(i => $"- {i.Name} (x{i.Quantity})"))
            : "- Empty";

        return $"""
            ---
            title: {player.Name}
            description: {player.Race} {player.Class} — Level {player.Level}
            tags: [player, {player.Race.ToLowerInvariant()}, {player.Class.ToLowerInvariant()}]
            ---

            # {player.Name}

            **Race:** {player.Race} | **Class:** {player.Class} | **Level:** {player.Level}

            ## Stats
            | Stat | Value | Modifier |
            |------|-------|----------|
            {FormatStatsTable(player)}

            ## Resources
            - **HP:** {player.Hp}/{player.MaxHp}
            - **MP:** {player.Mp}/{player.MaxMp}
            - **Gold:** {player.Gold}
            - **XP:** {player.Xp}
            - **Defense:** {player.Defense}

            ## Equipment
            {equipStr}

            ## Inventory
            {inventory}

            ## Backstory
            {player.Backstory}

            ---
            *Last active: {player.LastActiveAt:u}*
            """;
    }

    public static string RoomPage(Room room)
    {
        var exits = room.Exits.Count > 0
            ? string.Join("\n", room.Exits.Select(e => $"- **{e.Key}** → [{e.Value}](/rooms/{e.Value})"))
            : "- None";

        var npcs = room.Npcs.Count > 0
            ? string.Join("\n", room.Npcs.Select(n => $"- **{n.Name}** — {n.Personality} ({n.Faction})"))
            : "- None present";

        var items = room.Items.Count > 0
            ? string.Join("\n", room.Items.Select(i => $"- {i.Name}"))
            : "- None";

        var tags = room.EnvironmentTags.Count > 0
            ? string.Join(", ", room.EnvironmentTags)
            : "none";

        return $"""
            ---
            title: {room.Name}
            description: {room.Description}
            tags: [room, {tags}]
            ---

            # {room.Name}

            {room.Description}

            {(room.AsciiArt is not null ? $"```\n{room.AsciiArt}\n```\n" : "")}

            ## Exits
            {exits}

            ## NPCs
            {npcs}

            ## Items
            {items}

            ## Environment Tags
            {tags}

            ---
            *Discovered: {room.DiscoveredAt?.ToString("u") ?? "unknown"}*
            """;
    }

    public static string NpcPage(Npc npc)
    {
        var stats = new List<string>();
        if (npc.Hp.HasValue) stats.Add($"- **HP:** {npc.Hp}/{npc.MaxHp}");
        if (npc.AttackBonus.HasValue) stats.Add($"- **Attack Bonus:** {npc.AttackBonus:+0;-0}");
        if (npc.DamageDice is not null) stats.Add($"- **Damage:** {npc.DamageDice}");
        if (npc.Defense.HasValue) stats.Add($"- **Defense:** {npc.Defense}");
        var statsStr = stats.Count > 0 ? string.Join("\n", stats) : "- Not yet determined";

        var loot = npc.LootTable.Count > 0
            ? string.Join("\n", npc.LootTable.Select(i => $"- {i.Name} (x{i.Quantity})"))
            : "- None";

        return $"""
            ---
            title: {npc.Name}
            description: {npc.Personality}
            tags: [npc, {npc.Faction.ToLowerInvariant()}]
            ---

            # {npc.Name}

            **Faction:** {npc.Faction} | **Level:** {npc.Level} | **Disposition:** {npc.Disposition}

            ## Personality
            {npc.Personality}

            ## Combat Stats
            {statsStr}

            ## Loot Table
            {loot}

            ---
            *Hostile: {(npc.IsHostile ? "Yes" : "No")}*
            """;
    }

    public static string StoryEntryPage(StoryEntry entry)
    {
        return $"""
            ---
            title: Story — {entry.Timestamp:yyyy-MM-dd HH:mm}
            tags: [story, {entry.PlayerId}]
            ---

            # Story Event

            **Player:** {entry.PlayerId}
            **Location:** {entry.RoomId}
            **Time:** {entry.Timestamp:u}

            ## What Happened
            {entry.MechanicalSummary}

            ## The Tale
            {entry.Narration}
            """;
    }

    private static string FormatStatsTable(PlayerCharacter player) =>
        string.Join("\n", player.GetAttributeStats().Select(kv =>
            $"| {kv.Key.ToUpperInvariant()} | {kv.Value} | {PlayerCharacter.GetStatModifier(kv.Value):+0;-0} |"));
}
