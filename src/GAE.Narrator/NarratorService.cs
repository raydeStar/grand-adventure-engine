using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Narrator;

public class NarratorService : INarratorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NarratorService> _logger;
    private readonly WorldKnowledgeBuilder? _knowledge;
    private readonly IConversationLogger? _conversationLogger;
    private string _model;
    private bool _modelResolved;

    public NarratorService(HttpClient httpClient, ILogger<NarratorService> logger, string model = "default", WorldKnowledgeBuilder? knowledge = null, IConversationLogger? conversationLogger = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _knowledge = knowledge;
        _conversationLogger = conversationLogger;
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

        var systemPrompt = """
            You are the narrator of a dark-fantasy text adventure with the comic sensibility of
            classic Sierra point-and-click games (Quest for Glory, King's Quest, Space Quest).

            VOICE:
            - Dry, sardonic wit. You love absurd observations and understated reactions.
            - When the player FAILS, make it entertaining — slapstick, ironic commentary, the universe conspiring.
              Never just say "nothing happens." Make failures *memorable and funny.*
            - When the player SUCCEEDS, let them feel cool, but sneak in a wry aside.
            - Use concrete sensory detail and at least one vivid visual focal point.
            - Write 2-4 sentences. Be punchy, not flowery.

            RULES:
            - Narrate what the engine decided. Never contradict the mechanical result.
            - Always use the player's character name.
            - Never ask the player questions or break the fourth wall about being a narrator/AI.
            - For failed movement, describe the futile attempt with humor. The wall is unyielding, the cliff uninviting, etc.
            - For failed actions, honor the failure reason but translate it into something entertaining.
            - NPCs should react to absurd actions with personality — annoyance, amusement, disgust, concern.
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

        var userPrompt = $"""
            Player: {context.Player.Name} ({context.Player.Race} {context.Player.Class}, Level {context.Player.Level})
            Location: {context.CurrentRoom.Name} — {context.CurrentRoom.Description}
            Visible NPCs: {visibleNpcs}
            Visible Items: {visibleItems}
            Exits: {exits}
            Action: {context.Action.RawInput}
            Action Outcome: {(context.MechanicalResult.Success ? "success" : "failure")}
            Resolved Outcome: {resolvedOutcome}
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
    /// Specialized narration for room arrivals. Produces atmospheric first impressions —
    /// NPC reactions, sensory details, things that catch the eye — rather than repeating
    /// the room description (which is shown in the room panel).
    /// </summary>
    private async Task<string> NarrateRoomArrivalAsync(NarratorContext context, CancellationToken ct)
    {
        var systemPrompt = """
            You are the narrator of a dark-fantasy text adventure with the comic sensibility of
            classic Sierra point-and-click games (Quest for Glory, King's Quest, Space Quest).

            The player just walked into a new room. The room's NAME and DESCRIPTION are already
            displayed in a separate panel — DO NOT repeat them. Instead, narrate the ARRIVAL MOMENT:

            WHAT TO INCLUDE (pick 2-3, not all):
            - A sensory hit: the first thing the player smells, hears, or feels on their skin.
            - NPC reactions: does anyone look up? Ignore them? Reach for a weapon? Offer a drink?
            - Something that catches the eye: a glint, a stain, something out of place.
            - Atmosphere/mood: the vibe of the space as you step in. Tension, warmth, dread, boredom.
            - A brief transition beat: how the previous space gives way to this one.

            VOICE:
            - Dry, sardonic Sierra wit. Vivid but concise.
            - Write 2-3 sentences. This is a quick establishing shot, not a novel paragraph.
            - Use the player's character name.
            - Never say "You enter [room name]" or "You find yourself in [description]."
              The player already knows where they are from the room panel.
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

        var userPrompt = $"""
            Player: {context.Player.Name} ({context.Player.Race} {context.Player.Class}, Level {context.Player.Level})
            Arrived at: {context.CurrentRoom.Name}
            Room vibe: {context.CurrentRoom.Description}
            Environment: {envTags}
            NPCs present: {npcsPresent}
            Items visible: {notableItems}
            Direction traveled: {context.Action.Direction ?? "unknown"}
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

    /// <summary>Builds a reasonable offline fallback for room arrivals.</summary>
    private static string BuildArrivalFallback(NarratorContext context)
    {
        var room = context.CurrentRoom;
        var player = context.Player;
        var parts = new List<string>();

        if (room.Npcs.Count > 0)
        {
            var firstNpc = room.Npcs[0];
            parts.Add($"{firstNpc.Name} glances up as {player.Name} steps in.");
        }
        else
        {
            parts.Add($"{player.Name} arrives to find the place empty — or at least, that's how it looks.");
        }

        if (room.Items.Count > 0)
        {
            var item = room.Items[0];
            parts.Add($"A {item.Name.ToLowerInvariant()} catches the eye.");
        }

        if (room.Exits.Count > 2)
        {
            parts.Add($"Paths lead {string.Join(", ", room.Exits.Keys)}.");
        }

        return string.Join(" ", parts);
    }

    public async Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default)
    {
        var systemPrompt = """
            You are a world-building engine for a dark fantasy RPG with the flavor of classic Sierra adventures.
            Generate a room/location as JSON with these exact fields:
            {
              "name": "Short Evocative Name",
              "description": "2-3 atmospheric sentences. Hint at things worth interacting with.",
              "environmentTags": ["tag1", "tag2"],
              "npcs": [{ "id": "unique_id", "name": "NPC Name", "personality": "one-line personality with attitude", "isHostile": false }],
              "items": [{ "name": "Item Name", "description": "brief flavor text", "type": "Misc", "quantity": 1 }]
            }

            RULES:
            - Keep it consistent with the source location's theme and direction of travel.
            - Include 0-2 NPCs with distinct, memorable personalities (grumpy, paranoid, flirty, etc.)
            - Include 1-3 items that make sense for the location (weapons, potions, gold, curiosities, junk)
            - Each item must have a "type" field: one of "Weapon", "Armor", "Shield", "Helmet", "Potion", "Scroll", "Key", "QuestItem", or "Misc"
            - Descriptions should hint at interactive elements: things to search, people to talk to, paths to explore.
            - Add 2-4 environment tags for mood/theme (e.g. "dark", "tavern", "forest", "ruins", "marketplace").
            - Name should be evocative, not generic ("The Butcher's Alley" not "Alley").
            - Vary between indoor/outdoor, populated/desolate, safe/dangerous.
            - Return ONLY valid JSON, no markdown, no code fences.
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
                        IsHostile = n.IsHostile
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
                            IsEquippable = InventoryItem.IsEquippableType(itemType)
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

        var systemPrompt = """
            You are the Game Master for a dark-fantasy text-adventure RPG with the comic sensibility
            of classic Sierra point-and-click adventures (Quest for Glory, King's Quest, Space Quest).

            VOICE & TONE:
            - Dry, sardonic wit. You love the absurd. Channel the narrator from Space Quest who delights
              in describing your failures in excruciating, hilarious detail.
            - Failures should be FUNNY. Slapstick, ironic consequences, bystander reactions, deadpan commentary.
              Never just "nothing happens." If someone tries to pee on a gate, describe the awkward attempt,
              the wind direction, a guard's horrified expression — make the player laugh even when they fail.
            - Successes can be cool but with a wry edge — the universe is amused by heroism.
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
            - Equipment slots: Weapon, Armor, Shield, Helmet.
            - Prefer small, credible state changes. Risky actions can fail or partially succeed.
            - Be fair — no free loot, no instant kills. But make the journey entertaining.
            - Leave statChanges empty unless a concrete resource actually changed.
            - Leave inventoryChanges empty unless an item was clearly gained or lost.
            - Leave entityChanges empty unless the player directly interacted with that entity.
            - Leave roomChanges null unless the environment itself clearly changed.
            - Do NOT reinterpret body-part words or casual verbs as touching unrelated objects.

            Respond with ONLY valid JSON in this exact shape (no markdown, no code fences):
            {
              "narration": "Your dramatic narration here.",
              "success": true,
              "statChanges": { "hp": -5 },
              "inventoryChanges": [{ "action": "add", "itemName": "Rusty Key", "quantity": 1 }],
              "entityChanges": [{ "entityType": "npc", "action": "remove", "name": "Goblin Scout", "properties": {} }],
              "combatInitiated": false,
              "roomChanges": null
            }

            Fields:
            - statChanges: dictionary of stat deltas (hp, mp, gold, xp). Omit unchanged stats.
            - inventoryChanges: array of {action: "add"|"remove", itemName, quantity}. Empty array if none.
            - entityChanges: array of {entityType: "npc"|"item", action: "add"|"remove"|"update", name, properties}. Empty array if none.
            - combatInitiated: true only if the action triggers combat with someone.
            - roomChanges: null unless the action changes the room description or reveals new exits.
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
            {loreContext}

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

        var systemPrompt = $$"""
            You are now voicing {{npc.Name}} in direct conversation with the player.
            Channel the memorable NPCs of classic Sierra adventure games — each person has a distinct
            voice, quirks, and opinions. They are NOT quest dispensers; they are characters with lives.

            VOICE:
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
              }
            }
            """;

        var npcKnowledge = await GetNpcKnowledgeAsync(npc, room, ct);

        var userPrompt = $$"""
            Player: {{player.Name}} (Lv.{{player.Level}} {{player.Race}} {{player.Class}})
            {{player.FormatStatsCompact()}}
            Location: {{room.Name}} — {{room.Description}}
            Turn {{interaction.TurnCount + 1}} of conversation with {{npc.Name}}.
            Player says/does: "{{rawInput}}"
            {{npcKnowledge}}
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

        // Deterministic fallback — NPC responds generically
        return new FreeFormResponse
        {
            Success = true,
            Narration = $"{npc.Name} regards {player.Name} thoughtfully for a moment, then offers a noncommittal response that neither invites nor discourages further conversation.",
            InteractionUpdate = new InteractionUpdate
            {
                Mode = InteractionMode.Conversation,
                NpcDisposition = interaction.NpcDisposition ?? npc.Disposition,
                Context = [$"Player said: {rawInput}. {npc.Name} gave a guarded response."]
            }
        };
    }

    public async Task<FreeFormResponse> ProcessCombatTurnAsync(PlayerCharacter player, Room room, Npc enemy, InteractionState interaction, string rawInput, CancellationToken ct = default)
    {
        var combatMemory = enemy.DispositionState.MemoryFlags.Count > 0
            ? $"\n            Enemy memory: {string.Join(", ", enemy.DispositionState.MemoryFlags)}"
            : "";

        var systemPrompt = $$"""
            Combat is active against {{enemy.Name}}.
            Enemy HP: {{enemy.Hp ?? 0}}/{{enemy.MaxHp ?? 0}}
            Player HP: {{player.Hp}}/{{player.MaxHp}}
            Enemy faction: {{enemy.Faction}}{{combatMemory}}

            VOICE: Narrate combat with dramatic flair and dark humor. Misses should be entertaining —
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

        var userPrompt = $$"""
            Player: {{player.Name}} (Lv.{{player.Level}} {{player.Race}} {{player.Class}})
            HP: {{player.Hp}}/{{player.MaxHp}} | MP: {{player.Mp}}/{{player.MaxMp}}
            {{player.FormatStatsCompact()}}
            Weapon: {{player.Equipment.MainHand?.Name ?? "Fists"}} ({{player.Equipment.MainHand?.DamageDice ?? "1d4"}})
            Location: {{room.Name}}
            Combat turn {{interaction.TurnCount + 1}} vs {{enemy.Name}} (HP: {{enemy.Hp ?? 0}}/{{enemy.MaxHp ?? 0}}, Defense: {{enemy.Defense ?? 10}})
            Player action: "{{rawInput}}"
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
                Context = [$"Turn {interaction.TurnCount + 1}: {player.Name} acted ({rawInput}). Both sides exchanged blows."]
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
        // Simple string replacement — "narrative" is not a substring of any other expected key
        if (json.Contains("\"narrative\""))
            json = json.Replace("\"narrative\"", "\"narration\"");
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
                             "interactionUpdate"];

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

    private static string FallbackNarration() =>
        "The scene settles into a plain, unadorned stillness for a moment, yielding only the bare facts.";

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
                    ? $"Though {matchingCount} figures answer to that same silhouette, {context.Player.Name} fixes on one representative {npc.Name.ToLowerInvariant()} at the edge of the room."
                    : $"{context.Player.Name} fixes their attention on {npc.Name}.";
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
                    ? $"Among the {item.Name} scattered through the room, {context.Player.Name} picks one out for closer study."
                    : $"{context.Player.Name} gives {item.Name} a closer look.";
                var detail = !string.IsNullOrWhiteSpace(item.Description)
                    ? item.Description.TrimEnd('.')
                    : "its shape suggests use before beauty, the kind of tool made to matter more in the hand than on display";
                narration = $"{lead} {char.ToUpperInvariant(detail[0])}{detail[1..]}. In a place like {room.Name}, even a small object feels chosen rather than forgotten.";
                return true;
            }

            if (!string.IsNullOrWhiteSpace(target))
            {
                narration = $"{context.Player.Name} searches for {target}, but the name never quite lands on anything solid in {room.Name}. {BuildAmbientMissSentence(room)}";
                return true;
            }
        }

        var roomAtmosphere = BuildRoomAtmosphere(room);
        var focalDetail = BuildRoomFocalDetail(room);
        var exitDetail = BuildExitDetail(room);

        narration = $"{context.Player.Name} slows and lets {room.Name} resolve around them. {roomAtmosphere}";
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

        var roomName = string.IsNullOrWhiteSpace(context.CurrentRoom.Name) ? "the room" : context.CurrentRoom.Name;
        if (!context.MechanicalResult.Success)
        {
            var failureReason = TrimToSentence(context.MechanicalResult.MechanicalSummary);
            return $"{context.Player.Name} commits to the motion, but {roomName} gives nothing back except the hard truth of the attempt. {failureReason}";
        }

        var resolvedOutcome = TrimToSentence(context.MechanicalResult.MechanicalSummary);
        return string.IsNullOrWhiteSpace(resolvedOutcome)
            ? $"{context.Player.Name} shifts the scene in {roomName}, and the moment settles into a new shape without further ceremony."
            : $"{context.Player.Name} acts, and {roomName} answers in kind. {resolvedOutcome}";
    }

    private static string BuildRoomAtmosphere(Room room)
    {
        var description = TrimToSentence(room.Description);
        var roomText = $"{room.Name} {room.Description}".ToLowerInvariant();

        if (roomText.Contains("qa") || roomText.Contains("lab") || roomText.Contains("sterile") || roomText.Contains("fixture") || roomText.Contains("test"))
        {
            return "It feels less like a chamber built for comfort and more like a proving ground left humming between trials, all cold light, hard edges, and patient machinery.";
        }

        if (roomText.Contains("inn") || roomText.Contains("tavern"))
        {
            return "Warmth clings to the place in stubborn pockets, softening the rougher smells and sounds that travel in from the road.";
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return "The place keeps its details close, revealing itself through echo, shadow, and the pressure of the air more than any easy explanation.";
        }

        return $"The place carries the character of {description.TrimEnd('.').ToLowerInvariant()}, with every sound lingering just long enough to make the stillness feel deliberate.";
    }

    private static string BuildRoomFocalDetail(Room room)
    {
        var details = new List<string>();

        if (room.Npcs.Count > 0)
            details.Add($"The eye keeps returning to {SummarizeEntities(room.Npcs, npc => npc.Name)}, whose presence gives the room its weight");

        if (room.Items.Count > 0)
            details.Add($"{SummarizeEntities(room.Items, item => item.Name, item => item.Quantity)} glints with the promise of use or trouble");

        return details.Count switch
        {
            0 => string.Empty,
            1 => details[0] + ".",
            _ => details[0] + ", while " + details[1] + "."
        };
    }

    private static string BuildExitDetail(Room room)
        => room.Exits.Count > 0
            ? $"Only {HumanizeDirections(room.Exits.Keys)} offers a clean way onward."
            : "No obvious road onward presents itself at first glance.";

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

    private class GeneratedNpc
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Personality { get; set; }
        public string? Faction { get; set; }
        public bool IsHostile { get; set; }
        public int Level { get; set; } = 1;
    }

    private class GeneratedItem
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Type { get; set; }
        public int Quantity { get; set; } = 1;
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
}
