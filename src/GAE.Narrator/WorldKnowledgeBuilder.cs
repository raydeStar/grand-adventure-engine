using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Narrator;

/// <summary>
/// Builds world knowledge context for narrator prompts by pulling
/// relevant lore entries from the content registry.
/// </summary>
public class WorldKnowledgeBuilder
{
    private readonly ILogger<WorldKnowledgeBuilder> _logger;
    private readonly IContentRegistryService? _registry;

    public WorldKnowledgeBuilder(ILogger<WorldKnowledgeBuilder> logger, IContentRegistryService? registry = null)
    {
        _logger = logger;
        _registry = registry;
    }

    /// <summary>
    /// Build room-level lore context: finds lore entries linked to this room,
    /// matching its environment tags, or about its region.
    /// </summary>
    public Task<string> BuildContextAsync(Room room, CancellationToken ct = default)
    {
        if (_registry is null) return Task.FromResult(string.Empty);

        try
        {
            var allLore = _registry.LoreEntries.GetAll();
            var hints = new List<string>();

            foreach (var entry in allLore)
            {
                if (string.IsNullOrWhiteSpace(entry.NarratorHint)) continue;

                // Direct link to this room
                if (entry.LinkedEntityIds.Any(id => id.Equals(room.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    hints.Add(entry.NarratorHint);
                    continue;
                }

                // Tag overlap: lore tags matching room environment tags
                if (entry.Tags.Count > 0 && room.EnvironmentTags.Count > 0)
                {
                    var overlap = entry.Tags.Intersect(room.EnvironmentTags, StringComparer.OrdinalIgnoreCase).Any();
                    if (overlap)
                    {
                        hints.Add(entry.NarratorHint);
                        continue;
                    }
                }

                // Region/city scope lore with tag matching room name words
                if (entry.LoreScope is "region" or "city" or "location")
                {
                    var roomWords = room.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (roomWords.Any(w => entry.Name.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                                          entry.Tags.Any(t => t.Contains(w, StringComparison.OrdinalIgnoreCase))))
                    {
                        hints.Add(entry.NarratorHint);
                    }
                }
            }

            if (hints.Count == 0)
                return Task.FromResult(string.Empty);

            // Cap at 5 hints to avoid prompt bloat
            var selected = hints.Take(5);
            var context = "WORLD LORE CONTEXT (weave naturally into narration, do not recite verbatim):\n" +
                          string.Join("\n", selected.Select(h => $"- {h}"));
            return Task.FromResult(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build room lore context");
            return Task.FromResult(string.Empty);
        }
    }

    /// <summary>
    /// Build NPC-scoped knowledge: what this NPC knows based on their
    /// faction, knowledge scopes, and direct lore links.
    /// </summary>
    public Task<string> BuildScopedContextAsync(Npc npc, Room room, CancellationToken ct = default)
    {
        if (_registry is null)
            return Task.FromResult($"[{npc.Name} has limited knowledge — they know only what they can see in {room.Name}.]");

        try
        {
            var allLore = _registry.LoreEntries.GetAll();
            var hints = new List<string>();

            foreach (var entry in allLore)
            {
                if (string.IsNullOrWhiteSpace(entry.NarratorHint)) continue;

                // Direct link to this NPC
                if (entry.LinkedEntityIds.Any(id => id.Equals(npc.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    hints.Add(entry.NarratorHint);
                    continue;
                }

                // NPC's knowledge scopes match lore tags
                if (npc.KnowledgeScopes.Count > 0 && entry.Tags.Count > 0)
                {
                    var overlap = npc.KnowledgeScopes.Intersect(entry.Tags, StringComparer.OrdinalIgnoreCase).Any();
                    if (overlap)
                    {
                        hints.Add(entry.NarratorHint);
                        continue;
                    }
                }

                // Faction match
                if (!string.IsNullOrWhiteSpace(npc.Faction) && npc.Faction != "neutral")
                {
                    if (entry.Tags.Any(t => t.Equals(npc.Faction, StringComparison.OrdinalIgnoreCase)) ||
                        entry.LinkedEntityIds.Any(id => id.Equals(npc.Faction, StringComparison.OrdinalIgnoreCase)))
                    {
                        hints.Add(entry.NarratorHint);
                    }
                }
            }

            if (hints.Count == 0)
                return Task.FromResult($"[{npc.Name} has limited knowledge — they know only what they can see in {room.Name}.]");

            // Cap at 6 hints for NPC conversations
            var selected = hints.Take(6);
            var context = $"NPC KNOWLEDGE ({npc.Name} knows the following — use to inform dialogue, but don't dump exposition):\n" +
                          string.Join("\n", selected.Select(h => $"- {h}"));
            return Task.FromResult(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build NPC lore context for {NpcName}", npc.Name);
            return Task.FromResult($"[{npc.Name} has limited knowledge — they know only what they can see in {room.Name}.]");
        }
    }

    /// <summary>Search for lore entries matching a query string.</summary>
    public Task<string> SearchContextAsync(string query, CancellationToken ct = default)
    {
        if (_registry is null || string.IsNullOrWhiteSpace(query))
            return Task.FromResult(string.Empty);

        try
        {
            var allLore = _registry.LoreEntries.GetAll();
            var matches = allLore
                .Where(e => !string.IsNullOrWhiteSpace(e.NarratorHint) &&
                            (e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                             e.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                             (e.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(5)
                .Select(e => e.NarratorHint!);

            return Task.FromResult(matches.Any()
                ? "RELEVANT WORLD LORE:\n" + string.Join("\n", matches.Select(h => $"- {h}"))
                : string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search lore context");
            return Task.FromResult(string.Empty);
        }
    }
}
