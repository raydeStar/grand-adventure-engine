using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Narrator;

/// <summary>
/// Builds world knowledge context for narrator prompts.
/// Currently returns empty context — wiki.js has been removed.
/// Future: could pull from content registry, lore files, or other sources.
/// </summary>
public class WorldKnowledgeBuilder
{
    private readonly ILogger<WorldKnowledgeBuilder> _logger;

    public WorldKnowledgeBuilder(ILogger<WorldKnowledgeBuilder> logger)
    {
        _logger = logger;
    }

    public Task<string> BuildContextAsync(Room room, CancellationToken ct = default)
        => Task.FromResult(string.Empty);

    public Task<string> BuildScopedContextAsync(Npc npc, Room room, CancellationToken ct = default)
        => Task.FromResult($"[{npc.Name} has limited knowledge — they know only what they can see in {room.Name}.]");

    public Task<string> SearchContextAsync(string query, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
