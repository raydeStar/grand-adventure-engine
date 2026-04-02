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
    private readonly string _model;

    public NarratorService(HttpClient httpClient, ILogger<NarratorService> logger, string model = "default")
    {
        _httpClient = httpClient;
        _logger = logger;
        _model = model;
    }

    public async Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default)
    {
        if (TryBuildDeterministicLookNarration(context, out var lookNarration))
            return lookNarration;

        var systemPrompt = """
            You are Sir Thaddeus, the Grand Narrator of the Shattered Reaches.
            You speak in a dramatic, literary style with touches of dry wit.
            You narrate what the engine has already decided — never contradict the mechanical result.
            Keep responses to 2-3 sentences. Use vivid sensory detail.
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

        var userPrompt = $"""
            Player: {context.Player.Name} ({context.Player.Race} {context.Player.Class}, Level {context.Player.Level})
            Location: {context.CurrentRoom.Name} — {context.CurrentRoom.Description}
            Visible NPCs: {visibleNpcs}
            Visible Items: {visibleItems}
            Exits: {exits}
            Action: {context.Action.RawInput}
            Resolved Outcome: {resolvedOutcome}
            """;

        return await CompletionAsync(systemPrompt, userPrompt, ct);
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
                return new Npc
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = generated.Name ?? "Stranger",
                    Personality = generated.Personality ?? "",
                    Faction = generated.Faction ?? "neutral",
                    IsHostile = generated.IsHostile,
                    Level = generated.Level
                };
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

        var userPrompt = $"""
            Character Definition Card
            Resources:
            - HP: {player.Hp}/{player.MaxHp}
            - MP: {player.Mp}/{player.MaxMp}
            - XP: {player.Xp}
            Currencies:
            - Gold: {player.Gold}
            Attributes:
            - STR: {player.Str} ({player.GetModifier("str"):+0;-0})
            - DEX: {player.Dex} ({player.GetModifier("dex"):+0;-0})
            - CON: {player.Con} ({player.GetModifier("con"):+0;-0})
            - INT: {player.Int} ({player.GetModifier("int"):+0;-0})
            - WIS: {player.Wis} ({player.GetModifier("wis"):+0;-0})
            - CHA: {player.Cha} ({player.GetModifier("cha"):+0;-0})
            - LUCK: {player.Luck} ({player.GetModifier("luck"):+0;-0})
            Equipment Slots:
            - Weapon / Armor / Shield / Helmet
            Equipped Items: {(string.IsNullOrWhiteSpace(equipmentList) ? "None" : equipmentList)}
            Active Status Effects: {statusList}

            Player: {player.Name} (Lv.{player.Level} {player.Race} {player.Class})
            HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp} | Gold: {player.Gold}
            STR:{player.Str} DEX:{player.Dex} CON:{player.Con} INT:{player.Int} WIS:{player.Wis} CHA:{player.Cha}
            Inventory: {inventoryList}
            Location: {room.Name} — {room.Description}
            Room NPCs: {npcList}
            Room Items: {itemList}
            Exits: {string.Join(", ", room.Exits.Keys)}
            Recent history:
            {recentLines}

            Player action: "{rawInput}"
            """;

        string? rawCompletion = null;

        try
        {
            _logger.LogInformation("Dispatching free-form narrator request for {PlayerId} in room {RoomId}: {RawInput}", player.Id, room.Id, rawInput);
            rawCompletion = await CompletionOrThrowAsync(systemPrompt, userPrompt, ct, operation: "free-form", logPayload: true);

            // Strip markdown code fences if present
            rawCompletion = rawCompletion.Trim();
            if (rawCompletion.StartsWith("```", StringComparison.Ordinal))
            {
                var firstNewline = rawCompletion.IndexOf('\n');
                if (firstNewline > 0) rawCompletion = rawCompletion[(firstNewline + 1)..];
                if (rawCompletion.EndsWith("```", StringComparison.Ordinal)) rawCompletion = rawCompletion[..^3];
                rawCompletion = rawCompletion.Trim();
            }

            var response = JsonSerializer.Deserialize<FreeFormResponse>(rawCompletion, _jsonOptions);
            if (response is not null)
            {
                _logger.LogInformation("Free-form action processed: \"{RawInput}\" -> success={Success}", rawInput, response.Success);
                return response;
            }

            throw new JsonException("LM Studio returned an empty free-form response payload.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Free-form narration failed for \"{RawInput}\". Raw response: {RawResponse}", rawInput, rawCompletion ?? "<no response>");
        }

        return new FreeFormResponse
        {
            Narration = "The narrator's link falters before your action can be resolved. Try again in a moment.",
            Success = false
        };
    }

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

    private async Task<string> CompletionOrThrowAsync(string systemPrompt, string userPrompt, CancellationToken ct, string operation = "completion", bool logPayload = false)
    {
        var request = new LmStudioRequest
        {
            Model = _model,
            Messages =
            [
                new LmStudioMessage { Role = "system", Content = systemPrompt },
                new LmStudioMessage { Role = "user", Content = userPrompt }
            ],
            Temperature = 0.8,
            MaxTokens = 500
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

    private static string FallbackNarration() =>
        "The narrator clears his throat but no words come forth. Perhaps the tale will continue another time...";

    private static bool TryBuildDeterministicLookNarration(NarratorContext context, out string narration)
    {
        narration = string.Empty;
        if (context.Action.Type != ActionType.Look)
            return false;

        var room = context.CurrentRoom;
        if (!string.IsNullOrWhiteSpace(context.Action.Target))
        {
            var target = context.Action.Target.Trim();
            var npc = room.Npcs.FirstOrDefault(candidate => candidate.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (npc is not null)
            {
                narration = string.IsNullOrWhiteSpace(npc.Personality)
                    ? $"{context.Player.Name} studies {npc.Name} for a lingering moment, memorizing every small tell and guarded motion."
                    : $"{context.Player.Name} studies {npc.Name} for a lingering moment and catches the shape of their bearing: {npc.Personality.TrimEnd('.')}.";
                return true;
            }

            var item = room.Items.FirstOrDefault(candidate => candidate.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                var detail = !string.IsNullOrWhiteSpace(item.Description)
                    ? item.Description.TrimEnd('.')
                    : "it looks intact, ordinary, and ready to be used if needed";
                narration = $"{context.Player.Name} gives {item.Name} a careful once-over and notes that {detail}.";
                return true;
            }

            narration = $"{context.Player.Name} searches the room for {target}, but nothing here answers that description.";
            return true;
        }

        var observations = new List<string>();

        if (room.Exits.Count > 0)
            observations.Add($"The clearest way onward lies {HumanizeDirections(room.Exits.Keys)}.");

        if (room.Npcs.Count > 0)
            observations.Add($"{SummarizeEntities(room.Npcs, npc => npc.Name)} stand out immediately.");

        if (room.Items.Count > 0)
            observations.Add($"{SummarizeEntities(room.Items, item => item.Name, item => item.Quantity)} catches the eye.");

        narration = $"{context.Player.Name} pauses and lets {room.Name} come into focus. {TrimToSentence(room.Description)}";
        if (observations.Count > 0)
            narration += " " + string.Join(" ", observations.Take(2));

        return true;
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
        return string.IsNullOrWhiteSpace(summary) ? "- [Room state updated]" : $"- {summary}";
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
        public int MaxTokens { get; set; } = 500;
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
