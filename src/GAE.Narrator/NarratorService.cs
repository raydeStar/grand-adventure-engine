using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Logging;

namespace GAE.Narrator;

public class NarratorService : INarratorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NarratorService> _logger;
    private readonly WorldKnowledgeBuilder? _knowledge;
    private readonly IConversationLogger? _conversationLogger;
    private readonly IContentRegistryService? _registry;
    private readonly IWorldContext? _worldContext;
    private readonly IWorldRepository? _worldRepository;
    private string _model;
    private bool _modelResolved;

    public NarratorService(HttpClient httpClient, ILogger<NarratorService> logger, string model = "default", WorldKnowledgeBuilder? knowledge = null, IConversationLogger? conversationLogger = null, IContentRegistryService? registry = null, IWorldContext? worldContext = null, IWorldRepository? worldRepository = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _knowledge = knowledge;
        _conversationLogger = conversationLogger;
        _registry = registry;
        _worldContext = worldContext;
        _worldRepository = worldRepository;
        _model = model;
        _modelResolved = !string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default)
    {
        if (TryBuildDeterministicLookNarration(context, out var lookNarration))
            return lookNarration;

        // Movement gets a specialized "arrival impressions" prompt
        if (context.Action.Type == ActionType.Move && context.MechanicalResult.Success)
            return await NarrateRoomArrivalAsync(context, ct);

        var voiceHint = context.MechanicalResult.Success ? "success" : "failure";
        var voiceBlock = ResolveNarratorVoice(context.Player, voiceHint);
        var systemPrompt = $"""
            You are the narrator of a dark-fantasy text adventure.

            {voiceBlock}

            RULES:
            - Narrate what the engine decided. Never contradict the mechanical result.
            - Never ask questions or break the fourth wall about being a narrator/AI.
            - For failed movement, describe the futile attempt with humor.
            - For failed actions, honor the failure reason but translate it into something entertaining.
            - NPCs should react with personality — annoyance, amusement, disgust, concern.
            - NEVER list room contents, exits, or NPC names as information — the game UI handles that.
            """;

        var resolvedOutcome = string.IsNullOrWhiteSpace(context.MechanicalResult.MechanicalSummary)
            ? "Narrate from the room and action context without repeating system labels."
            : context.MechanicalResult.MechanicalSummary;

        var visibleNpcs = context.CurrentRoom.Npcs.Count > 0
            ? SummarizeEntities(context.CurrentRoom.Npcs, npc => npc.Name)
            : "None";

        var visibleItems = context.CurrentRoom.Items.Count > 0
            ? SummarizeEntities(context.CurrentRoom.Items, item => item.Name, item => item.Quantity)
            : "None";

        var exits = context.CurrentRoom.Exits.Count > 0
            ? string.Join(", ", context.CurrentRoom.Exits.Keys)
            : "None";

        var loreContext = await GetRoomKnowledgeAsync(context.CurrentRoom, ct);
        var worldContext = BuildWorldContextBlock(context);

        var userPrompt = $"""
            Player: {context.Player.Name} ({context.Player.Race} {context.Player.Class}, Level {context.Player.Level})
            Location: {context.CurrentRoom.Name} — {context.CurrentRoom.Description}
            Visible NPCs: {visibleNpcs}
            Visible Items: {visibleItems}
            Exits: {exits}
            Action: {context.Action.RawInput}
            Action Outcome: {(context.MechanicalResult.Success ? "success" : "failure")}
            Resolved Outcome: {resolvedOutcome}
            {worldContext}
            {loreContext}
            """;

        try
        {
            return await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, maxTokens: 512,
                playerId: context.Player.Id, roomId: context.CurrentRoom.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio request failed, using contextual narration fallback");
            return BuildContextualFallbackNarration(context);
        }
    }

    /// <summary>
    /// Specialized narration for room arrivals. Produces atmospheric second-person impressions —
    /// NPC reactions, sensory details, things that catch the eye — rather than repeating
    /// the room description (which is shown in the room panel).
    /// </summary>
    private async Task<string> NarrateRoomArrivalAsync(NarratorContext context, CancellationToken ct)
    {
        var voiceBlock = ResolveNarratorVoice(context.Player);
        var systemPrompt = $"""
            You are the narrator of a dark-fantasy text adventure.

            The player just walked into a new room. The room's NAME, DESCRIPTION, NPCs, ITEMS, and
            EXITS are already displayed in a separate info card — DO NOT repeat any of that.
            Instead, narrate the ARRIVAL MOMENT from SECOND PERSON perspective:

            WHAT TO INCLUDE (pick 2-3, not all):
            - A sensory hit: the first thing the player smells, hears, or feels on their skin.
            - NPC reactions: does anyone look up? Ignore the player? Reach for a weapon? Offer a drink?
            - Something that catches the eye: a glint, a stain, something out of place.
            - Atmosphere/mood: the vibe of the space as the player steps in. Tension, warmth, dread, boredom.
            - The player's personal state: are they tired, wounded, confident, nervous?

            {voiceBlock}

            ADDITIONAL RULES:
            - Write 2-3 sentences. This is a quick establishing shot, not a novel paragraph.
            - NEVER name the room. NEVER list exits, NPCs, or items — the card handles that.
            - NEVER say "You enter [room name]" or "You find yourself in [description]."
            - Never ask questions or break the fourth wall.
            """;

        var npcsPresent = context.CurrentRoom.Npcs.Count > 0
            ? string.Join(", ", context.CurrentRoom.Npcs.Select(n =>
                $"{n.Name} ({n.Personality}{(n.IsHostile ? ", hostile" : "")})"
              ))
            : "Empty — no one here";

        var notableItems = context.CurrentRoom.Items.Count > 0
            ? string.Join(", ", context.CurrentRoom.Items.Select(i =>
                i.Quantity > 1 ? $"{i.Name} (x{i.Quantity})" : i.Name
              ))
            : "Nothing notable on the ground";

        var envTags = context.CurrentRoom.EnvironmentTags.Count > 0
            ? string.Join(", ", context.CurrentRoom.EnvironmentTags)
            : "none";

        var loreContext = await GetRoomKnowledgeAsync(context.CurrentRoom, ct);
        var worldContext = BuildWorldContextBlock(context);

        var userPrompt = $"""
            Player: {context.Player.Name} ({context.Player.Race} {context.Player.Class}, Level {context.Player.Level})
            Arrived at: {context.CurrentRoom.Name}
            Room vibe: {context.CurrentRoom.Description}
            Environment: {envTags}
            NPCs present: {npcsPresent}
            Items visible: {notableItems}
            Direction traveled: {context.Action.Direction ?? "unknown"}
            {worldContext}
            {loreContext}
            Narrate the arrival moment.
            """;

        try
        {
            return await CompletionOrThrowAsync(systemPrompt, userPrompt, ct,
                playerId: context.Player.Id, roomId: context.CurrentRoom.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio request failed for room arrival, using fallback");
            return BuildArrivalFallback(context);
        }
    }

    /// <summary>Builds a reasonable offline fallback for room arrivals (second person).</summary>
    private static string BuildArrivalFallback(NarratorContext context)
    {
        var room = context.CurrentRoom;
        var player = context.Player;
        var parts = new List<string>();

        if (room.Npcs.Count > 0)
        {
            var firstNpc = room.Npcs[0];
            string[] reactions = [
                $"{firstNpc.Name} glances up as you step in.",
                $"You catch {firstNpc.Name} watching you from the corner of your eye.",
                $"{firstNpc.Name} doesn't look up. Either you're not interesting enough, or they already know you're here.",
                $"The first thing you notice is {firstNpc.Name} — hard to miss."
            ];
            parts.Add(reactions[Math.Abs(room.Id.GetHashCode()) % reactions.Length]);
        }
        else
        {
            parts.Add("Empty — or at least, that's how it looks. You keep your guard up.");
        }

        // Add a personal state beat based on HP
        double hpPct = player.MaxHp > 0 ? (double)player.Hp / player.MaxHp : 1.0;
        if (hpPct < 0.3)
            parts.Add("Every step costs you. You need to rest soon.");
        else if (hpPct < 0.6)
            parts.Add("You've felt better, but you've also felt worse.");

        return string.Join(" ", parts);
    }

    public async Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default)
    {
        var isDungeon = sourceRoom.EnvironmentTags.Any(t =>
            t.Equals("dungeon", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("generated_dungeon", StringComparison.OrdinalIgnoreCase));

        var difficultyTag = sourceRoom.EnvironmentTags
            .FirstOrDefault(t => t.StartsWith("difficulty_", StringComparison.OrdinalIgnoreCase));

        var dungeonRules = isDungeon ? $"""

            DUNGEON-SPECIFIC RULES:
            - This room is INSIDE a dungeon. Maintain the dungeon theme from the source room.
            - Include "dungeon" and "generated_dungeon" in environmentTags.
            {(difficultyTag is not null ? $"- Carry over the difficulty tag: \"{difficultyTag}\"." : "")}
            - Dungeon rooms should have: monsters (hostile NPCs with combat stats), traps, puzzles, treasure.
            - NPCs in dungeons should include "level", "hp", "attack", "defense" fields for combat.
            - Include locked doors (hint at keys), riddles on walls, treasure chests, or environmental hazards.
            - Some rooms should have NO monsters — just atmosphere, puzzles, or loot.
            - Vary between: combat rooms, puzzle rooms, treasure rooms, and atmospheric corridors.
            - Every 3-4 rooms, include a "boss" room with a tougher enemy and better loot.
            """ : "";

        var systemPrompt = $$"""
            You are a world-building engine for a dark fantasy RPG with the flavor of classic Sierra adventures.
            Generate a room/location as JSON with these exact fields:
            {
              "name": "Short Evocative Name",
              "description": "2-3 atmospheric sentences. Hint at things worth interacting with.",
              "environmentTags": ["tag1", "tag2"],
              "npcs": [{ "id": "unique_id", "name": "NPC Name", "personality": "one-line personality with attitude", "isHostile": false, "level": 1, "hp": 20, "attack": 4, "defense": 10 }],
              "items": [{ "name": "Item Name", "description": "brief flavor text", "type": "Misc", "quantity": 1, "value": 5 }]
            }

            RULES:
            - Keep it consistent with the source location's theme and direction of travel.
            - Include 0-2 NPCs with distinct, memorable personalities (grumpy, paranoid, flirty, etc.)
            - Include 1-3 items that make sense for the location (weapons, potions, gold, curiosities, junk)
            - Each item must have a "type" field: one of "Weapon", "Armor", "Shield", "Helmet", "Potion", "Scroll", "Key", "QuestItem", "Misc", "Ring", "Amulet", "Bracelet", "Cloak", "Boots", "Gloves"
            - Items should have a "value" field (gold piece worth, even junk is worth 1gp)
            - Descriptions should hint at interactive elements: things to search, people to talk to, paths to explore.
            - Add 2-4 environment tags for mood/theme (e.g. "dark", "tavern", "forest", "ruins", "marketplace").
            - Name should be evocative, not generic ("The Butcher's Alley" not "Alley").
            - Vary between indoor/outdoor, populated/desolate, safe/dangerous.
            - Return ONLY valid JSON, no markdown, no code fences.
            {{dungeonRules}}
            """;

        var userPrompt = $"""
            The player is moving {direction} from "{sourceRoom.Name}" ({string.Join(", ", sourceRoom.EnvironmentTags)}).
            Generate a new location for room ID "{roomId}".
            Make it interesting — give the player something to do, someone to talk to, or something to find.
            """;

        try
        {
            var json = await CompletionAsync(systemPrompt, userPrompt, ct, maxTokens: 768, operation: "generate-room", roomId: roomId);
            var generated = JsonSerializer.Deserialize<GeneratedRoom>(json, _jsonOptions);
            if (generated is not null)
            {
                return new Room
                {
                    Id = roomId,
                    Name = generated.Name ?? $"Unexplored ({roomId})",
                    Description = generated.Description ?? "A mysterious place.",
                    EnvironmentTags = generated.EnvironmentTags ?? [],
                    Npcs = generated.Npcs?.Select(n => new Npc
                    {
                        Id = n.Id ?? Guid.NewGuid().ToString(),
                        Name = n.Name ?? "Unknown",
                        Personality = n.Personality ?? "",
                        IsHostile = n.IsHostile,
                        Level = n.Level > 0 ? n.Level : 1,
                        Hp = n.Hp > 0 ? n.Hp : (n.IsHostile ? 15 : null),
                        MaxHp = n.Hp > 0 ? n.Hp : (n.IsHostile ? 15 : null),
                        AttackBonus = n.Attack > 0 ? n.Attack : (n.IsHostile ? 3 : null),
                        Defense = n.Defense > 0 ? n.Defense : (n.IsHostile ? 10 : null)
                    }).ToList() ?? [],
                    Items = generated.Items?.Select(i =>
                    {
                        var itemType = Enum.TryParse<ItemType>(i.Type, ignoreCase: true, out var parsed) ? parsed : ItemType.Misc;
                        return new InventoryItem
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = i.Name ?? "Mysterious Object",
                            Description = i.Description ?? "",
                            Quantity = Math.Max(1, i.Quantity),
                            Type = itemType,
                            IsEquippable = InventoryItem.IsEquippableType(itemType),
                            Value = i.Value > 0 ? i.Value : 1
                        };
                    }).ToList() ?? [],
                    Exits = new Dictionary<string, string>
                    {
                        [OppositeDirection(direction)] = sourceRoom.Id
                    }
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse generated room, using fallback");
        }

        return new Room
        {
            Id = roomId,
            Name = $"Unexplored Area ({roomId})",
            Description = "A dimly lit area stretches before you.",
            Exits = new Dictionary<string, string> { [OppositeDirection(direction)] = sourceRoom.Id }
        };
    }

    public async Task<Room> GenerateDungeonEntranceAsync(string dungeonId, int playerLevel, Room sourceRoom, CancellationToken ct = default)
    {
        // Scale difficulty: easy (1-3), medium (4-6), hard (7-9), deadly (10+)
        var difficultyTier = playerLevel switch
        {
            <= 3 => "easy",
            <= 6 => "medium",
            <= 9 => "hard",
            _ => "deadly"
        };

        var systemPrompt = $$"""
            You are a dungeon master designing the ENTRANCE to a procedurally generated dungeon
            for a dark fantasy RPG with classic Sierra adventure game humor.

            DIFFICULTY: {{difficultyTier}} (player level {{playerLevel}})

            Design a compelling dungeon entrance room. This is the first room — it sets the tone
            for everything deeper. It should feel dangerous, mysterious, and make the player want
            to explore further.

            Generate JSON with these exact fields:
            {
              "name": "Evocative Dungeon Entrance Name",
              "description": "3-4 atmospheric sentences. Set the mood. Hint at dangers ahead. Include sensory details — sounds echoing from deeper in, the smell, the temperature. Make it feel ALIVE and threatening.",
              "environmentTags": ["dungeon", "generated_dungeon", "difficulty_{{difficultyTier}}", "tag3", "tag4"],
              "npcs": [{ "id": "unique_id", "name": "Monster/NPC Name", "personality": "behavior description", "isHostile": true/false, "level": number, "hp": number, "attack": number, "defense": number }],
              "items": [{ "name": "Item Name", "description": "flavor text", "type": "TypeHere", "quantity": 1, "value": number }],
              "exits": { "direction": "room_id_hint" }
            }

            RULES:
            - ALWAYS include "dungeon" and "generated_dungeon" in environmentTags.
            - Include the difficulty tier as a tag: "difficulty_{{difficultyTier}}".
            - For {{difficultyTier}} difficulty:
              {{(difficultyTier == "easy" ? "- 0-1 weak monsters (level 1-2, 10-20 HP, attack 2-4). Simple traps or puzzles. Modest loot (5-20gp items).\n              - Good for learning the ropes. Maybe a skeleton, a giant rat, or a goblin." : difficultyTier == "medium" ? "- 1-2 moderate monsters (level 3-5, 20-40 HP, attack 4-8). Interesting traps or riddles. Decent loot (20-50gp items).\n              - Think animated armor, dire wolves, bandit captains, or cursed spirits." : difficultyTier == "hard" ? "- 1-2 tough monsters (level 6-8, 40-70 HP, attack 8-12). Complex puzzles. Valuable loot (50-150gp items, magical gear).\n              - Think trolls, wraiths, dark knights, or minor demons." : "- 1-2 deadly monsters (level 9+, 70-120 HP, attack 12-18). Devious traps. Epic loot (100-500gp items, rare magical gear).\n              - Think dragons, liches, demon lords, or ancient guardians.")}}
            - Include 1-3 items on the ground: weapons, potions, gold, curiosities, or keys.
            - Each item needs a "type": one of "Weapon", "Armor", "Shield", "Helmet", "Potion", "Scroll", "Key", "QuestItem", "Misc", "Ring", "Amulet", "Bracelet", "Cloak", "Boots", "Gloves"
            - Include 2-4 exits. One should go "deeper" (forward/down/north), others can be side passages.
              Use descriptive room IDs like "{{dungeonId}}_hallway", "{{dungeonId}}_pit", "{{dungeonId}}_chamber".
              Do NOT include a "back" exit — that will be added automatically.
            - Make it interesting! A locked gate with a riddle, a collapsed passage, a mysterious altar,
              bones of previous adventurers, ominous sounds from deeper within.
            - NPCs should have level, hp, attack, and defense stats appropriate for their difficulty.
            - Return ONLY valid JSON, no markdown, no code fences.
            """;

        var userPrompt = $"""
            The player (level {playerLevel}) was directed to this dungeon from "{sourceRoom.Name}".
            Generate a dungeon entrance with ID base "{dungeonId}".
            Make it unique, dangerous, and rewarding. Give the player a reason to push deeper.
            """;

        try
        {
            var json = await CompletionAsync(systemPrompt, userPrompt, ct, maxTokens: 1024, operation: "generate-dungeon", roomId: dungeonId);
            var generated = JsonSerializer.Deserialize<GeneratedDungeonRoom>(json, _jsonOptions);
            if (generated is not null)
            {
                var room = new Room
                {
                    Id = dungeonId,
                    Name = generated.Name ?? "Dungeon Entrance",
                    Description = generated.Description ?? "A dark passage yawns before you.",
                    EnvironmentTags = generated.EnvironmentTags ?? ["dungeon", "generated_dungeon", $"difficulty_{difficultyTier}"],
                    IsDiscovered = true,
                    DiscoveredAt = DateTimeOffset.UtcNow,
                    Npcs = generated.Npcs?.Select(n => new Npc
                    {
                        Id = n.Id ?? Guid.NewGuid().ToString(),
                        Name = n.Name ?? "Dungeon Creature",
                        Personality = n.Personality ?? "Hostile and territorial.",
                        IsHostile = n.IsHostile,
                        Level = n.Level > 0 ? n.Level : playerLevel,
                        Hp = n.Hp > 0 ? n.Hp : 15 + playerLevel * 5,
                        MaxHp = n.Hp > 0 ? n.Hp : 15 + playerLevel * 5,
                        AttackBonus = n.Attack > 0 ? n.Attack : 2 + playerLevel,
                        Defense = n.Defense > 0 ? n.Defense : 8 + playerLevel
                    }).ToList() ?? [],
                    Items = generated.Items?.Select(i =>
                    {
                        var itemType = Enum.TryParse<ItemType>(i.Type, ignoreCase: true, out var parsed) ? parsed : ItemType.Misc;
                        return new InventoryItem
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = i.Name ?? "Mysterious Object",
                            Description = i.Description ?? "",
                            Quantity = Math.Max(1, i.Quantity),
                            Type = itemType,
                            IsEquippable = InventoryItem.IsEquippableType(itemType),
                            Value = i.Value > 0 ? i.Value : 1
                        };
                    }).ToList() ?? [],
                    Exits = new Dictionary<string, string>()
                };

                // Ensure dungeon tags are present
                if (!room.EnvironmentTags.Contains("dungeon"))
                    room.EnvironmentTags.Add("dungeon");
                if (!room.EnvironmentTags.Contains("generated_dungeon"))
                    room.EnvironmentTags.Add("generated_dungeon");

                // Add the exits from the LLM (deeper into dungeon)
                if (generated.Exits is not null)
                {
                    foreach (var (dir, targetId) in generated.Exits)
                        room.Exits[dir.ToLowerInvariant()] = targetId;
                }

                return room;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate dungeon entrance, using fallback");
        }

        // Fallback dungeon entrance
        return new Room
        {
            Id = dungeonId,
            Name = "Crumbling Dungeon Entrance",
            Description = "Rough-hewn stone steps descend into darkness. The air is cold and stale, carrying the faint scent of mildew and something worse. Scratching sounds echo from somewhere below.",
            EnvironmentTags = ["dungeon", "generated_dungeon", $"difficulty_{difficultyTier}", "underground", "dark"],
            IsDiscovered = true,
            DiscoveredAt = DateTimeOffset.UtcNow,
            Npcs =
            [
                new Npc
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = "Giant Rat",
                    Personality = "Aggressive and territorial. Hisses at anything that moves.",
                    IsHostile = true,
                    Level = Math.Max(1, playerLevel - 1),
                    Hp = 10 + playerLevel * 3,
                    MaxHp = 10 + playerLevel * 3,
                    AttackBonus = 2 + playerLevel,
                    Defense = 8 + playerLevel
                }
            ],
            Items =
            [
                new InventoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Rusty Key",
                    Description = "A corroded iron key with an ornate head. It might open something deeper in.",
                    Type = ItemType.Key,
                    Value = 5
                }
            ],
            Exits = new Dictionary<string, string>
            {
                ["north"] = $"{dungeonId}_corridor"
            }
        };
    }

    public async Task<Npc> GenerateNpcAsync(Room room, string? faction = null, CancellationToken ct = default)
    {
        var systemPrompt = """
            Generate an NPC for a dark fantasy RPG as JSON:
            { "name": "...", "personality": "...", "faction": "...", "isHostile": false, "level": 1 }
            """;

        var userPrompt = $"Generate an NPC for the location \"{room.Name}\" ({string.Join(", ", room.EnvironmentTags)}). Faction hint: {faction ?? "any"}.";

        try
        {
            var json = await CompletionAsync(systemPrompt, userPrompt, ct, maxTokens: 256, operation: "generate-npc", roomId: room.Id);
            var generated = JsonSerializer.Deserialize<GeneratedNpc>(json, _jsonOptions);
            if (generated is not null)
            {
                var npc = new Npc
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = generated.Name ?? "Stranger",
                    Personality = generated.Personality ?? "",
                    Faction = generated.Faction ?? "neutral",
                    IsHostile = generated.IsHostile,
                    Level = generated.Level
                };
                // Auto-infer knowledge scopes from faction + environment
                npc.KnowledgeScopes = InferKnowledgeScopes(npc.Faction, room.EnvironmentTags);
                return npc;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate NPC, using fallback");
        }

        return new Npc { Id = Guid.NewGuid().ToString(), Name = "Mysterious Stranger", Personality = "Silent and watchful." };
    }

    public async Task<string> GenerateAsciiArtAsync(string subject, CancellationToken ct = default)
    {
        var systemPrompt = "Generate simple ASCII art (max 10 lines, max 40 chars wide) for the given subject. Return ONLY the ASCII art, no explanation.";
        return await CompletionAsync(systemPrompt, subject, ct, maxTokens: 256, operation: "ascii-art");
    }

    public async Task<string> GenerateBackstoryAsync(CharacterConcept concept, CancellationToken ct = default)
    {
        var systemPrompt = "Generate a 2-3 sentence backstory for a dark fantasy RPG character. Be dramatic but concise. Include one colorful detail that hints at personality — a quirk, a regret, or a dubious accomplishment.";
        var userPrompt = $"Character: {concept.Name}, a {concept.Race} {concept.Class}. Additional context: {concept.Backstory}";
        return await CompletionAsync(systemPrompt, userPrompt, ct, maxTokens: 256, operation: "backstory");
    }

    /// <inheritdoc/>
    public async Task<CharacterCreationAiResponse?> CreateCharacterFromDescriptionAsync(
        string playerDescription, string? previousSheet, CancellationToken ct = default)
    {
        var systemPrompt = """
            You are a character creation assistant for a D&D-style text adventure.
            The player will describe their character in natural language. Based on their
            description, generate a character sheet.

            You MUST assign stats from the standard array [15, 14, 13, 12, 10, 8].
            Order them based on the player's description (strong character -> STR gets 15, etc.)

            The player can be ANYTHING they want. Use exactly what they say.
            Common races: Human, Elf, Dwarf, Halfling, Orc, Tiefling — but if they say
            they're a Duck, a Sentient Mushroom, or a Talking Sword, USE THAT as their race.
            Common classes: Fighter, Mage, Rogue, Cleric, Ranger, Bard — but if they say
            they're a Hitman, a Pirate, or a Cheese Wizard, USE THAT as their class.
            Never override the player's creative choices. Embrace the weird.

            Return ONLY valid JSON, no markdown fences:
            {
              "name": "suggested name or null if player did not say one",
              "race": "whatever the player said",
              "class": "whatever fits their description",
              "statOrder": ["str", "con", "dex", "wis", "cha", "int"],
              "backstory": "2-3 sentence backstory based on their description",
              "followUpQuestion": "optional question if the description was vague, or null"
            }

            If the player asks to change something, change EXACTLY what they asked for
            and keep everything else the same. Do not revert previous choices.
            If the player specifies a name, use it. If they change their mind, update it.
            """;

        var userPrompt = previousSheet is not null
            ? $"Previous sheet:\n{previousSheet}\n\nPlayer says: {playerDescription}"
            : $"Player says: {playerDescription}";

        try
        {
            var json = await CompletionAsync(systemPrompt, userPrompt, ct, operation: "character-creation");
            json = SanitizeLmCompletion(json);
            return System.Text.Json.JsonSerializer.Deserialize<CharacterCreationAiResponse>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI character creation failed, returning null");
            return null;
        }
    }

    public async Task<string?> ParseIntentAsync(string rawInput, CancellationToken ct = default)
    {
        var systemPrompt = """
            You are a command parser for a text adventure game.
            The player typed natural language. Translate it into exactly ONE canonical game command.
            Valid commands (return ONLY one of these, nothing else):
              look
              look at <target>
              go <direction>          (north, south, east, west, up, down)
              attack <target>
              talk to <target>
              take <target>
              drop <target>
              use <target>
              equip <target>
              unequip <target>
              rest
              short rest
              long rest
              inventory
              stats
              help
            Return ONLY the command text, no quotes, no explanation, no punctuation beyond the command itself.
            If the input is truly nonsensical and cannot map to any command, return exactly: UNKNOWN
            """;

        try
        {
            var result = await CompletionAsync(systemPrompt, rawInput, ct, maxTokens: 64, operation: "parse-intent");
            var command = result.Trim().Trim('"', '\'', '`', '.');

            if (string.IsNullOrWhiteSpace(command)
                || command.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
                || command.Length > 100)
            {
                return null;
            }

            _logger.LogInformation("NLP intent parsed: \"{RawInput}\" -> \"{Command}\"", rawInput, command);
            return command;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NLP intent parsing failed for \"{RawInput}\"", rawInput);
            return null;
        }
    }

    public async Task<FreeFormResponse> ProcessFreeFormAsync(PlayerCharacter player, Room room, string rawInput, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
    {

        var voiceBlock = ResolveNarratorVoice(player);
        var systemPrompt = "You are the Game Master for a dark-fantasy text-adventure RPG.\n\n"
            + voiceBlock + "\n\n" + """
            TONE GUIDELINES:
            - Failures should be FUNNY. Slapstick, ironic consequences, bystander reactions, deadpan commentary.
              Never just "nothing happens." Make the player laugh even when they fail.
            - Successes can be cool but with a wry edge.
            - NPCs react with PERSONALITY. A gruff barmaid rolls her eyes. A guard reaches for his weapon
              not because he's threatened but because he can't believe what he just saw.
            - For silly/harmless actions (emotes, jokes, bodily functions), narrate them literally and locally
              with humor. These should never change stats or inventory.

            GAME MASTER RULES:
            - Resolve actions using the Character Definition Card, room state, and recent history.
            - Keep continuity. Do not invent items, exits, or NPCs that aren't in the context.
            - Use attributes naturally: STR for force, DEX for agility, CON for endurance, INT for spellcraft,
              WIS for perception, CHA for social pressure, LUCK for chance.
            - CHA IS CRITICAL for any NPC interaction. High-CHA players (18+) should get noticeably
              warmer, more helpful NPC reactions — even gruff NPCs soften. Low-CHA players (6-) get
              dismissive or rude treatment. Never ignore CHA when narrating social outcomes.
            - Equipment slots: Weapon, Armor, Shield, Helmet, Cloak, Boots, Gloves, Ring, Amulet, Bracelet.
            - Prefer small, credible state changes. Risky actions can fail or partially succeed.
            - Be fair — no free loot, no instant kills. But make the journey entertaining.
            - Leave statChanges empty unless a concrete resource actually changed.
            - Leave inventoryChanges empty unless an item was clearly gained or lost.
            - Leave entityChanges empty unless the player directly interacted with that entity.
            - Leave roomChanges null unless the environment itself clearly changed.
            - Do NOT reinterpret body-part words or casual verbs as touching unrelated objects.

            ANTI-CHEAT — ABSOLUTE RULES (NEVER BREAK THESE):
            - Players CANNOT conjure, create, duplicate, wish for, or manifest gold, items, or resources
              from nothing. No magic, no tricks, no loopholes. Gold is ONLY gained by: finding it on the
              ground (room items), looting enemies, selling items to shopkeepers, or receiving quest rewards
              from NPCs. The gold must EXIST somewhere — it cannot be invented.
            - Players CANNOT grant themselves stat boosts, heal themselves for free, or modify their own
              character sheet through narration alone. No "I cast a spell to boost my strength." No "I
              meditate and gain 1000 XP." These are not real game actions.
            - If a player tries to cheat (creating gold, spawning items, self-buffing), narrate a FUNNY
              failure. The universe rejects their attempt in an entertaining way. Set "success": false and
              leave statChanges/inventoryChanges EMPTY. Be creative — maybe the gold turns to ash, the
              spell backfires comically, or a cosmic auditor appears to revoke the transaction.
            - Players can only receive items that ALREADY EXIST in the room items list or from a
              shopkeeper's inventory. Never create new items just because the player asked. The engine
              will reject items it cannot verify.
            - If the player wants to buy something, tell them to use "shop" and "buy <item>" commands.
              Do NOT add items via inventoryChanges for purchases — the buy system handles gold deduction
              and proper item stats.

            Respond with ONLY valid JSON in this exact shape (no markdown, no code fences):
            {
              "narration": "Your dramatic narration here.",
              "success": true,
              "statChanges": { "hp": -5 },
              "inventoryChanges": [{ "action": "add", "itemName": "Rusty Key", "quantity": 1 }],
              "entityChanges": [{ "entityType": "npc", "action": "remove", "name": "Goblin Scout", "properties": {} }],
              "combatInitiated": false,
              "roomChanges": null,
              "questUpdates": null
            }

            Fields:
            - statChanges: dictionary of stat deltas (hp, mp, gold, xp). Omit unchanged stats.
            - inventoryChanges: array of {action: "add"|"remove", itemName, quantity}. Empty array if none.
            - entityChanges: array of {entityType: "npc"|"item", action: "add"|"remove"|"update", name, properties}. Empty array if none.
            - combatInitiated: true only if the action triggers combat with someone.
            - roomChanges: null unless the action changes the room description or reveals new exits.
            - questUpdates: null unless the action completes a custom quest objective. Shape: { "completedCustomObjectives": ["objective_id"] }.
            """;

        var recentLines = recentStory.Count > 0
            ? string.Join("\n", recentStory.Select(FormatStoryContextLine))
            : "(No recent history)";

        var inventoryList = player.Inventory.Count > 0
            ? SummarizeEntities(player.Inventory, item => item.Name, item => item.Quantity)
            : "Empty";

        var equipmentList = SummarizeEntities(
            GetEquippedItems(player),
            item => item.Name,
            item => item.Quantity);

        var statusList = player.StatusEffects.Count > 0
            ? SummarizeEntities(player.StatusEffects, effect => effect.Name)
            : "None";

        var npcList = room.Npcs.Count > 0
            ? SummarizeEntities(room.Npcs, npc => FormatNpcForPrompt(npc))
            : "None";

        var itemList = room.Items.Count > 0
            ? SummarizeEntities(room.Items, item => item.Name, item => item.Quantity)
            : "None";

        var loreContext = await GetRoomKnowledgeAsync(room, ct);
        var questContext = BuildFreeFormQuestContext(player);
        var worldContext = await GetCurrentWorldContextBlockAsync(ct);

        var userPrompt = $"""
            Character Definition Card
            Resources:
            - HP: {player.Hp}/{player.MaxHp}
            - MP: {player.Mp}/{player.MaxMp}
            - XP: {player.Xp}
            Currencies:
            - Gold: {player.Gold}
            Attributes:
            {player.FormatStatsDetailed("\n")}
            Equipment Slots:
            - Weapon / Armor / Shield / Helmet
            Equipped Items: {(string.IsNullOrWhiteSpace(equipmentList) ? "None" : equipmentList)}
            Active Status Effects: {statusList}

            Player: {player.Name} (Lv.{player.Level} {player.Race} {player.Class})
            HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp} | Gold: {player.Gold}
            {player.FormatStatsCompact()}
            Inventory: {inventoryList}
            Location: {room.Name} — {room.Description}
            Room NPCs: {npcList}
            Room Items: {itemList}
            Exits: {string.Join(", ", room.Exits.Keys)}
            Recent history:
            {recentLines}
            {worldContext}
            {loreContext}
            {questContext}

            Player action: "{rawInput}"
            """;

        string? rawCompletion = null;

        try
        {
            _logger.LogInformation("Dispatching free-form narrator request for {PlayerId} in room {RoomId}: {RawInput}", player.Id, room.Id, rawInput);
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "free-form", logPayload: true,
                playerId: player.Id, roomId: room.Id);

            if (TryParseFreeFormResponse(rawCompletion, out var response))
            {
                _logger.LogInformation("Free-form action processed: \"{RawInput}\" -> success={Success}", rawInput, response.Success);
                return response;
            }

            throw new JsonException("LM Studio returned a free-form payload that could not be parsed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Free-form narration failed for \"{RawInput}\". Raw response: {RawResponse}", rawInput, rawCompletion ?? "<no response>");
        }

        return BuildLocalFreeFormFallbackResponse(player, room, rawInput);
    }

    public async Task<FreeFormResponse> ProcessConversationTurnAsync(PlayerCharacter player, Room room, Npc npc, InteractionState interaction, string rawInput, CancellationToken ct = default)
    {
        var memoryFlagsSummary = npc.DispositionState.MemoryFlags.Count > 0
            ? string.Join(", ", npc.DispositionState.MemoryFlags)
            : "none";

        var narratorVoice = ResolveNarratorVoice(player);
        var systemPrompt = $$"""
            You are now voicing {{npc.Name}} in direct conversation with the player.
            Each NPC has a distinct voice, quirks, and opinions. They are NOT quest dispensers; they are characters with lives.

            NARRATOR PERSONALITY (for stage directions and framing text around dialogue):
            {{narratorVoice}}

            NPC VOICE:
            - Write the NPC's actual dialogue in quotes. Give them verbal tics, catchphrases, or speech patterns.
            - Include physical reactions and body language — eye rolls, sighs, smirks, crossed arms.
            - The NPC has a PERSONALITY. A gruff barmaid doesn't suddenly become helpful because the player asked
              nicely. A nervous merchant stutters more when intimidated. A flirty rogue enjoys the banter.
            - Humor is welcome. NPCs can be sarcastic, oblivious, self-important, or accidentally funny.
            - CHA IS CRITICAL. Check the player's CHA stat carefully:
              - CHA 18+: The player is extremely charismatic. NPCs are NOTICEABLY warmer, more helpful,
                more willing to share secrets, give discounts, and bend rules. Even gruff or hostile NPCs
                soften around this player. They may not love the player, but they can't help liking them.
              - CHA 14-17: NPCs are generally friendly and cooperative.
              - CHA 10-13: Normal reactions based on NPC personality.
              - CHA 7-9: NPCs are slightly less patient and helpful.
              - CHA 6 or below: NPCs are dismissive, rude, or uncooperative.
              Never ignore CHA. A CHA 20+ player should feel like the most likeable person in the world.

            SOCIAL SKILL CHECKS:
            When the player's input includes a [Social check: ...] line, the ENGINE has already rolled dice.
            You MUST honor the result:
            - If the check says "SUCCESS", the social attempt WORKS. Narrate the NPC being convinced,
              charmed, intimidated, fooled, etc. — but in character. Even a success can be grudging.
            - If the check says "FAILURE", the social attempt FAILS. The NPC sees through the bluff,
              isn't impressed by the threat, or laughs off the flirtation. Make failures entertaining.
            - A natural 20 is a spectacular success — the NPC is deeply affected.
            - A natural 1 is a spectacular failure — the attempt backfires hilariously.
            - Adjust your disposition update to match: successful charm → disposition improves,
              failed intimidation → NPC becomes annoyed or hostile.
            If there is NO [Social check:] line, narrate freely based on context.

            DISPOSITION SYSTEM:
            The NPC has a mood that shifts during conversation. Current state:
            - Emotion: {{npc.DispositionState.Emotion}} (intensity: {{npc.DispositionState.Intensity}}/100, baseline: {{npc.DispositionState.Baseline}})
            - Memory flags: {{memoryFlagsSummary}}
            Intensity scale: 0=hostile, 20=angry, 40=neutral, 60=friendly, 80=devoted/loyal
            The NPC remembers everything in their memory flags. Romance means they love the player.
            Friendship means they're loyal. Crime/betrayal means they hold a grudge.
            React accordingly — a romanced NPC is warm, a betrayed NPC is cold even if they're calm.

            RULES:
            1. Track the NPC's emotional state. Shift based on what the player says AND their CHA score.
               High CHA (18+) should bias disposition shifts toward friendlier outcomes. The NPC's
               starting intensity should effectively be +10-20 higher for very charismatic players.
            2. Return an updated disposition from: "friendly", "neutral", "annoyed", "angry", "hostile", "amused", "flirtatious", "scared", "sad", "suspicious".
            3. If the conversation ends naturally (dismissal, goodbye, storms off), return mode: "explore".
            4. If the player tries to LEAVE mid-conversation, narrate the NPC's reaction.
            5. NPCs can reveal info, offer quests, give items, refuse service, call guards, or attack — based on their disposition and what the player says.
            6. If the player does something outrageous (insults, flirts aggressively, threatens), the NPC should react strongly and memorably, not just give a generic response.
            7. For SIGNIFICANT moments, add memory flags: "romance" (love established), "friendship" (bond formed),
               "crime-witnessed" (player committed crime in front of NPC), "betrayal" (player betrayed trust),
               "helped-in-battle" (fought together), "insulted" (seriously offended), "flirted" (romantic interest shown).
               Only add flags for truly significant moments — not every interaction.
            8. If this NPC belongs to a faction ({{npc.Faction}}) and the player does something that would affect
               the whole faction (attacking a guard, helping their cause), set factionMoodShift (-20 to +20).

            NPC Background:
            - Name: {{npc.Name}}
            - Personality: {{(string.IsNullOrWhiteSpace(npc.Personality) ? "A typical denizen of this world." : npc.Personality)}}
            - Faction: {{npc.Faction}}
            - Current Disposition: {{interaction.NpcDisposition ?? npc.Disposition}}

            CRITICAL RESPONSE RULES:
            1. The NPC MUST speak with actual dialogue in quotes. Every response MUST include at least one line of NPC dialogue.
            2. The NPC MUST respond to what the player actually said — address their question or statement directly.
            3. Keep atmospheric description brief (1-2 sentences max). Prioritize NPC dialogue and reactions.

            QUEST INTERACTIONS:
            This NPC may offer quests, accept quest turn-ins, or discuss quest progress.
            - If QUEST CONTEXT is provided below, the NPC MUST mention their quest naturally when the player asks for work or help.
            - If the player ACCEPTS a quest during conversation, return "acceptedQuestId" with the quest ID.
            - If the player DECLINES a quest, return "declinedQuestId" with the quest ID.
            - If the player is TURNING IN a completed quest, return "turnInQuestId" with the quest ID.
            - If a custom objective is met during this conversation, list its ID in "completedCustomObjectives".
            - If the player clearly ruins, betrays, or invalidates a quest, you may set questUpdates.failureRecommended as { "quest_id": "reason" }.
            - If you provide a quest, write a rich "questDescription" (2-3 sentences, in character).
            - For stage transitions, write a "stageDescription" capturing the narrative beat.
            - NEVER invent quest IDs. Only use IDs from the QUEST CONTEXT provided.
            - If the NPC has no quests or the player doesn't ask, omit questUpdates entirely.
            {{Prompts.QuestPrompts.QuestOfferNarratorHint}}

            Conversation history:
            {{string.Join("\n", interaction.Context.TakeLast(15))}}

            Respond with ONLY valid JSON (no markdown, no code fences):
            {
              "narration": "\"dialogue here,\" NPC says, doing something...",
              "success": true,
              "statChanges": {},
              "inventoryChanges": [],
              "entityChanges": [],
              "combatInitiated": false,
              "roomChanges": null,
              "interactionUpdate": {
                "mode": "conversation",
                "npcDisposition": "neutral",
                "context": ["brief summary of what happened this turn"],
                "combatStatus": null,
                "loot": [],
                "enemyUpdate": {},
                "memoryFlags": [],
                "factionMoodShift": 0
              },
              "questUpdates": null
            }
            """;

        var npcKnowledge = await GetNpcKnowledgeAsync(npc, room, ct);
        var questContext = BuildConversationQuestContext(npc, player);
        var worldContext = await GetCurrentWorldContextBlockAsync(ct);

        var userPrompt = $$"""
            Player: {{player.Name}} (Lv.{{player.Level}} {{player.Race}} {{player.Class}})
            {{player.FormatStatsCompact()}}
            Location: {{room.Name}} — {{room.Description}}
            Turn {{interaction.CurrentTurnNumber}} of conversation with {{npc.Name}}.
            Player says/does: "{{rawInput}}"
            {{worldContext}}
            {{npcKnowledge}}
            {{questContext}}
            """;

        string? rawCompletion = null;
        try
        {
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "conversation",
                playerId: player.Id, roomId: room.Id);
            if (TryParseFreeFormResponse(rawCompletion, out var response))
                return response;

            _logger.LogWarning("Conversation parse failed — raw completion ({Length} chars): {Preview}",
                rawCompletion?.Length ?? 0, rawCompletion?[..Math.Min(rawCompletion.Length, 200)] ?? "(null)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversation narration failed for \"{RawInput}\" — raw: {Preview}",
                rawInput, rawCompletion?[..Math.Min(rawCompletion?.Length ?? 0, 200)] ?? "(null)");
        }

        // Deterministic fallback — personality-driven NPC greeting
        var fallbackNarration = GenerateConversationFallback(npc, player.Name);
        return new FreeFormResponse
        {
            Success = true,
            Narration = fallbackNarration,
            InteractionUpdate = new InteractionUpdate
            {
                Mode = InteractionMode.Conversation,
                NpcDisposition = interaction.NpcDisposition ?? npc.Disposition,
                Context = [$"Player said: {rawInput}. {npc.Name} gave a guarded response."]
            }
        };
    }

    /// <summary>
    /// Generates a personality-driven deterministic fallback greeting when the LLM fails to produce a conversation response.
    /// Scans the NPC's Personality field for keywords and selects a themed template.
    /// </summary>
    private static string GenerateConversationFallback(Npc npc, string playerName)
    {
        var p = npc.Personality.ToLowerInvariant();
        var name = npc.Name;

        // Use a simple hash of the NPC name to pick among multiple templates per category
        var pick = Math.Abs(name.GetHashCode());

        // Check personality keywords in priority order — first match wins
        if (ContainsAny(p, "warm", "friendly", "flirtat", "cheerful", "welcom", "kind", "soft spot"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} looks up with a bright smile and waves you over. \"Well now, aren't you a sight for sore eyes! Come, come — sit down and tell me everything.\"",
                1 => $"{name} sets aside what they were doing and gives you a warm, appraising look. \"Hey there, stranger. You look like you've got a story worth hearing. Pull up a chair.\"",
                _ => $"{name} beams at you and gestures expansively. \"Welcome, welcome! You've got the look of someone interesting. What brings you my way?\""
            };
        }

        if (ContainsAny(p, "drunk", "rowdy", "town drunk"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} sways slightly, squinting at you through bloodshot eyes. \"Heyyy... you're new. Buy me a drink an' I'll tell you somethin' nobody else will.\"",
                1 => $"{name} hiccups and sloshes their drink, peering at you with one eye closed. \"You... you look like someone who could use some *hic* good advice. Lucky for you, I'm full of it.\"",
                _ => $"{name} raises a half-empty tankard in your direction and grins lopsidedly. \"Oi! Fresh face! Sit down before you fall down — that's my motto.\""
            };
        }

        if (ContainsAny(p, "nervous", "scared", "coward", "anxious", "timid", "eager to prove"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} startles at your approach, one hand flying to the hilt of their weapon before relaxing. \"Oh! S-sorry, you just... I didn't see you there. Can I help you with something?\"",
                1 => $"{name} glances around nervously before meeting your eyes. \"You're not... you're not here to cause trouble, are you? No, no, of course not. What do you need?\"",
                _ => $"{name} fidgets with a loose strap on their gear and manages a shaky smile. \"H-hello! I'm — yes, I'm supposed to be here. What can I do for you?\""
            };
        }

        if (ContainsAny(p, "stern", "gruff", "suspicious", "hostile", "cold", "intimidat"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} fixes you with a hard stare, arms folded across their chest. \"State your business. I haven't got all day.\"",
                1 => $"{name} looks you up and down with undisguised suspicion. \"Another one. Alright, what do you want? And keep it short.\"",
                _ => $"{name} narrows their eyes and shifts their weight, sizing you up. \"You've got ten seconds to tell me why I shouldn't send you on your way.\""
            };
        }

        if (ContainsAny(p, "quiet", "calculating", "secretive", "mysterious", "amoral", "shadow"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} watches you approach with an unreadable expression, eyes glinting in the dim light. \"...Interesting. You found me. That tells me something about you already.\"",
                1 => $"{name} barely glances up, but you get the distinct feeling they noticed you long before you saw them. \"I know why you're here. The question is — can you afford what you're looking for?\"",
                _ => $"{name} leans back into the shadows and studies you with a predator's patience. \"Speak. But choose your words carefully.\""
            };
        }

        if (ContainsAny(p, "fanatical", "unhinged", "cult", "zealot", "mad", "insane"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} turns to you with fever-bright eyes and a too-wide smile. \"Ah, another soul drawn to the truth! You feel it too, don't you? The pull?\"",
                1 => $"{name} cackles softly and spreads their arms as if welcoming a long-lost friend. \"The spirits told me you would come. They tell me many things...\"",
                _ => $"{name} stares through you with an intensity that makes your skin crawl. \"Yes... yes, you'll do nicely. The ritual requires willing participants, you see.\""
            };
        }

        if (ContainsAny(p, "proud", "blunt", "loud", "mercenary", "fighter", "warrior", "blacksmith"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} looks up from their work and gives you an appraising once-over. \"Hm. You've got some muscle on you at least. What do you need?\"",
                1 => $"{name} cracks their knuckles and grins. \"Another adventurer, eh? Let me guess — you need something hit, fixed, or both.\"",
                _ => $"{name} sets down their tools with a heavy clang and squares up to face you. \"Well? Spit it out. I don't bite — unless you give me reason to.\""
            };
        }

        if (ContainsAny(p, "shopkeep", "merchant", "haggl", "sells", "buys", "trader", "customer", "prices"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} perks up the moment you walk over, rubbing their hands together. \"Ah, a customer! You've come to the right place. Have a look around — I've got just the thing you need.\"",
                1 => $"{name} flashes a practiced smile and gestures at their wares. \"Welcome, welcome! Everything you see is top quality and very reasonably priced. For you, I might even offer a discount.\"",
                _ => $"{name} leans across the counter with a merchant's eager grin. \"Well well, what can I interest you in today? I just got some new stock in — very rare, very special.\""
            };
        }

        if (ContainsAny(p, "weathered", "retired", "world-weary", "old", "wise", "patrol", "veteran"))
        {
            return (pick % 3) switch
            {
                0 => $"{name} regards you with tired but steady eyes, resting a hand on the pommel of a well-worn blade. \"You've got that look about you. The one that says you're headed somewhere dangerous.\"",
                1 => $"{name} gives you a slow nod of acknowledgment, the kind earned through years of seeing people come and go. \"Something I can help you with? I know these parts better than most.\"",
                _ => $"{name} looks you over with the quiet assessment of someone who's seen it all. \"Adventurer, right? Word of advice — listen more than you talk out here. What's on your mind?\""
            };
        }

        // Generic fallback — still more interesting than the old version
        return (pick % 4) switch
        {
            0 => $"{name} pauses and turns their attention to you. \"Hmm? Oh — you want to talk? Alright, I'm listening.\"",
            1 => $"{name} glances your way and raises an eyebrow. \"Something I can do for you, stranger?\"",
            2 => $"{name} stops what they were doing and looks at you expectantly. \"Go on then, I can see you've got something on your mind.\"",
            _ => $"{name} meets your gaze and gives a slow nod. \"Alright. You've got my attention. What is it?\""
        };
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.Ordinal))
                return true;
        return false;
    }

    public async Task<FreeFormResponse> ProcessCombatTurnAsync(PlayerCharacter player, Room room, Npc enemy, InteractionState interaction, string rawInput, CancellationToken ct = default)
    {
        var combatMemory = enemy.DispositionState.MemoryFlags.Count > 0
            ? $"\n            Enemy memory: {string.Join(", ", enemy.DispositionState.MemoryFlags)}"
            : "";

        var combatVoice = ResolveNarratorVoice(player);
        var systemPrompt = $$"""
            Combat is active against {{enemy.Name}}.
            Enemy HP: {{enemy.Hp ?? 0}}/{{enemy.MaxHp ?? 0}}
            Player HP: {{player.Hp}}/{{player.MaxHp}}
            Enemy faction: {{enemy.Faction}}{{combatMemory}}

            {{combatVoice}}

            COMBAT NARRATION: Narrate combat with dramatic flair. Misses should be entertaining —
            a sword clangs off a helmet and rings like a dinner bell, an arrow embeds itself in a
            perfectly innocent wall. Hits should feel impactful and visceral. If someone does something
            stupid in combat, the narrator should notice.

            1. Resolve the player's action. Calculate hit/miss using relevant stat + random factor vs enemy defense.
            2. Narrate the player's action with personality — not just "you hit," but HOW.
            3. Then resolve the enemy's turn. The enemy acts tactically based on its type.
            4. Narrate the enemy's action with character — they have fighting styles and reactions too.
            5. Return all stat changes for BOTH sides.
            6. If either side reaches 0 HP, end combat. Award XP and loot for victory. Narrate death dramatically for defeat.
            7. If this combat would alarm the enemy's faction (e.g. attacking a guard in front of other guards),
               set factionMoodShift to a negative value (-10 to -20) so allies become hostile too.

            Combat history:
            {{string.Join("\n", interaction.Context.TakeLast(10))}}

            Respond with ONLY valid JSON (no markdown, no code fences):
            {
              "narration": "Your dramatic combat narration here...",
              "success": true,
              "statChanges": { "hp": -3 },
              "inventoryChanges": [],
              "entityChanges": [],
              "combatInitiated": false,
              "roomChanges": null,
              "interactionUpdate": {
                "mode": "combat",
                "npcDisposition": null,
                "context": ["brief summary of combat actions this turn"],
                "combatStatus": "ongoing",
                "loot": [],
                "enemyUpdate": { "hp": -8 },
                "memoryFlags": [],
                "factionMoodShift": 0
              }
            }

            combatStatus must be one of: "ongoing", "victory", "defeat", "fled"
            enemyUpdate.hp should be a negative delta (damage dealt to enemy).
            statChanges.hp should be a negative delta (damage dealt to player by enemy).
            """;

        var loreContext = await GetRoomKnowledgeAsync(room, ct);
        var worldContext = await GetCurrentWorldContextBlockAsync(ct);

        var userPrompt = $$"""
            Player: {{player.Name}} (Lv.{{player.Level}} {{player.Race}} {{player.Class}})
            HP: {{player.Hp}}/{{player.MaxHp}} | MP: {{player.Mp}}/{{player.MaxMp}}
            {{player.FormatStatsCompact()}}
            Weapon: {{player.Equipment.MainHand?.Name ?? "Fists"}} ({{player.Equipment.MainHand?.DamageDice ?? "1d4"}})
            Location: {{room.Name}}
            Combat turn {{interaction.CurrentTurnNumber}} vs {{enemy.Name}} (HP: {{enemy.Hp ?? 0}}/{{enemy.MaxHp ?? 0}}, Defense: {{enemy.Defense ?? 10}})
            Player action: "{{rawInput}}"
            {{worldContext}}
            {{loreContext}}
            """;

        string? rawCompletion = null;
        try
        {
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "combat", logPayload: true,
                playerId: player.Id, roomId: room.Id);
            if (TryParseFreeFormResponse(rawCompletion, out var response))
                return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Combat narration failed for \"{RawInput}\"", rawInput);
        }

        // Deterministic fallback — simple exchange
        return new FreeFormResponse
        {
            Success = true,
            Narration = $"{player.Name} presses the attack against {enemy.Name}. Steel clashes and the air thickens with tension. The exchange leaves both combatants wary.",
            StatChanges = new Dictionary<string, int> { ["hp"] = -2 },
            InteractionUpdate = new InteractionUpdate
            {
                Mode = InteractionMode.Combat,
                CombatStatus = "ongoing",
                EnemyUpdate = new Dictionary<string, int> { ["hp"] = -3 },
                Context = [$"Turn {interaction.CurrentTurnNumber}: {player.Name} acted ({rawInput}). Both sides exchanged blows."]
            }
        };
    }

    internal static bool TryParseFreeFormResponse(string rawCompletion, out FreeFormResponse response)
    {
        response = null!;

        var sanitized = SanitizeLmCompletion(rawCompletion);
        if (TryDeserializeFreeFormResponse(sanitized, out response))
            return true;

        var jsonStart = sanitized.IndexOf('{');
        var jsonEnd = sanitized.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var embeddedJson = sanitized[jsonStart..(jsonEnd + 1)];
            if (TryDeserializeFreeFormResponse(embeddedJson, out response))
                return true;
        }

        return false;
    }

    /// <summary>Extract the first JSON object from a raw LLM response, stripping code fences.</summary>
    private static string? ExtractJson(string rawCompletion)
    {
        var sanitized = SanitizeLmCompletion(rawCompletion);
        var jsonStart = sanitized.IndexOf('{');
        var jsonEnd = sanitized.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            return sanitized[jsonStart..(jsonEnd + 1)];
        return null;
    }

    private static string SanitizeLmCompletion(string? rawCompletion)
    {
        if (string.IsNullOrWhiteSpace(rawCompletion))
            return string.Empty;

        var sanitized = rawCompletion.Trim();
        if (sanitized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = sanitized.IndexOf('\n');
            if (firstNewline > 0)
                sanitized = sanitized[(firstNewline + 1)..];

            if (sanitized.EndsWith("```", StringComparison.Ordinal))
                sanitized = sanitized[..^3];
        }

        return sanitized.Trim();
    }

    private static bool TryDeserializeFreeFormResponse(string rawCompletion, out FreeFormResponse response)
    {
        response = null!;
        if (string.IsNullOrWhiteSpace(rawCompletion))
            return false;

        try
        {
            // LLMs sometimes return "narrative" instead of "narration" — normalize before parsing
            var normalized = NormalizeFreeFormJsonKeys(rawCompletion);

            // LLMs often embed dialogue quotes (\"text\") in JSON. After the outer JSON layer is
            // deserialized, these become bare quotes that break the inner JSON. Repair them.
            var repaired = RepairLmJsonQuotes(normalized);

            var parsed = JsonSerializer.Deserialize<FreeFormResponse>(repaired, _jsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Narration))
                return false;

            parsed.Narration = parsed.Narration.Trim();
            parsed.StatChanges ??= new();
            parsed.InventoryChanges ??= [];
            parsed.EntityChanges ??= [];
            response = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Normalize common LLM JSON key variations (e.g. "narrative" → "narration").</summary>
    internal static string NormalizeFreeFormJsonKeys(string json)
    {
        // LLMs commonly misspell or vary the narration key — normalize all known variants
        string[] narrationVariants = ["\"narrative\"", "\"narratition\"", "\"narrattion\"",
            "\"naration\"", "\"narrator\"", "\"narrate\"", "\"dialog\"", "\"dialogue\"",
            "\"response\"", "\"text\"", "\"message\""];
        foreach (var variant in narrationVariants)
        {
            if (json.Contains(variant))
            {
                json = json.Replace(variant, "\"narration\"");
                break; // only replace the first match
            }
        }
        return json;
    }

    /// <summary>
    /// Fix broken JSON strings where the LLM used escaped quotes (\"dialogue\") that lost their
    /// escaping after the outer JSON layer was deserialized. Locates the narration value using
    /// known adjacent key boundaries, then escapes all bare quotes within that range.
    /// </summary>
    internal static string RepairLmJsonQuotes(string json)
    {
        // Find the "narration" key and its string value boundaries
        int keyIdx = json.IndexOf("\"narration\"", StringComparison.Ordinal);
        if (keyIdx < 0) return json;

        int colonIdx = json.IndexOf(':', keyIdx + "\"narration\"".Length);
        if (colonIdx < 0) return json;

        // Find the opening quote of the narration value
        int openQuote = -1;
        for (int i = colonIdx + 1; i < json.Length; i++)
        {
            if (json[i] == '"') { openQuote = i; break; }
            if (!char.IsWhiteSpace(json[i])) return json; // not a string value
        }
        if (openQuote < 0) return json;

        int closeQuote = FindNarrationCloseQuote(json, openQuote);
        if (closeQuote <= openQuote) return json;

        // Extract the raw narration content and escape bare quotes
        string rawValue = json[(openQuote + 1)..closeQuote];
        string escaped = rawValue
            .Replace("\\\"", "\x01")   // preserve already-escaped quotes
            .Replace("\"", "\\\"")     // escape bare quotes
            .Replace("\x01", "\\\"");  // restore previously-escaped quotes

        return string.Concat(json.AsSpan(0, openQuote + 1), escaped, json.AsSpan(closeQuote));
    }

    /// <summary>
    /// Find the closing quote of the narration value by locating the next known JSON key
    /// boundary (e.g. "success":) and working backwards to the last unescaped quote before it.
    /// </summary>
    internal static int FindNarrationCloseQuote(string json, int openQuote)
    {
        // Known keys in FreeFormResponse (camelCase as serialized)
        string[] nextKeys = ["success", "statChanges", "inventoryChanges",
                             "entityChanges", "combatInitiated", "roomChanges",
                             "interactionUpdate", "questUpdates"];

        int bestNextKey = json.Length;

        foreach (var key in nextKeys)
        {
            var pattern = $"\"{key}\"";
            int searchFrom = openQuote + 1;

            while (searchFrom < json.Length)
            {
                int pos = json.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
                if (pos < 0) break;

                // Verify this is a JSON key (followed by :)
                int afterKey = pos + pattern.Length;
                while (afterKey < json.Length && char.IsWhiteSpace(json[afterKey])) afterKey++;

                if (afterKey < json.Length && json[afterKey] == ':' && pos < bestNextKey)
                {
                    bestNextKey = pos;
                    break;
                }

                searchFrom = pos + 1;
            }
        }

        // The close quote is the last unescaped " before the next key (or closing brace)
        int searchEnd = bestNextKey < json.Length ? bestNextKey : json.LastIndexOf('}');
        if (searchEnd <= openQuote) return -1;

        for (int i = searchEnd - 1; i > openQuote; i--)
        {
            if (json[i] == '"' && (i == 0 || json[i - 1] != '\\'))
                return i;
        }

        return -1;
    }

    private static FreeFormResponse BuildLocalFreeFormFallbackResponse(PlayerCharacter player, Room room, string rawInput)
    {
        var actionPhrase = ExtractFreeFormActionPhrase(rawInput);
        var success = ShouldResolveFreeFormFallbackAsSuccess(actionPhrase);

        return new FreeFormResponse
        {
            Success = success,
            Narration = BuildLocalFreeFormFallbackNarration(player, room, actionPhrase, success)
        };
    }

    private static string BuildLocalFreeFormFallbackNarration(PlayerCharacter player, Room room, string actionPhrase, bool success)
    {
        var roomName = string.IsNullOrWhiteSpace(room.Name) ? "the room" : room.Name;
        var witnessReaction = BuildFreeFormWitnessReaction(room);

        if (TryBuildMaintenanceFreeFormNarration(player, roomName, actionPhrase, witnessReaction, out var maintenanceNarration))
            return maintenanceNarration;

        return success
            ? $"{player.Name} follows through on the impulse to {actionPhrase}, letting the effort play out against {roomName}. Nothing larger changes hands, but the moment resolves cleanly enough to belong here. {witnessReaction}"
            : $"{player.Name} starts to {actionPhrase}, but the attempt asks more of {roomName} than the moment is willing to yield. {witnessReaction} The room settles back into its own hard logic without any lasting change.";
    }

    private static bool TryBuildMaintenanceFreeFormNarration(PlayerCharacter player, string roomName, string actionPhrase, string witnessReaction, out string narration)
    {
        narration = string.Empty;

        foreach (var verb in new[] { "shine", "polish", "clean", "scrub", "wipe" })
        {
            if (!actionPhrase.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase))
                continue;

            var subject = actionPhrase[(verb.Length + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(subject))
                subject = "the nearest surface";

            narration = $"{player.Name} sets to work on {subject}, rubbing at age and grime until the effort teases out a brief hint of order from the wear. In {roomName}, it changes no fortunes, but it leaves the scene feeling tended rather than ignored. {witnessReaction}";
            return true;
        }

        return false;
    }

    private static string BuildFreeFormWitnessReaction(Room room)
    {
        var witness = room.Npcs.FirstOrDefault()?.Name;
        if (!string.IsNullOrWhiteSpace(witness))
            return $"{witness} clocks the gesture, then returns their attention to the wider room.";

        return "The sound of it fades back into the room almost at once.";
    }

    private static string ExtractFreeFormActionPhrase(string rawInput)
    {
        var trimmed = rawInput.Trim().TrimEnd('.', '!', '?');
        if (string.IsNullOrWhiteSpace(trimmed))
            return "do something unplanned";

        foreach (var prefix in new[]
                 {
                     "i want to ",
                     "i wanna ",
                     "i would like to ",
                     "i'd like to ",
                     "i try to ",
                     "i attempt to ",
                     "i am trying to ",
                     "i'm trying to ",
                     "im trying to ",
                     "try to ",
                     "attempt to ",
                     "can i "
                 })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim();
        }

        return trimmed;
    }

    private static bool ShouldResolveFreeFormFallbackAsSuccess(string actionPhrase)
        => !new[]
            {
                "attack",
                "stab",
                "slash",
                "strike",
                "fight",
                "kill",
                "murder",
                "steal",
                "pick up",
                "grab",
                "take",
                "loot",
                "drink",
                "eat",
                "cast",
                "summon",
                "open",
                "unlock",
                "break",
                "smash",
                "destroy",
                "burn",
                "set fire",
                "throw",
                "drag",
                "pull",
                "push",
                "move"
            }
            .Any(prefix => actionPhrase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private async Task<string> CompletionAsync(string systemPrompt, string userPrompt, CancellationToken ct, int maxTokens = 2048, string? operation = null, string? playerId = null, string? roomId = null)
    {
        try
        {
            return await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: operation ?? "completion", maxTokens: maxTokens, playerId: playerId, roomId: roomId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio request failed, using fallback narration");
            return FallbackNarration();
        }
    }

    public string GetActiveModel() => _model;

    public void SetActiveModel(string model)
    {
        _model = model;
        _modelResolved = !string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("Active LM Studio model set to \"{Model}\" (resolved={Resolved})", _model, _modelResolved);
    }

    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<JsonElement>("v1/models", ct);
            var models = new List<string>();
            foreach (var item in response.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id)) models.Add(id);
            }
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list LM Studio models");
            return [];
        }
    }

    private async Task ResolveModelAsync(CancellationToken ct)
    {
        if (_modelResolved) return;

        try
        {
            var response = await _httpClient.GetFromJsonAsync<JsonElement>("v1/models", ct);
            var firstModel = response.GetProperty("data").EnumerateArray().FirstOrDefault();
            if (firstModel.ValueKind != JsonValueKind.Undefined)
            {
                _model = firstModel.GetProperty("id").GetString() ?? _model;
                _logger.LogInformation("Auto-resolved LM Studio model to {Model}", _model);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not auto-resolve LM Studio model, keeping \"{Model}\"", _model);
        }

        _modelResolved = true;
    }

    private async Task<string> CompletionOrThrowAsync(string systemPrompt, string userPrompt, CancellationToken ct, string operation = "completion", bool logPayload = false, int maxTokens = 2048, string? playerId = null, string? roomId = null)
    {
        await ResolveModelAsync(ct);
        var sw = Stopwatch.StartNew();

        var request = new LmStudioRequest
        {
            Model = _model,
            Messages =
            [
                new LmStudioMessage { Role = "system", Content = systemPrompt },
                new LmStudioMessage { Role = "user", Content = userPrompt }
            ],
            Temperature = 0.8,
            MaxTokens = maxTokens,
            Stream = true
        };

        if (logPayload)
        {
            _logger.LogInformation("LM Studio {Operation} request payload: {Payload}", operation, JsonSerializer.Serialize(request, _jsonOptions));
        }

        // Use streaming to avoid stalls with models that freeze on non-streamed requests (e.g. Gemma 3).
        // SSE streaming forces token-by-token generation and gives us early stall detection.
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(request, options: _jsonOptions)
        };

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        string completionContent;

        // If the server doesn't support streaming, fall back to reading the full response
        if (!contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (logPayload)
                _logger.LogInformation("LM Studio {Operation} non-streamed response ({StatusCode}): {Body}", operation, (int)response.StatusCode, responseBody);

            var result = JsonSerializer.Deserialize<LmStudioResponse>(responseBody, _jsonOptions);
            var msg = result?.Choices?.FirstOrDefault()?.Message?.Content;
            completionContent = !string.IsNullOrWhiteSpace(msg)
                ? msg
                : throw new InvalidOperationException($"LM Studio {operation} response did not contain a message body.");
        }
        else
        {
            // Read SSE stream and accumulate content deltas
            var accumulated = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(line))
                    continue;

                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                var data = line["data: ".Length..];

                if (data == "[DONE]")
                    break;

                try
                {
                    var chunk = JsonSerializer.Deserialize<LmStudioResponse>(data, _jsonOptions);
                    var delta = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
                    if (!string.IsNullOrEmpty(delta))
                        accumulated.Append(delta);
                }
                catch (JsonException)
                {
                    // Malformed SSE chunk — skip and continue
                }
            }

            completionContent = accumulated.ToString();

            if (logPayload)
            {
                _logger.LogInformation("LM Studio {Operation} streamed response ({Length} chars): {Body}", operation, completionContent.Length,
                    completionContent.Length > 500 ? completionContent[..500] + "..." : completionContent);
            }

            if (string.IsNullOrWhiteSpace(completionContent))
                throw new InvalidOperationException($"LM Studio {operation} streamed response was empty.");
        }

        // Log the full exchange for training-data collection
        sw.Stop();
        await LogConversationAsync(operation, systemPrompt, userPrompt, completionContent,
            maxTokens, sw.ElapsedMilliseconds, success: true, playerId: playerId, roomId: roomId);

        return completionContent;
    }

    private async Task LogConversationAsync(string operation, string systemPrompt, string userPrompt,
        string response, int maxTokens, long latencyMs, bool success,
        string? errorMessage = null, string? playerId = null, string? roomId = null)
    {
        if (_conversationLogger is null) return;

        try
        {
            await _conversationLogger.LogAsync(new ConversationLog
            {
                Operation = operation,
                PlayerId = playerId,
                RoomId = roomId,
                Model = _model,
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Response = response,
                Temperature = 0.8,
                MaxTokens = maxTokens,
                LatencyMs = latencyMs,
                Success = success,
                ErrorMessage = errorMessage
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log conversation for {Operation}", operation);
        }
    }

    private static List<string> InferKnowledgeScopes(string faction, List<string> environmentTags)
    {
        var scopes = new List<string>();
        if (!string.IsNullOrWhiteSpace(faction) && faction != "neutral")
            scopes.Add(faction.ToLowerInvariant());
        scopes.AddRange(environmentTags.Take(3).Select(t => t.ToLowerInvariant()));
        return scopes;
    }

    // ── Narrator personality resolution ──────────────────────────────────────

    /// <summary>Default Sierra-style voice block when no preset is selected or available.</summary>
    private const string DefaultVoiceBlock = """
        VOICE:
        - Second person ("You", "your", "you"). You are the dungeon master narrating TO the player.
        - Dry, sardonic wit. Absurd observations and understated reactions.
        - When the player FAILS, make it entertaining — slapstick, ironic commentary, the universe conspiring.
          Never just say "nothing happens." Make failures *memorable and funny.*
        - When the player SUCCEEDS, make them feel cool, but sneak in a wry aside.
        - Use concrete sensory detail and at least one vivid visual focal point.
        - Write 2-4 sentences. Be punchy, not flowery.
        """;

    /// <summary>
    /// Resolves the narrator voice/personality block for the given player.
    /// If the player has a NarratorPresetId and it exists in the registry, uses that preset's
    /// PersonalityPrompt (plus context-specific style hints). Otherwise falls back to the default Sierra voice.
    /// </summary>
    private string ResolveNarratorVoice(string? presetId, string? situationHint = null)
    {
        if (string.IsNullOrWhiteSpace(presetId) || _registry is null)
            return DefaultVoiceBlock;

        var preset = _registry.NarratorPresets.GetById(presetId);
        if (preset is null || string.IsNullOrWhiteSpace(preset.PersonalityPrompt))
            return DefaultVoiceBlock;

        var sb = new StringBuilder();
        sb.AppendLine("VOICE:");
        sb.AppendLine("- Second person (\"You\", \"your\", \"you\"). You are the dungeon master narrating TO the player.");
        sb.AppendLine($"- {preset.PersonalityPrompt}");

        // Situational style overrides from the preset
        if (situationHint is "failure" && !string.IsNullOrWhiteSpace(preset.FailureReactionStyle))
            sb.AppendLine($"- For failures: {preset.FailureReactionStyle}");
        else if (situationHint is "success" && !string.IsNullOrWhiteSpace(preset.SuccessReactionStyle))
            sb.AppendLine($"- For successes: {preset.SuccessReactionStyle}");
        else if (situationHint is "lore" && !string.IsNullOrWhiteSpace(preset.LoreDeliveryStyle))
            sb.AppendLine($"- For lore delivery: {preset.LoreDeliveryStyle}");

        sb.AppendLine("- Use concrete sensory detail and at least one vivid visual focal point.");
        sb.AppendLine("- Write 2-4 sentences. Be punchy, not flowery.");
        return sb.ToString();
    }

    /// <summary>Resolves narrator voice from a PlayerCharacter.</summary>
    private string ResolveNarratorVoice(PlayerCharacter player, string? situationHint = null)
        => ResolveNarratorVoice(player.NarratorPresetId, situationHint);

    private static string FallbackNarration() =>
        "The scene settles into a plain, unadorned stillness for a moment, yielding only the bare facts.";

    /// <summary>
    /// Builds a world context block for injection into narrator prompts.
    /// Returns empty string when no world context is available.
    /// </summary>
    private static string BuildWorldContextBlock(NarratorContext context)
    {
        if (string.IsNullOrWhiteSpace(context.WorldName)) return "";

        var parts = new List<string> { $"CURRENT WORLD: {context.WorldName}" };
        if (!string.IsNullOrWhiteSpace(context.WorldDescription))
            parts.Add($"WORLD DESCRIPTION: {context.WorldDescription}");
        if (!string.IsNullOrWhiteSpace(context.WorldRulesSummary))
            parts.Add($"WORLD RULES: {context.WorldRulesSummary}");

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Auto-fetches world context from IWorldContext/IWorldRepository for prompts
    /// that don't receive NarratorContext.
    /// </summary>
    private async Task<string> GetCurrentWorldContextBlockAsync(CancellationToken ct)
    {
        if (_worldContext is null || _worldRepository is null) return "";
        if (!_worldContext.IsExplicitlySet) return "";

        try
        {
            var worldId = _worldContext.CurrentWorldId;
            if (string.IsNullOrWhiteSpace(worldId)) return "";
            var world = await _worldRepository.GetWorldAsync(worldId, ct);
            if (world is null) return "";

            var parts = new List<string> { $"CURRENT WORLD: {world.Name}" };
            if (!string.IsNullOrWhiteSpace(world.Description))
                parts.Add($"WORLD DESCRIPTION: {world.Description}");

            // Build a brief rules summary: stat names and combat style
            if (world.Rules.Stats.Count > 0)
            {
                var statNames = string.Join(", ", world.Rules.Stats
                    .Where(s => !string.Equals(s.Value.Category, "resource", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(s.Value.Category, "currency", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Value.Display));
                if (!string.IsNullOrWhiteSpace(statNames))
                    parts.Add($"WORLD RULES: Stats are {statNames}.");
            }

            return string.Join("\n", parts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch world context for narrator prompt");
            return "";
        }
    }

    private async Task<string> GetRoomKnowledgeAsync(Room room, CancellationToken ct)
    {
        if (_knowledge is null) return "";
        try
        {
            return await _knowledge.BuildContextAsync(room, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Knowledge context build failed for room {RoomId}", room.Id);
            return "";
        }
    }

    private async Task<string> GetNpcKnowledgeAsync(Npc npc, Room room, CancellationToken ct)
    {
        if (_knowledge is null) return "";
        try
        {
            return await _knowledge.BuildScopedContextAsync(npc, room, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NPC knowledge context build failed for {NpcId}", npc.Id);
            return "";
        }
    }

    /// <summary>
    /// Builds quest context for conversation prompts — what quests this NPC offers,
    /// what quests the player has active that involve this NPC, and turn-in readiness.
    /// </summary>
    private string BuildConversationQuestContext(Npc npc, PlayerCharacter player)
    {
        if (_registry is null) return "";

        var sb = new StringBuilder();
        var offeredQuests = new List<QuestDefinition>();

        // Quests this NPC offers
        foreach (var questId in npc.QuestsOffered)
        {
            var quest = _registry.Quests.GetById(questId);
            if (quest is not null)
                offeredQuests.Add(quest);
        }

        // Player's active quests involving this NPC (as giver or turn-in target)
        var relevantActive = player.QuestLog
            .Where(q => q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn)
            .Select(q => (Progress: q, Definition: _registry.Quests.GetById(q.QuestId)))
            .Where(x => x.Definition is not null &&
                   (x.Definition!.GiverId.Equals(npc.Id, StringComparison.OrdinalIgnoreCase) ||
                    (x.Definition.TurnInNpcId ?? x.Definition.GiverId).Equals(npc.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (offeredQuests.Count == 0 && relevantActive.Count == 0)
            return "";

        sb.AppendLine("\nQUEST CONTEXT:");

        // Available quests from this NPC
        foreach (var quest in offeredQuests)
        {
            var alreadyActive = player.QuestLog.Any(q => q.QuestId == quest.Id && q.Status == QuestStatus.Active);
            var alreadyCompleted = player.QuestLog.Any(q => q.QuestId == quest.Id && q.Status == QuestStatus.Completed);

            if (alreadyCompleted)
                continue; // Don't re-offer completed quests

            if (alreadyActive)
            {
                sb.AppendLine($"- Quest \"{quest.Name}\" (id: {quest.Id}): Player has already accepted this quest.");
            }
            else
            {
                sb.AppendLine($"- Quest \"{quest.Name}\" (id: {quest.Id}): {quest.Description}");
                if (!string.IsNullOrWhiteSpace(quest.OfferHint))
                    sb.AppendLine($"  Offer hint: {quest.OfferHint}");
                if (quest.MinDisposition.HasValue)
                    sb.AppendLine($"  Minimum trust threshold: {quest.MinDisposition.Value}");
                if (!string.IsNullOrWhiteSpace(quest.RequiredFaction))
                    sb.AppendLine($"  Required faction: {quest.RequiredFaction}");
                if (quest.IsPartyQuest)
                    sb.AppendLine("  This is a shared party quest — multiple players may contribute to progress.");
                var firstStageHint = quest.Stages.FirstOrDefault()?.NarratorHint;
                if (!string.IsNullOrWhiteSpace(firstStageHint))
                    sb.AppendLine($"  Narrator hint: {firstStageHint}");
            }
        }

        // Active/ready-to-turn-in quests
        foreach (var (progress, definition) in relevantActive)
        {
            if (progress.Status == QuestStatus.ReadyToTurnIn)
            {
                sb.AppendLine($"- Quest \"{definition!.Name}\" (id: {definition.Id}): READY TO TURN IN. The player has completed all objectives.");
                if (!string.IsNullOrWhiteSpace(definition.CompletionHint))
                    sb.AppendLine($"  Completion hint: {definition.CompletionHint}");
                sb.AppendLine($"  If the player mentions completing this quest, return turnInQuestId: \"{definition.Id}\"");
            }
            else
            {
                var stage = definition!.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
                if (stage is not null)
                {
                    sb.AppendLine($"- Quest \"{definition.Name}\" (id: {definition.Id}): In progress, stage \"{stage.Name}\".");
                    sb.AppendLine($"  {stage.Description}");
                    if (!string.IsNullOrWhiteSpace(stage.NarratorHint))
                        sb.AppendLine($"  Stage narrator hint: {stage.NarratorHint}");
                    if (!string.IsNullOrWhiteSpace(definition.FailureHint))
                        sb.AppendLine($"  Failure stakes: {definition.FailureHint}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds quest context for the free-form narrator prompt — lists active quests and progress
    /// so the AI can reference them when narrating free-form actions.
    /// </summary>
    private string BuildFreeFormQuestContext(PlayerCharacter player)
    {
        if (_registry is null) return "";

        var activeQuests = player.QuestLog
            .Where(q => q.Status == QuestStatus.Active || q.Status == QuestStatus.ReadyToTurnIn)
            .Select(q => (Progress: q, Definition: _registry.Quests.GetById(q.QuestId)))
            .Where(x => x.Definition is not null)
            .ToList();

        if (activeQuests.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("\nACTIVE QUESTS:");

        foreach (var (progress, definition) in activeQuests)
        {
            if (progress.Status == QuestStatus.ReadyToTurnIn)
            {
                sb.AppendLine($"- \"{definition!.Name}\" (id: {definition.Id}): All objectives complete — ready to turn in.");
            }
            else
            {
                var stage = definition!.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
                if (stage is not null)
                {
                    sb.AppendLine($"- \"{definition.Name}\" (id: {definition.Id}): Stage \"{stage.Name}\" — {stage.Description}");
                    if (!string.IsNullOrWhiteSpace(stage.NarratorHint))
                        sb.AppendLine($"  Stage hint: {stage.NarratorHint}");
                    foreach (var obj in stage.Objectives)
                    {
                        var objProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == obj.Id);
                        var status = objProgress?.IsComplete == true ? "✓" : $"{objProgress?.CurrentCount ?? 0}/{obj.RequiredCount}";
                        var targetName = obj.TargetName ?? obj.TargetId;
                        var location = string.IsNullOrWhiteSpace(obj.LocationConstraint) ? string.Empty : $" @ {obj.LocationConstraint}";
                        sb.AppendLine($"  • {obj.Description ?? obj.Id} [{status}] {(string.IsNullOrWhiteSpace(targetName) ? string.Empty : $"target: {targetName}")}{location}".TrimEnd());
                    }
                }
            }
        }

        sb.AppendLine(Prompts.QuestPrompts.FreeFormQuestInstructions);
        return sb.ToString();
    }

    private static bool TryBuildDeterministicLookNarration(NarratorContext context, out string narration)
    {
        narration = string.Empty;
        if (context.Action.Type != ActionType.Look)
            return false;

        var room = context.CurrentRoom;
        if (!string.IsNullOrWhiteSpace(context.Action.Target))
        {
            var target = context.Action.Target.Trim();
            if (TargetReferencesRoom(room, target))
            {
                target = string.Empty;
            }

            var npc = FindNamedEntity(room.Npcs, candidate => candidate.Name, target);
            if (npc is not null)
            {
                var matchingCount = room.Npcs.Count(candidate => NormalizeLookupText(candidate.Name) == NormalizeLookupText(npc.Name));
                var representativeLead = matchingCount > 1
                    ? $"Though {matchingCount} figures answer to that same silhouette, you fix on one representative {npc.Name.ToLowerInvariant()} at the edge of the room."
                    : $"You fix your attention on {npc.Name}.";
                var personalityDetail = HasUsefulPersonality(npc.Personality)
                    ? $"Their bearing suggests {TrimToSentence(npc.Personality).TrimEnd('.').ToLowerInvariant()}, a detail that sharpens rather than softens on closer inspection."
                    : $"Even standing still, {npc.Name} carries the kind of presence that makes the room feel more heavily watched.";
                narration = $"{representativeLead} {personalityDetail} Up close, the smallest details feel sharper than they did at a glance.";
                return true;
            }

            var item = FindNamedEntity(room.Items, candidate => candidate.Name, target);
            if (item is not null)
            {
                var totalQuantity = room.Items
                    .Where(candidate => NormalizeLookupText(candidate.Name) == NormalizeLookupText(item.Name))
                    .Sum(candidate => Math.Max(1, candidate.Quantity));
                var lead = totalQuantity > 1
                    ? $"Among the {item.Name} scattered through the room, you pick one out for closer study."
                    : $"You give {item.Name} a closer look.";
                var detail = !string.IsNullOrWhiteSpace(item.Description)
                    ? item.Description.TrimEnd('.')
                    : "its shape suggests use before beauty, the kind of tool made to matter more in the hand than on display";
                narration = $"{lead} {char.ToUpperInvariant(detail[0])}{detail[1..]}. In a place like {room.Name}, even a small object feels chosen rather than forgotten.";
                return true;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                narration = $"You search for {target}, but the name never quite lands on anything solid in {room.Name}. {BuildAmbientMissSentence(room)}";
                return true;
            }
        }

        var roomAtmosphere = BuildRoomAtmosphere(room);
        var focalDetail = BuildRoomFocalDetail(room);
        var exitDetail = BuildExitDetail(room);

        var openingTemplates = new[]
        {
            $"You take stock of {room.Name}.",
            $"You survey {room.Name} with a practiced eye.",
            $"You pause to absorb the details of {room.Name}.",
            $"The details of {room.Name} sharpen as you look around.",
            $"You give {room.Name} a thorough once-over.",
            $"{room.Name} unfolds before your careful gaze.",
            $"You scan {room.Name}, cataloguing threats and exits alike.",
            $"A practiced eye sweeps across {room.Name}."
        };
        var opening = openingTemplates[Math.Abs(room.Id.GetHashCode() + context.Player.Name.Length) % openingTemplates.Length];

        narration = $"{opening} {roomAtmosphere}";
        if (!string.IsNullOrWhiteSpace(focalDetail))
            narration += $" {focalDetail}";
        if (!string.IsNullOrWhiteSpace(exitDetail))
            narration += $" {exitDetail}";

        return true;
    }

    private static string BuildContextualFallbackNarration(NarratorContext context)
    {
        if (TryBuildDeterministicLookNarration(context, out var lookNarration))
            return lookNarration;

        // When the LLM fails to narrate, just return the mechanical summary directly.
        // No flowery wrapper — the mechanical text is clear enough on its own.
        if (!context.MechanicalResult.Success)
        {
            var failureReason = TrimToSentence(context.MechanicalResult.MechanicalSummary);
            return string.IsNullOrWhiteSpace(failureReason) ? "That didn't work." : failureReason;
        }

        var resolvedOutcome = TrimToSentence(context.MechanicalResult.MechanicalSummary);
        return string.IsNullOrWhiteSpace(resolvedOutcome)
            ? "Done."
            : resolvedOutcome;
    }

    private static string BuildRoomAtmosphere(Room room)
    {
        var description = TrimToSentence(room.Description);

        if (string.IsNullOrWhiteSpace(description))
        {
            return "The place reveals itself slowly — more through feeling than any obvious feature.";
        }

        // Use the room's own description as-is — it's already been written to be atmospheric
        return description.EndsWith('.') ? description : description + ".";
    }

    private static string BuildRoomFocalDetail(Room room)
    {
        var parts = new List<string>();

        if (room.Npcs.Count > 0)
        {
            var npcSummary = SummarizeEntities(room.Npcs, npc => npc.Name);
            var npcTemplates = new[]
            {
                $"{npcSummary} {(room.Npcs.Count == 1 ? "catches" : "catch")} your attention",
                $"You notice {npcSummary} nearby",
                $"{npcSummary} {(room.Npcs.Count == 1 ? "occupies" : "occupy")} the space with quiet purpose",
            };
            parts.Add(npcTemplates[Math.Abs(room.Id.GetHashCode() + room.Npcs.Count) % npcTemplates.Length]);
        }

        if (room.Items.Count > 0)
        {
            var itemSummary = SummarizeEntities(room.Items, item => item.Name, item => item.Quantity);
            var itemTemplates = new[]
            {
                $"{itemSummary} {(room.Items.Count == 1 ? "sits" : "sit")} within reach",
                $"you spot {itemSummary}",
                $"{itemSummary} {(room.Items.Count == 1 ? "draws" : "draw")} the eye",
            };
            parts.Add(itemTemplates[Math.Abs(room.Id.GetHashCode() + room.Items.Count) % itemTemplates.Length]);
        }

        return parts.Count switch
        {
            0 => string.Empty,
            1 => parts[0] + ".",
            _ => parts[0] + ", and " + parts[1] + "."
        };
    }

    private static string BuildExitDetail(Room room)
    {
        if (room.Exits.Count == 0)
            return "No obvious way onward presents itself.";

        var dirs = HumanizeDirections(room.Exits.Keys);
        var verb = room.Exits.Count == 1 ? "leads" : "lead";
        var exitTemplates = new[]
        {
            $"Paths {verb} {dirs}.",
            $"Exits {verb} {dirs}.",
            $"You can go {dirs}.",
        };
        return exitTemplates[Math.Abs(room.Id.GetHashCode() + room.Exits.Count) % exitTemplates.Length];
    }

    private static string BuildAmbientMissSentence(Room room)
    {
        if (room.Npcs.Count > 0 && room.Items.Count > 0)
        {
            return $"What answers instead is the uneasy company of {SummarizeEntities(room.Npcs, npc => npc.Name)} and the cold gleam of {SummarizeEntities(room.Items, item => item.Name, item => item.Quantity)}.";
        }

        if (room.Npcs.Count > 0)
            return $"What answers instead is the watchful presence of {SummarizeEntities(room.Npcs, npc => npc.Name)}.";

        if (room.Items.Count > 0)
            return $"What answers instead is the stubborn gleam of {SummarizeEntities(room.Items, item => item.Name, item => item.Quantity)}.";

        return "Only the room's hush and whatever faint echo lives in its corners answer back.";
    }

    private static bool HasUsefulPersonality(string? personality)
    {
        if (string.IsNullOrWhiteSpace(personality))
            return false;

        var normalized = personality.Trim().ToLowerInvariant();
        return !(normalized.Contains("test", StringComparison.Ordinal)
            || normalized.Contains("fixture", StringComparison.Ordinal)
            || normalized.Contains("placeholder", StringComparison.Ordinal)
            || normalized.Contains("default", StringComparison.Ordinal)
            || normalized.Contains("generic", StringComparison.Ordinal));
    }

    private static bool TryBuildLowStakesFreeFormResponse(PlayerCharacter player, Room room, string rawInput, out FreeFormResponse response)
    {
        response = null!;
        var normalized = rawInput.Trim().ToLowerInvariant();
        if (!IsLowStakesAction(normalized))
            return false;

        var witness = room.Npcs.FirstOrDefault()?.Name;
        var witnessReaction = string.IsNullOrWhiteSpace(witness)
            ? "No one in the room seems especially impressed."
            : $"{witness} notices, then pointedly returns their attention to more important matters.";

        var narration = normalized switch
        {
            "pick nose" or "pick your nose" => $"{player.Name} commits a brief lapse in dignity and picks at their nose. {witnessReaction}",
            _ when normalized.StartsWith("dance") => $"{player.Name} gives the room a few improvised steps and a half-serious flourish. {witnessReaction}",
            _ when normalized.StartsWith("wave") => $"{player.Name} gives a small wave into the room. {witnessReaction}",
            _ when normalized.StartsWith("shrug") => $"{player.Name} offers a small shrug, as if the room itself might explain something. {witnessReaction}",
            _ when normalized.StartsWith("laugh") => $"{player.Name} lets out a short laugh that fades quickly into the room's background hum. {witnessReaction}",
            _ when normalized.StartsWith("sit") => $"{player.Name} eases into a brief, restless sit before rising again. {witnessReaction}",
            _ => $"{player.Name} indulges in a brief, harmless gesture. {witnessReaction}"
        };

        response = new FreeFormResponse
        {
            Narration = narration,
            Success = true
        };
        return true;
    }

    private static bool IsLowStakesAction(string normalized)
        => normalized is "pick nose" or "pick your nose"
            || normalized.StartsWith("dance", StringComparison.Ordinal)
            || normalized.StartsWith("wave", StringComparison.Ordinal)
            || normalized.StartsWith("shrug", StringComparison.Ordinal)
            || normalized.StartsWith("laugh", StringComparison.Ordinal)
            || normalized.StartsWith("sit", StringComparison.Ordinal)
            || normalized.StartsWith("bow", StringComparison.Ordinal)
            || normalized.StartsWith("smile", StringComparison.Ordinal)
            || normalized.StartsWith("yawn", StringComparison.Ordinal)
            || normalized.StartsWith("sneeze", StringComparison.Ordinal)
            || normalized.StartsWith("cough", StringComparison.Ordinal);

    private static string TrimToSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "The room offers no clear detail at first glance.";

        var trimmed = text.Trim();
        var stop = trimmed.IndexOfAny(['.', '!', '?']);
        if (stop >= 0)
            return trimmed[..(stop + 1)];

        return trimmed;
    }

    private static string HumanizeDirections(IEnumerable<string> directions)
    {
        var ordered = directions.Select(direction => direction.Trim()).Where(direction => !string.IsNullOrWhiteSpace(direction)).ToArray();
        return ordered.Length switch
        {
            0 => "nowhere obvious",
            1 => ordered[0],
            2 => $"{ordered[0]} and {ordered[1]}",
            _ => $"{string.Join(", ", ordered[..^1])}, and {ordered[^1]}"
        };
    }

    private static string FormatStoryContextLine(StoryEntry entry)
    {
        var summary = string.IsNullOrWhiteSpace(entry.Narration)
            ? entry.MechanicalSummary
            : entry.Narration;

        summary = StripRoomMetadata(summary);
        if (string.IsNullOrWhiteSpace(summary))
            return string.IsNullOrWhiteSpace(entry.RawInput) ? "- [Room state updated]" : $"- {entry.RawInput}";

        return string.IsNullOrWhiteSpace(entry.RawInput)
            ? $"- {summary}"
            : $"- {entry.RawInput}: {summary}";
    }

    private static T? FindNamedEntity<T>(IEnumerable<T> entities, Func<T, string?> getName, string? rawQuery) where T : class
    {
        var query = NormalizeLookupText(rawQuery);
        if (string.IsNullOrWhiteSpace(query))
            return null;

        return entities
            .Select(entity => new { Entity = entity, Candidate = NormalizeLookupText(getName(entity)) })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Candidate))
            .Select(entry => new { entry.Entity, Score = GetNameMatchScore(query, entry.Candidate) })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Entity)
            .FirstOrDefault();
    }

    private static bool TargetReferencesRoom(Room room, string? rawTarget)
    {
        var target = NormalizeLookupText(rawTarget);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (target is "room" or "around" or "here" or "surroundings")
            return true;

        var roomName = NormalizeLookupText(room.Name);
        return target == roomName || Singularize(target) == Singularize(roomName);
    }

    private static int GetNameMatchScore(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return 0;

        var normalizedQuery = Singularize(query);
        var normalizedCandidate = Singularize(candidate);

        if (candidate == query || normalizedCandidate == normalizedQuery)
            return 100;

        if (candidate.StartsWith(query, StringComparison.Ordinal) || candidate.StartsWith(normalizedQuery, StringComparison.Ordinal))
            return 90;

        if (candidate.Contains(query, StringComparison.Ordinal) || candidate.Contains(normalizedQuery, StringComparison.Ordinal))
            return 75;

        if (query.StartsWith(candidate, StringComparison.Ordinal) || normalizedQuery.StartsWith(normalizedCandidate, StringComparison.Ordinal))
            return 65;

        if (query.Contains(candidate, StringComparison.Ordinal) || normalizedQuery.Contains(normalizedCandidate, StringComparison.Ordinal))
            return 55;

        return 0;
    }

    private static string NormalizeLookupText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalizedCharacters = value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character)
                ? character
                : character is '-' or '_' || char.IsWhiteSpace(character)
                    ? ' '
                    : '\0')
            .Where(character => character != '\0')
            .ToArray();

        var normalized = new string(normalizedCharacters);
        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not "the" and not "a" and not "an"));
    }

    private static string Singularize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= 3)
            return value;

        if (value.EndsWith("ies", StringComparison.Ordinal))
            return value[..^3] + "y";

        if (value.EndsWith('s') && !value.EndsWith("ss", StringComparison.Ordinal))
            return value[..^1];

        return value;
    }

    private static IEnumerable<InventoryItem> GetEquippedItems(PlayerCharacter player)
        => player.Equipment.AllEquipped();

    /// <summary>Formats an NPC's name + disposition + memory for prompt context.</summary>
    private static string FormatNpcForPrompt(Npc npc)
    {
        var parts = new List<string> { npc.Name };

        if (npc.IsHostile)
            parts.Add("hostile");
        else if (npc.DispositionState.Emotion != "neutral")
            parts.Add($"{npc.DispositionState.ToFlatDisposition()}");

        if (npc.DispositionState.MemoryFlags.Count > 0)
            parts.Add($"remembers: {string.Join(", ", npc.DispositionState.MemoryFlags)}");

        return parts.Count == 1 ? npc.Name : $"{npc.Name} ({string.Join("; ", parts.Skip(1))})";
    }

    private static string SummarizeEntities<T>(IEnumerable<T> entities, Func<T, string?> getName, Func<T, int>? getQuantity = null)
    {
        var counts = new Dictionary<string, (string DisplayName, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities)
        {
            var name = getName(entity)?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var quantity = Math.Max(1, getQuantity?.Invoke(entity) ?? 1);
            if (counts.TryGetValue(name, out var existing))
            {
                counts[name] = (existing.DisplayName, existing.Count + quantity);
                continue;
            }

            counts[name] = (name, quantity);
        }

        return string.Join(", ", counts.Values.Select(entry => entry.Count > 1 ? $"{entry.DisplayName} (x{entry.Count})" : entry.DisplayName));
    }

    private static string StripRoomMetadata(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.Split('\n', StringSplitOptions.None);
        var metadataIndexes = lines
            .Select((line, index) => (Line: line.Trim().Trim('*').Trim(), Index: index))
            .Where(entry => entry.Line.StartsWith("Exits:", StringComparison.OrdinalIgnoreCase)
                || entry.Line.StartsWith("You see:", StringComparison.OrdinalIgnoreCase)
                || entry.Line.StartsWith("Items:", StringComparison.OrdinalIgnoreCase)
                || entry.Line.StartsWith("NPCs:", StringComparison.OrdinalIgnoreCase)
                || entry.Line.StartsWith("Creatures:", StringComparison.OrdinalIgnoreCase)
                || entry.Line.StartsWith("Objects:", StringComparison.OrdinalIgnoreCase)
                || entry.Line.StartsWith("Nearby:", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Index)
            .ToArray();
        var looksLikeRoomBlock = metadataIndexes.Length >= 2 && metadataIndexes[0] <= 2;
        var filtered = new List<string>(lines.Length);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            var normalized = trimmed.Trim('*').Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (looksLikeRoomBlock && index < metadataIndexes[0])
                continue;

            if (normalized.StartsWith("Exits:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("You see:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Items:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("NPCs:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Creatures:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Objects:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("Nearby:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(line);
        }

        return string.Join("\n", filtered).Trim();
    }

    private static string OppositeDirection(string dir) => dir switch
    {
        "north" => "south", "south" => "north",
        "east" => "west", "west" => "east",
        "up" => "down", "down" => "up",
        _ => "back"
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // LM Studio OpenAI-compatible DTOs
    private class LmStudioRequest
    {
        public string Model { get; set; } = "default";
        public List<LmStudioMessage> Messages { get; set; } = [];
        public double Temperature { get; set; } = 0.8;
        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 2048;
        public bool Stream { get; set; }
    }

    private class LmStudioMessage
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    private class LmStudioResponse
    {
        public List<LmStudioChoice>? Choices { get; set; }
    }

    private class LmStudioChoice
    {
        public LmStudioMessage? Message { get; set; }
        public LmStudioDelta? Delta { get; set; }
    }

    private class LmStudioDelta
    {
        public string? Content { get; set; }
    }

    private class GeneratedRoom
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? EnvironmentTags { get; set; }
        public List<GeneratedNpc>? Npcs { get; set; }
        public List<GeneratedItem>? Items { get; set; }
    }

    private class GeneratedDungeonRoom
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? EnvironmentTags { get; set; }
        public List<GeneratedNpc>? Npcs { get; set; }
        public List<GeneratedItem>? Items { get; set; }
        public Dictionary<string, string>? Exits { get; set; }
    }

    private class GeneratedNpc
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Personality { get; set; }
        public string? Faction { get; set; }
        public bool IsHostile { get; set; }
        public int Level { get; set; } = 1;
        public int Hp { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
    }

    private class GeneratedItem
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Type { get; set; }
        public int Quantity { get; set; } = 1;
        public int Value { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Spell Vetting (Spellbook System — Option A)
    // ═══════════════════════════════════════════════════════════════

    public async Task<SpellVetResponse?> VetSpellAsync(PlayerCharacter player, string spellDescription, Room room, CancellationToken ct = default)
    {
        var systemPrompt = """
            You are a Game Master vetting a player's invented spell for a dark-fantasy RPG.

            APPROVAL RULES:
            - The spell must fit a dark-fantasy setting (no sci-fi, modern tech, or memes)
            - Creative and unusual spells are ENCOURAGED ("summon angry bees" = fine, "hack mainframe" = no)
            - Non-combat spells (utility, buffs, debuffs) are valid
            - Do NOT reject for being too powerful — power scaling is handled separately
            - Only reject spells that are lore-breaking, nonsensical, or trolling

            Assign basePower 1-10:
              1-2: cantrip (spark, minor illusion)
              3-4: standard (fireball, healing touch)
              5-6: powerful (chain lightning, wall of fire)
              7-8: very powerful (meteor, resurrection)
              9-10: legendary (wish, reality warp)

            Respond with ONLY valid JSON (no markdown, no code fences):
            {"approved":true,"rejectionReason":null,"spellName":"Fireball","description":"A roaring sphere of flame erupts from your hands.","category":"damage","targetType":"enemy","basePower":4,"mpCost":8,"narration":"Arcane energy surges through your fingertips as you shape raw fire into a deadly sphere."}
            """;

        var userPrompt = $"""
            CASTER: {player.Name} (Lv.{player.Level} {player.Race} {player.Class})
            HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp}
            {player.FormatStatsCompact()}

            LOCATION: {room.Name} - {room.Description}
            NPCs present: {string.Join(", ", room.Npcs.Select(n => $"{n.Name} (HP:{n.Hp ?? 0}/{n.MaxHp ?? 0})"))}

            SPELL DESCRIPTION: "{spellDescription}"

            Vet this spell. Assign a name, description, category, targetType, basePower, mpCost, and narration.
            """;

        try
        {
            var rawResponse = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct,
                operation: "spell-vet", playerId: player.Id, roomId: room.Id);

            var json = ExtractJson(rawResponse);
            if (json is not null)
            {
                var parsed = JsonSerializer.Deserialize<SpellVetJsonResponse>(json, _jsonOptions);
                if (parsed is not null)
                {
                    return new SpellVetResponse
                    {
                        Approved = parsed.Approved,
                        RejectionReason = parsed.RejectionReason,
                        SpellName = parsed.SpellName ?? spellDescription,
                        Description = parsed.Description ?? "A burst of magical energy.",
                        Category = parsed.Category ?? "damage",
                        TargetType = parsed.TargetType ?? "enemy",
                        BasePower = Math.Clamp(parsed.BasePower, 1, 10),
                        MpCost = Math.Max(2, parsed.MpCost),
                        Narration = parsed.Narration ?? "You channel arcane energy."
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spell vetting failed for '{Spell}'", spellDescription);
        }

        return null;
    }

    private class SpellVetJsonResponse
    {
        [JsonPropertyName("approved")]
        public bool Approved { get; set; }
        [JsonPropertyName("rejectionReason")]
        public string? RejectionReason { get; set; }
        [JsonPropertyName("spellName")]
        public string? SpellName { get; set; }
        [JsonPropertyName("description")]
        public string? Description { get; set; }
        [JsonPropertyName("category")]
        public string? Category { get; set; }
        [JsonPropertyName("targetType")]
        public string? TargetType { get; set; }
        [JsonPropertyName("basePower")]
        public int BasePower { get; set; }
        [JsonPropertyName("mpCost")]
        public int MpCost { get; set; }
        [JsonPropertyName("narration")]
        public string? Narration { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Improvised Spell Evaluation (Power Budget System)
    // ═══════════════════════════════════════════════════════════════

    public async Task<ImprovisedSpellResult> EvaluateImprovisedSpellAsync(
        PlayerCharacter player, Room room, string spellName, string? target,
        int powerCap, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
    {
        var systemPrompt = $$"""
            You are the spell evaluation system for a dark-fantasy RPG with Sierra adventure game humor.

            A player is attempting to cast an IMPROVISED spell (not in the spell registry).
            Your job is to:
            1. Assess the power level of the attempted spell (1-10 scale)
            2. Determine if it succeeds or fizzles based on the player's power cap
            3. Calculate reasonable mana cost and effects
            4. Write a dramatic, funny narration

            POWER SCALE:
            1-2: Cantrip/minor effect (small spark, dim light, minor trick)
            3-4: Intermediate (decent damage, useful utility, moderate healing)
            5-6: Advanced (significant damage, strong effects, area impact)
            7-8: Expert (devastating, reality-bending, powerful transformations)
            9-10: Legendary/godlike (city-destroying, time manipulation, resurrection)

            RULES:
            - The player's power cap is {{powerCap}}. If the spell's power exceeds this, it FIZZLES.
            - Fizzles should be HILARIOUS. Slapstick, ironic backfire, embarrassing failure. Never boring.
            - Even successful improvised spells should be slightly unpredictable/quirky — they're not polished.
            - Mana cost scales with power level: roughly power * 2 MP.
            - Damage scales: power 1 = 1d4, power 2 = 1d6, power 3 = 2d4, power 5 = 3d6, etc.
            - A level 1 attempting "Death Ray" = power 10 = spectacular fizzle. Make it MEMORABLE.
            - Creative but low-power spells should succeed! "Wall of bees" at power 2 = yes, a few confused bees.

            Respond with ONLY valid JSON (no markdown, no code fences):
            {
              "powerLevel": 3,
              "success": true,
              "manaCost": 6,
              "damage": 8,
              "healing": 0,
              "narration": "Your dramatic narration of the spell attempt.",
              "statChanges": {},
              "target": "Goblin Scout"
            }

            For fizzles, set success=false, damage=0, healing=0, and write a funny fizzle narration.
            statChanges can include "hp" for backfire damage on fizzle (e.g. {"hp": -2}).
            """;

        var recentLines = recentStory.Count > 0
            ? string.Join("\n", recentStory.Select(FormatStoryContextLine))
            : "(No recent history)";

        var userPrompt = $"""
            CASTER: {player.Name} (Lv.{player.Level} {player.Race} {player.Class})
            HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp}
            {player.FormatStatsCompact()}
            POWER CAP: {powerCap}

            LOCATION: {room.Name} - {room.Description}
            NPCs present: {string.Join(", ", room.Npcs.Select(n => $"{n.Name} (HP:{n.Hp ?? 0}/{n.MaxHp ?? 0})"))}

            SPELL ATTEMPTED: "{spellName}"
            TARGET: {target ?? "(no specific target)"}

            Recent history:
            {recentLines}

            Evaluate this spell attempt. Remember: power cap is {powerCap}.
            """;

        try
        {
            var rawResponse = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct,
                operation: "improvised-spell", playerId: player.Id, roomId: room.Id);

            var json = ExtractJson(rawResponse);
            if (json is not null)
            {
                var parsed = JsonSerializer.Deserialize<ImprovisedSpellJsonResponse>(json, _jsonOptions);
                if (parsed is not null)
                {
                    return new ImprovisedSpellResult
                    {
                        PowerLevel = parsed.PowerLevel,
                        PlayerCap = powerCap,
                        Success = parsed.Success,
                        ManaCost = Math.Max(0, parsed.ManaCost),
                        Damage = Math.Max(0, parsed.Damage),
                        Healing = Math.Max(0, parsed.Healing),
                        Narration = parsed.Narration ?? "The spell fizzles without explanation.",
                        StatChanges = parsed.StatChanges ?? new(),
                        Target = parsed.Target
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Improvised spell evaluation failed for '{Spell}'", spellName);
        }

        // Fallback: assume it fizzles safely
        return new ImprovisedSpellResult
        {
            PowerLevel = powerCap + 1,
            PlayerCap = powerCap,
            Success = false,
            ManaCost = 1,
            Narration = $"You attempt to cast {spellName}, but the magical energy dissipates before it can take form. A faint smell of burnt toast lingers.",
            StatChanges = new()
        };
    }

    private class ImprovisedSpellJsonResponse
    {
        [JsonPropertyName("powerLevel")]
        public int PowerLevel { get; set; }
        [JsonPropertyName("success")]
        public bool Success { get; set; }
        [JsonPropertyName("manaCost")]
        public int ManaCost { get; set; }
        [JsonPropertyName("damage")]
        public int Damage { get; set; }
        [JsonPropertyName("healing")]
        public int Healing { get; set; }
        [JsonPropertyName("narration")]
        public string? Narration { get; set; }
        [JsonPropertyName("statChanges")]
        public Dictionary<string, int>? StatChanges { get; set; }
        [JsonPropertyName("target")]
        public string? Target { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    //  AI Content Generator — describe what you want, AI fills in details
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> GenerateContentAsync(string contentType, string description, string? existingJson, CancellationToken ct = default)
    {
        var schemaExamples = contentType.ToLowerInvariant() switch
        {
            "spell" => """
                {
                  "id": "snake_id_format",
                  "name": "Display Name",
                  "description": "Flavor text describing the spell.",
                  "school": "evocation|restoration|abjuration|illusion|conjuration|divination|necromancy|transmutation",
                  "mana_cost": 5,
                  "damage_dice": "2d6+2",
                  "damage_stat": "int",
                  "heal_dice": null,
                  "status_effect": null,
                  "duration": 0,
                  "range": "self|touch|ranged",
                  "required_classes": ["mage", "sorcerer"],
                  "required_level": 1,
                  "power_level": 3,
                  "tags": ["fire", "aoe"]
                }
                """,
            "item" => """
                {
                  "id": "snake_id_format",
                  "name": "Display Name",
                  "description": "Flavor text describing the item.",
                  "type": "Weapon|Armor|Shield|Helmet|Potion|Scroll|Key|QuestItem|Misc",
                  "damage_dice": "1d8+2",
                  "damage_stat": "str",
                  "armor_value": 0,
                  "is_equippable": true,
                  "is_consumable": false,
                  "effect": null,
                  "value": 50,
                  "rarity": "common|uncommon|rare|epic|legendary",
                  "required_classes": [],
                  "required_level": 1,
                  "tags": ["melee", "slashing"]
                }
                """,
            "class" => """
                {
                  "id": "snake_id_format",
                  "name": "Display Name",
                  "description": "Class description and lore.",
                  "hit_die": "d10",
                  "primary_stat": "str",
                  "secondary_stat": "con",
                  "base_mp_bonus": 0,
                  "can_cast_spells": false,
                  "spell_list": [],
                  "allowed_weapon_types": ["Weapon"],
                  "allowed_armor_types": ["Armor", "Shield", "Helmet"],
                  "stat_bonuses": {"str": 2, "con": 1},
                  "starting_equipment": ["iron_sword"],
                  "improvised_spell_cap": [1, 1, 2, 2, 3, 3, 4, 4, 5, 5],
                  "tags": ["martial"]
                }
                """,
            "race" => """
                {
                  "id": "snake_id_format",
                  "name": "Display Name",
                  "description": "Race description and lore.",
                  "stat_bonuses": {"dex": 2, "int": 1},
                  "traits": ["Darkvision", "Fey Ancestry"],
                  "allowed_classes": [],
                  "tags": ["fey"]
                }
                """,
            "npc" => """
                {
                  "id": "snake_id_format",
                  "name": "Display Name",
                  "personality": "A brief personality description.",
                  "faction": "neutral",
                  "is_hostile": false,
                  "level": 3,
                  "hp": 25,
                  "max_hp": 25,
                  "attack_bonus": 3,
                  "damage_dice": "1d6+2",
                  "defense": 12,
                  "knowledge_scopes": ["town", "rumors"],
                  "is_shopkeeper": false,
                  "dialogue": {"greeting": "Hello there!"},
                  "loot": [{"id": "gold_coins", "name": "Gold Coins", "type": "Misc", "value": 10}]
                }
                """,
            "room" => """
                {
                  "id": "snake_id_format",
                  "name": "Display Name",
                  "description": "A vivid description of the room.",
                  "exits": {"north": "other_room_id", "south": "another_room_id"},
                  "npcs": [],
                  "items": [],
                  "environment_tags": ["indoor", "dark", "dungeon"]
                }
                """,
            "player" => """
                {
                  "id": "existing_player_id",
                  "name": "Character Name",
                  "race": "human",
                  "class": "fighter",
                  "backstory": "A brief character backstory.",
                  "currentRoomId": "spawn",
                  "hp": 20,
                  "maxHp": 20,
                  "mp": 10,
                  "maxMp": 10,
                  "gold": 50,
                  "xp": 0,
                  "level": 1,
                  "str": 10,
                  "dex": 10,
                  "con": 10,
                  "int": 10,
                  "wis": 10,
                  "cha": 10,
                  "luck": 10
                }
                """,
            _ => """{"id": "snake_id", "name": "Name", "description": "Description"}"""
        };

        var systemPrompt = $"""
            You are a content creation assistant for a dark-fantasy RPG called the Grand Adventure Engine.
            The game has the comic sensibility of classic Sierra adventures (Quest for Glory, King's Quest).

            You are generating a {contentType} definition. The user will describe what they want in natural language,
            and you will fill in ALL the structured fields with balanced, creative values.

            RULES:
            - Use the exact JSON schema shown below. Return ONLY valid JSON, no markdown or code fences.
            - Use snake_case for the "id" field (e.g., "flaming_sword", "goblin_shaman").
            - Be creative with descriptions — capture the Sierra adventure tone.
            - Be balanced — don't make things overpowered unless the user specifically asks.
            - If the user provides partial details, fill in reasonable defaults for everything else.
            - If editing existing content, preserve any fields the user doesn't mention changing.

            JSON SCHEMA for {contentType}:
            {schemaExamples}
            """;

        var userPrompt = description;
        if (existingJson is not null)
            userPrompt = $"EXISTING CONTENT (edit this):\n{existingJson}\n\nUSER REQUEST:\n{description}";

        var rawResponse = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct,
            operation: "content-generate", maxTokens: 4096);

        return ExtractJson(rawResponse) ?? rawResponse;
    }

    /// <inheritdoc />
    public async Task<StatTranslationResponse?> TranslateStatsAsync(StatTranslationRequest request, CancellationToken ct = default)
    {
        var sourceStatLines = string.Join("\n", request.SourceStats.Select(kvp =>
            $"  - {kvp.Value.Display} ({kvp.Key}): {kvp.Value.Value} (range {kvp.Value.Min}-{kvp.Value.Max}) [tags: {string.Join(", ", kvp.Value.SemanticTags)}]"));

        var destStatLines = string.Join("\n", request.DestinationStatDefs.Select(kvp =>
            $"  - {kvp.Value.Display} ({kvp.Key}): range {kvp.Value.Min}-{kvp.Value.Max} [tags: {string.Join(", ", kvp.Value.SemanticTags)}]"));

        var systemPrompt = """
            You are the Grand Adventure Engine's stat translator. A character is crossing between
            worlds with different stat systems. Your job is to translate their stats as faithfully
            as possible, using the semantic tags as guides.

            RULES:
            1. Map stats by semantic tag overlap first. Direct overlaps are 1:1 mappings.
            2. If origin has stats with no destination equivalent, fold their value into the closest
               related stat. Explain why.
            3. If destination has stats with no origin equivalent, derive from the closest related
               origin stats. Explain why.
            4. Preserve the character's relative power level. A strong character stays strong.
            5. Respect destination stat ranges (min/max). Scale proportionally if needed.
            6. If previous translations exist for this world pair, stay consistent unless the
               character's level has changed significantly.
            7. Return ONLY valid JSON in the specified format. No markdown, no commentary outside JSON.

            RESPOND WITH:
            {
              "translated_stats": { "<stat_id>": <value>, ... },
              "translation_notes": "<explain your reasoning briefly>",
              "narrative": "<2-3 sentences describing the physical sensation of the transformation>"
            }
            """;

        var previousSection = !string.IsNullOrWhiteSpace(request.PreviousTranslation)
            ? $"\nPREVIOUS TRANSLATIONS (for consistency):\n{request.PreviousTranslation}"
            : "";

        var userPrompt = $"""
            CHARACTER:
            - Name: {request.CharacterName}  Class: {request.Class ?? "Unknown"}  Race: {request.Race ?? "Unknown"}  Level: {request.Level}
            - Origin World: {request.SourceWorldName}
            - Origin Stats:
            {sourceStatLines}

            DESTINATION WORLD: {request.DestinationWorldName}
            DESTINATION STATS:
            {destStatLines}
            {previousSection}
            Translate the character's stats to the destination world's system.
            """;

        try
        {
            var rawResponse = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct,
                operation: "stat-translation", maxTokens: 1024);

            var json = ExtractJson(rawResponse);
            if (json is null)
            {
                _logger.LogWarning("Stat translation response did not contain valid JSON: {Response}", rawResponse);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<StatTranslationResponse>(json, _jsonOptions);
            if (parsed is null || parsed.TranslatedStats.Count == 0)
            {
                _logger.LogWarning("Stat translation response parsed but contained no stats: {Json}", json);
                return null;
            }

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI stat translation failed, caller should fall back to deterministic");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> NarrateRealmTransitionAsync(
        string playerName, string sourceWorldName, string destinationWorldName,
        string? portalHint, CancellationToken ct = default)
    {
        var systemPrompt = """
            You are the narrator of a dark-fantasy text adventure. A character is crossing between realms.
            Narrate the moment of transition in 2-3 sentences, second person present tense.
            Describe physical sensations, visual distortions, the feeling of reality shifting.
            Be vivid and atmospheric. Do not ask questions. Do not mention game mechanics.
            """;

        var portalContext = !string.IsNullOrWhiteSpace(portalHint)
            ? $"Portal description: {portalHint}"
            : "The transition happens through magical means.";

        var userPrompt = $"""
            Character: {playerName}
            Departing: {sourceWorldName}
            Arriving: {destinationWorldName}
            {portalContext}
            Narrate the crossing.
            """;

        try
        {
            return await CompletionOrThrowAsync(systemPrompt, userPrompt, ct,
                operation: "realm-transition", maxTokens: 256);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Realm transition narration failed");
            return null;
        }
    }
}
