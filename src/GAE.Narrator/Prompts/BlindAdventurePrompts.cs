using System.Text.Json;

namespace GAE.Narrator.Prompts;

/// <summary>
/// Centralized prompt templates and deterministic fallbacks for Blind Adventure narration flows.
/// These prompts are kept separate from the main narrator logic so future engine work can reuse them
/// without embedding large prompt strings inside request handlers.
/// </summary>
public static class BlindAdventurePrompts
{
    /// <summary>
    /// Builds the system prompt for Blind Adventure room generation.
    /// </summary>
    public static string BuildRoomGenerationSystemPrompt() => """
        You are Sir Thaddeus, generating the next room for a Blind Adventure text RPG.

        Return ONLY valid JSON with this exact shape:
        {
          "name": "Short evocative room name",
          "description": "2-4 sentences of grounded atmospheric description",
          "exits": ["north", "east"],
          "npcs": [
            {
              "name": "NPC name",
              "description": "one short sentence about who they are or how they read",
              "disposition": "friendly|neutral|wary|hostile"
            }
          ],
          "items": [
            {
              "name": "Item name",
              "description": "brief sensory detail",
              "type": "weapon|armor|potion|key|quest_item|misc"
            }
          ]
        }

        RULES:
        - Match the storyline's setting, tone, and theme.
        - Weave in the next plot beat only if it fits naturally in this room.
        - Exits are directions only. Do not invent room IDs, coordinates, maps, or unseen linked rooms.
        - Do not include stat blocks, hit points, attack values, defenses, or other engine mechanics.
        - Keep the room immediately playable: something to inspect, notice, or interact with.
        - NPCs and items are optional. Prefer zero entries over filler.
        - Descriptions should be concrete and atmospheric, never generic filler.
        """;

    /// <summary>
    /// Builds the user prompt for Blind Adventure room generation.
    /// </summary>
    public static string BuildRoomGenerationUserPrompt(
        string currentRoomName,
        string currentRoomDescription,
        IReadOnlyList<string> visibleExits,
        IReadOnlyList<string> environmentTags,
        string direction,
        string storylineName,
        string setting,
        string tone,
        string theme,
        IReadOnlyList<string> plotBeats,
        IReadOnlyList<string> visitedRoomSummaries,
        string? nextPlotBeat,
        int roomsRemaining)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoomName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoomDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(storylineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(tone);
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);

        return $"""
            Storyline: {storylineName}
            Setting: {setting}
            Tone: {tone}
            Theme: {theme}
            Rooms remaining before the story should start wrapping up: {Math.Max(0, roomsRemaining)}
            Full plot beat list:
            {FormatBullets(plotBeats, "- none supplied")}

            Current room:
            - Name: {currentRoomName}
            - Description: {currentRoomDescription}
            - Visible exits: {FormatInlineList(visibleExits, "none")}
            - Environment tags: {FormatInlineList(environmentTags, "none")}

            The player moves {direction} into unexplored space.
            Recently visited rooms:
            {FormatBullets(visitedRoomSummaries, "- no prior rooms beyond the current location")}

            Next plot beat to consider:
            {(string.IsNullOrWhiteSpace(nextPlotBeat) ? "- none; focus on atmosphere and forward motion" : $"- {nextPlotBeat}")}

            Generate the next room so it feels like a natural continuation of this adventure.
            """;
    }

    /// <summary>
    /// Returns deterministic fallback JSON for Blind Adventure room generation failures.
    /// </summary>
    public static string BuildRoomGenerationFallback(
        string direction,
        string storylineName,
        string setting,
        string tone,
        int roomsRemaining)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(storylineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(tone);

        var opposite = OppositeDirection(direction);
        var exits = new List<string> { opposite };
        if (roomsRemaining > 1)
            exits.Add("forward");

        var payload = new
        {
            name = "The Next Uncertain Chamber",
            description = $"You press deeper into {storylineName}. The place carries the weight of {setting.ToLowerInvariant()}, and the mood stays {tone.ToLowerInvariant()}. Even in failure, the path ahead feels deliberate rather than empty.",
            exits,
            npcs = Array.Empty<object>(),
            items = Array.Empty<object>()
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Builds the system prompt for integrating a plot beat into the current Blind Adventure scene.
    /// </summary>
    public static string BuildPlotBeatIntegrationSystemPrompt() => """
        You are Sir Thaddeus, weaving the next major plot beat into an ongoing Blind Adventure.

        Write 2-3 sentences of narration only.

        RULES:
        - Integrate the supplied plot beat organically into the current scene.
        - Keep the established tone and theme intact.
        - Never announce this as a "plot beat" or make it feel mechanically inserted.
        - Do not ask the player a question.
        - Do not mention systems, prompts, IDs, stats, or hidden game logic.
        """;

    /// <summary>
    /// Builds the user prompt for plot beat integration.
    /// </summary>
    public static string BuildPlotBeatIntegrationUserPrompt(
        string currentRoomName,
        string currentSceneSummary,
        string storylineName,
        string tone,
        string theme,
        string plotBeat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentRoomName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSceneSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(storylineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tone);
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        ArgumentException.ThrowIfNullOrWhiteSpace(plotBeat);

        return $"""
            Storyline: {storylineName}
            Tone: {tone}
            Theme: {theme}
            Current room: {currentRoomName}
            Current scene summary: {currentSceneSummary}
            Plot beat to weave in: {plotBeat}

            Deliver the beat subtly, as part of the scene already unfolding.
            """;
    }

    /// <summary>
    /// Returns deterministic fallback narration for plot beat integration failures.
    /// </summary>
    public static string BuildPlotBeatFallbackNarration(string storylineName, string tone, string plotBeat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storylineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tone);
        ArgumentException.ThrowIfNullOrWhiteSpace(plotBeat);

        return $"In {storylineName}, the air tightens with {tone.ToLowerInvariant()} intent as one truth edges into view: {plotBeat}. The revelation arrives like something that was always waiting in the dark for its proper hour.";
    }

    /// <summary>
    /// Builds the system prompt for Blind Adventure conclusions.
    /// </summary>
    public static string BuildAdventureConclusionSystemPrompt() => """
        You are Sir Thaddeus, concluding a Blind Adventure text RPG.

        Return ONLY valid JSON with this exact shape:
        {
          "narration": "2-4 sentences of ending narration",
          "summary": "1-2 sentences summarizing what the player achieved or failed to achieve"
        }

        RULES:
        - Land the ending with finality and emotional clarity.
        - Use the storyline's tone and theme.
        - Reference visited places and key events only in player-facing language.
        - Do not mention systems, prompts, IDs, room counts, or engine mechanics.
        """;

    /// <summary>
    /// Builds the user prompt for Blind Adventure conclusions.
    /// </summary>
    public static string BuildAdventureConclusionUserPrompt(
        string storylineName,
        string setting,
        string tone,
        string theme,
        IReadOnlyList<string> visitedRooms,
        IReadOnlyList<string> keyEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storylineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(tone);
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);

        return $"""
            Storyline: {storylineName}
            Setting: {setting}
            Tone: {tone}
            Theme: {theme}
            Important places visited:
            {FormatBullets(visitedRooms, "- no named locations recorded")}

            Key events to resolve:
            {FormatBullets(keyEvents, "- the journey was quiet but transformative")}

            Conclude the adventure with a fitting ending and a concise summary.
            """;
    }

    /// <summary>
    /// Returns deterministic fallback JSON for Blind Adventure conclusion failures.
    /// </summary>
    public static string BuildAdventureConclusionFallback(
        string storylineName,
        string theme,
        IReadOnlyList<string> keyEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storylineName);
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);

        var resolvedEvent = keyEvents.FirstOrDefault(eventText => !string.IsNullOrWhiteSpace(eventText))
            ?? "the journey leaves its mark";

        var payload = new
        {
            narration = $"The last echoes of {storylineName} settle around you, and the road behind no longer feels uncertain. Whatever else the world may forget, it will remember how {resolvedEvent.ToLowerInvariant()}.",
            summary = $"This adventure closes on the theme of {theme.ToLowerInvariant()}, with the final turning point resting on {resolvedEvent.ToLowerInvariant()}."
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string FormatBullets(IReadOnlyList<string> values, string emptyFallback)
        => values.Count == 0
            ? emptyFallback
            : string.Join(Environment.NewLine, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => $"- {v}"));

    private static string FormatInlineList(IReadOnlyList<string> values, string emptyFallback)
    {
        var filtered = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return filtered.Count == 0 ? emptyFallback : string.Join(", ", filtered);
    }

    private static string OppositeDirection(string direction) => direction.Trim().ToLowerInvariant() switch
    {
        "north" => "south",
        "south" => "north",
        "east" => "west",
        "west" => "east",
        "up" => "down",
        "down" => "up",
        "left" => "right",
        "right" => "left",
        "forward" => "back",
        "back" => "forward",
        _ => "back"
    };
}