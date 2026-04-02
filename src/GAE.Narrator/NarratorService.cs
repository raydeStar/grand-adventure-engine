using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Narrator;

public class NarratorService : INarratorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NarratorService> _logger;
    private readonly WorldKnowledgeBuilder? _knowledge;
    private string _model;
    private bool _modelResolved;

    public NarratorService(HttpClient httpClient, ILogger<NarratorService> logger, string model = "default", WorldKnowledgeBuilder? knowledge = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _knowledge = knowledge;
        _model = model;
        _modelResolved = !string.Equals(model, "default", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default)
    {
        if (TryBuildDeterministicLookNarration(context, out var lookNarration))
            return lookNarration;

        var systemPrompt = """
            You are Sir Thaddeus, the Grand Narrator of the Shattered Reaches.
            You speak in a dramatic, literary style with touches of dry wit.
            You narrate what the engine has already decided — never contradict the mechanical result.
            Write 2-4 vivid sentences with concrete sensory detail and at least one specific visual focal point.
            If the action failed, narrate the failed attempt in-world and honor the exact failure reason without repeating blunt system text verbatim.
            If combat occurred, describe the action dramatically.
            Always address the player's character by name.
            Never ask the player questions.
            Never mention prompts, systems, mechanics, waiting for results, or that you are narrating.
            For movement, acknowledge the transition and the new location.
            For direct actions, describe only consequences that are already supported by the supplied outcome and room context.
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
            return await CompletionOrThrowAsync(systemPrompt, userPrompt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio request failed, using contextual narration fallback");
            return BuildContextualFallbackNarration(context);
        }
    }

    public async Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default)
    {
        var systemPrompt = """
            You are a world-building engine for a dark fantasy RPG.
            Generate a room/location as JSON with these fields:
            { "name": "...", "description": "2-3 sentences", "environmentTags": ["tag1", "tag2"],
              "npcs": [{ "id": "snake_id", "name": "...", "personality": "...", "isHostile": false }] }
            Keep it consistent with the source location's theme.
            """;

        var userPrompt = $"""
            The player is moving {direction} from "{sourceRoom.Name}" ({string.Join(", ", sourceRoom.EnvironmentTags)}).
            Generate a new location for room ID "{roomId}".
            """;

        try
        {
            var json = await CompletionAsync(systemPrompt, userPrompt, ct);
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
            var json = await CompletionAsync(systemPrompt, userPrompt, ct);
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
        return await CompletionAsync(systemPrompt, subject, ct);
    }

    public async Task<string> GenerateBackstoryAsync(CharacterConcept concept, CancellationToken ct = default)
    {
        var systemPrompt = "Generate a 2-3 sentence backstory for a dark fantasy RPG character. Be dramatic but concise.";
        var userPrompt = $"Character: {concept.Name}, a {concept.Race} {concept.Class}. Additional context: {concept.Backstory}";
        return await CompletionAsync(systemPrompt, userPrompt, ct);
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
            var result = await CompletionAsync(systemPrompt, rawInput, ct);
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
        if (TryBuildLowStakesFreeFormResponse(player, room, rawInput, out var lowStakesResponse))
        {
            _logger.LogInformation("Resolved low-stakes free-form action locally for {PlayerId}: {RawInput}", player.Id, rawInput);
            return lowStakesResponse;
        }

        var systemPrompt = """
            You are the AI Game Master for a dark-fantasy text-adventure RPG.
            The player typed an action that is NOT a recognized system command.
            You must simulate the outcome as a fair, creative dungeon master.

            GAME MASTER INSTRUCTIONS:
            - Resolve the action using the supplied Character Definition Card, current room state, and recent history.
            - Keep continuity with the world state. Do not invent items, exits, wounds, or NPCs that contradict the provided context.
            - Use the character attributes naturally: STR for force, DEX for agility/precision, CON for endurance, INT for reasoning or spellcraft, WIS for perception/judgment, CHA for social pressure, LUCK for chance.
            - Equipment slots available to this game are Weapon, Armor, Shield, and Helmet.
            - Prefer small, credible state changes over wild swings. Risky actions can fail or partially succeed.
            - For low-stakes emotes, jokes, or bodily actions, keep the outcome literal and local.
            - Do NOT reinterpret body-part words or casual verbs as touching unrelated objects or NPC possessions.
            - Most low-stakes actions should not change stats, inventory, room layout, exits, or NPC rosters.

            RULES:
            - Narrate the outcome in 2-4 dramatic sentences.
            - Determine mechanical consequences: stat changes, item gains/losses, NPC reactions, room changes.
            - Be fair — do not give free loot or instant kills. Risky actions should sometimes fail.
            - Respect the player's current state (HP, inventory, location).
            - If the action is impossible or absurd, narrate a humorous failure but do NOT punish harshly.
            - Leave statChanges empty unless a concrete resource changed.
            - Leave inventoryChanges empty unless an item was clearly gained or lost.
            - Leave entityChanges empty unless the player directly interacted with that entity and the change is obvious.
            - Leave roomChanges null unless the environment itself clearly changed.

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
            ? SummarizeEntities(room.Npcs, npc => npc.IsHostile ? $"{npc.Name} (hostile)" : npc.Name)
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
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "free-form", logPayload: true);

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
        var systemPrompt = $$"""
            You are now voicing {{npc.Name}} in direct conversation with the player.
            You MUST:

            1. Write the NPC's actual dialogue in quotes.
            2. Include the NPC's physical reactions and body language.
            3. Track the NPC's emotional state. Start at their current disposition ({{interaction.NpcDisposition ?? npc.Disposition}}) and shift it based on what the player says.
            4. Return an updated disposition in your response from: "friendly", "neutral", "annoyed", "angry", "hostile", "amused", "flirtatious", "scared", "sad", "suspicious".
            5. If the conversation reaches a natural end (NPC dismisses the player, player says goodbye, NPC storms off), return mode: "explore" to exit conversation mode.
            6. If the player tries to LEAVE mid-conversation, narrate the NPC's reaction to being cut off.
            7. NPCs can reveal information, offer quests, give items, refuse service, call guards, or attack — based on their disposition and what the player says.

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
                "enemyUpdate": {}
              }
            }
            """;

        var npcKnowledge = await GetNpcKnowledgeAsync(npc, room, ct);

        var userPrompt = $$"""
            Player: {{player.Name}} (Lv.{{player.Level}} {{player.Race}} {{player.Class}})
            Location: {{room.Name}} — {{room.Description}}
            Turn {{interaction.TurnCount + 1}} of conversation with {{npc.Name}}.
            Player says/does: "{{rawInput}}"
            {{npcKnowledge}}
            """;

        string? rawCompletion = null;
        try
        {
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "conversation", logPayload: true);
            if (TryParseFreeFormResponse(rawCompletion, out var response))
                return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversation narration failed for \"{RawInput}\"", rawInput);
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
        var systemPrompt = $$"""
            Combat is active against {{enemy.Name}}.
            Enemy HP: {{enemy.Hp ?? 0}}/{{enemy.MaxHp ?? 0}}
            Player HP: {{player.Hp}}/{{player.MaxHp}}

            1. Resolve the player's action. Calculate hit/miss using relevant stat + random factor vs enemy defense.
            2. Narrate the player's action dramatically.
            3. Then resolve the enemy's turn. The enemy acts tactically based on its type.
            4. Narrate the enemy's action.
            5. Return all stat changes for BOTH sides.
            6. If either side reaches 0 HP, end combat. Award XP and loot for victory. Narrate death for defeat.

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
                "enemyUpdate": { "hp": -8 }
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
            Weapon: {{player.Equipment.Weapon?.Name ?? "Fists"}} ({{player.Equipment.Weapon?.DamageDice ?? "1d4"}})
            Location: {{room.Name}}
            Combat turn {{interaction.TurnCount + 1}} vs {{enemy.Name}} (HP: {{enemy.Hp ?? 0}}/{{enemy.MaxHp ?? 0}}, Defense: {{enemy.Defense ?? 10}})
            Player action: "{{rawInput}}"
            {{loreContext}}
            """;

        string? rawCompletion = null;
        try
        {
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "combat", logPayload: true);
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

    private static bool TryParseFreeFormResponse(string rawCompletion, out FreeFormResponse response)
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
            var parsed = JsonSerializer.Deserialize<FreeFormResponse>(rawCompletion, _jsonOptions);
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

    private async Task<string> CompletionAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        try
        {
            return await CompletionOrThrowAsync(systemPrompt, userPrompt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LM Studio request failed, using fallback narration");
            return FallbackNarration();
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

    private async Task<string> CompletionOrThrowAsync(string systemPrompt, string userPrompt, CancellationToken ct, string operation = "completion", bool logPayload = false)
    {
        await ResolveModelAsync(ct);

        var request = new LmStudioRequest
        {
            Model = _model,
            Messages =
            [
                new LmStudioMessage { Role = "system", Content = systemPrompt },
                new LmStudioMessage { Role = "user", Content = userPrompt }
            ],
            Temperature = 0.8,
            MaxTokens = 2048
        };

        if (logPayload)
        {
            _logger.LogInformation("LM Studio {Operation} request payload: {Payload}", operation, JsonSerializer.Serialize(request, _jsonOptions));
        }

        using var response = await _httpClient.PostAsJsonAsync("v1/chat/completions", request, _jsonOptions, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (logPayload)
        {
            _logger.LogInformation("LM Studio {Operation} response status {StatusCode}: {Body}", operation, (int)response.StatusCode, responseBody);
        }

        response.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<LmStudioResponse>(responseBody, _jsonOptions);
        var content = result?.Choices?.FirstOrDefault()?.Message?.Content;
        return !string.IsNullOrWhiteSpace(content)
            ? content
            : throw new InvalidOperationException($"LM Studio {operation} response did not contain a message body.");
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
    {
        if (player.Equipment.Weapon is not null)
            yield return player.Equipment.Weapon;

        if (player.Equipment.Armor is not null)
            yield return player.Equipment.Armor;

        if (player.Equipment.Shield is not null)
            yield return player.Equipment.Shield;

        if (player.Equipment.Helmet is not null)
            yield return player.Equipment.Helmet;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // LM Studio OpenAI-compatible DTOs
    private class LmStudioRequest
    {
        public string Model { get; set; } = "default";
        public List<LmStudioMessage> Messages { get; set; } = [];
        public double Temperature { get; set; } = 0.8;
        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 2048;
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
    }

    private class GeneratedRoom
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<string>? EnvironmentTags { get; set; }
        public List<GeneratedNpc>? Npcs { get; set; }
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
}
