using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Narrator;

/// <summary>
/// Pre-fetches world lore from Wiki.js and builds context strings
/// for narrator prompts. Nullable in NarratorService — if wiki is
/// down, the narrator still works, just without lore context.
/// </summary>
public class WorldKnowledgeBuilder
{
    private readonly IWikiService _wiki;
    private readonly ILogger<WorldKnowledgeBuilder> _logger;

    public WorldKnowledgeBuilder(IWikiService wiki, ILogger<WorldKnowledgeBuilder> logger)
    {
        _wiki = wiki;
        _logger = logger;
    }

    /// <summary>
    /// Builds general world knowledge context for a room and its surroundings.
    /// Used by NarrateAction, ProcessFreeForm, etc.
    /// </summary>
    public async Task<string> BuildContextAsync(Room room, CancellationToken ct = default)
    {
        var sections = new List<string>();

        // Fetch room's wiki page if it exists
        var roomContent = await _wiki.GetPageContentAsync($"rooms/{room.Id}", ct);
        if (roomContent is not null)
            sections.Add($"[Room Lore: {room.Name}]\n{TrimToMaxLines(roomContent, 20)}");

        // Fetch NPC pages for NPCs in the room
        foreach (var npc in room.Npcs.Take(5)) // cap to avoid huge context
        {
            var npcContent = await _wiki.GetPageContentAsync($"npcs/{npc.Id}", ct);
            if (npcContent is not null)
                sections.Add($"[NPC: {npc.Name}]\n{TrimToMaxLines(npcContent, 10)}");
        }

        // Search for relevant region/faction lore based on environment tags
        if (room.EnvironmentTags.Count > 0)
        {
            var tagQuery = string.Join(" ", room.EnvironmentTags.Take(3));
            var searchResults = await _wiki.SearchAsync(tagQuery, ct);
            foreach (var result in searchResults.Take(3))
            {
                if (!string.IsNullOrWhiteSpace(result.Description))
                    sections.Add($"[Lore: {result.Title}]\n{result.Description}");
            }
        }

        // Fetch recent events for this room
        var eventPages = await _wiki.GetPagesAsync($"events/npc/", ct);
        // Also check room-specific events
        var roomEvents = await _wiki.GetPagesAsync($"events/player/", ct);

        var recentEvents = eventPages.Concat(roomEvents)
            .OrderByDescending(p => p.Path) // paths include timestamps, so this sorts by recency
            .Take(3);

        foreach (var evt in recentEvents)
        {
            var content = await _wiki.GetPageContentAsync(evt.Path, ct);
            if (content is not null)
                sections.Add($"[Recent Event: {evt.Title}]\n{TrimToMaxLines(content, 5)}");
        }

        if (sections.Count == 0)
            return "";

        return "WORLD KNOWLEDGE (from the living chronicle):\n" + string.Join("\n\n", sections);
    }

    /// <summary>
    /// Builds knowledge context scoped to what a specific NPC would know,
    /// filtered by their KnowledgeScopes tags.
    /// </summary>
    public async Task<string> BuildScopedContextAsync(Npc npc, Room room, CancellationToken ct = default)
    {
        var scopes = npc.KnowledgeScopes;
        if (scopes.Count == 0)
        {
            // NPC with no scopes gets basic room awareness only
            return $"[{npc.Name} has limited knowledge — they know only what they can see in {room.Name}.]";
        }

        var sections = new List<string>();

        // NPC's own page
        var npcContent = await _wiki.GetPageContentAsync($"npcs/{npc.Id}", ct);
        if (npcContent is not null)
            sections.Add($"[Self-Knowledge: {npc.Name}]\n{TrimToMaxLines(npcContent, 8)}");

        // Search wiki for each knowledge scope tag
        foreach (var scope in scopes.Take(5))
        {
            var results = await _wiki.SearchAsync(scope, ct);
            foreach (var result in results.Take(2))
            {
                var content = await _wiki.GetPageContentAsync(result.Path, ct);
                if (content is not null)
                    sections.Add($"[{npc.Name} knows about: {result.Title}]\n{TrimToMaxLines(content, 8)}");
            }
        }

        if (sections.Count == 0)
            return $"[{npc.Name} has limited knowledge — their scopes ({string.Join(", ", scopes)}) returned no lore.]";

        return $"NPC KNOWLEDGE (what {npc.Name} knows):\n" + string.Join("\n\n", sections);
    }

    /// <summary>
    /// Searches wiki for context relevant to a specific query/topic.
    /// Used when the narrator needs ad-hoc knowledge lookup.
    /// </summary>
    public async Task<string> SearchContextAsync(string query, CancellationToken ct = default)
    {
        var results = await _wiki.SearchAsync(query, ct);
        if (results.Count == 0)
            return "";

        var sections = new List<string>();
        foreach (var result in results.Take(3))
        {
            var content = await _wiki.GetPageContentAsync(result.Path, ct);
            if (content is not null)
                sections.Add($"[{result.Title}]\n{TrimToMaxLines(content, 10)}");
            else if (!string.IsNullOrWhiteSpace(result.Description))
                sections.Add($"[{result.Title}]\n{result.Description}");
        }

        return sections.Count > 0
            ? "RELEVANT LORE:\n" + string.Join("\n\n", sections)
            : "";
    }

    private static string TrimToMaxLines(string content, int maxLines)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines)
            return content;

        return string.Join('\n', lines.Take(maxLines)) + "\n[...truncated]";
    }
}
