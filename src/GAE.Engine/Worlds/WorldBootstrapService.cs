using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Worlds;

/// <summary>
/// Establishes the default world scaffolding that keeps the existing single-world runtime valid
/// while the broader multi-world system is rolled out incrementally.
/// </summary>
public class WorldBootstrapService
{
    private readonly IWorldRepository _worldRepository;
    private readonly IStateManager _stateManager;
    private readonly ILogger<WorldBootstrapService> _logger;

    public WorldBootstrapService(
        IWorldRepository worldRepository,
        IStateManager stateManager,
        ILogger<WorldBootstrapService> logger)
    {
        _worldRepository = worldRepository;
        _stateManager = stateManager;
        _logger = logger;
    }

    /// <summary>
    /// Creates the default world if needed and backfills per-player world state for existing saves.
    /// This is intentionally conservative: it only fills missing values and does not overwrite
    /// any world data that may already have been authored.
    /// </summary>
    public async Task EnsureDefaultWorldAsync(GameRulesConfig rules, CancellationToken ct = default)
    {
        var defaultWorld = await _worldRepository.GetWorldAsync(WorldDefaults.DefaultWorldId, ct);
        if (defaultWorld is null)
        {
            defaultWorld = new World
            {
                Id = WorldDefaults.DefaultWorldId,
                Name = WorldDefaults.DefaultWorldName,
                Description = "The original Grand Adventure Engine world.",
                SpawnRoomId = WorldDefaults.DefaultSpawnRoomId,
                Rules = CloneRules(rules),
                Tags = ["default", "legacy"]
            };
            await _worldRepository.SaveWorldAsync(defaultWorld, ct);
            _logger.LogInformation("Created default world {WorldId}", defaultWorld.Id);
        }

        var players = await _stateManager.GetAllPlayersAsync(ct);
        foreach (var player in players)
        {
            var playerChanged = false;
            if (string.IsNullOrWhiteSpace(player.ActiveWorldId))
            {
                player.ActiveWorldId = WorldDefaults.DefaultWorldId;
                playerChanged = true;
            }

            if (string.IsNullOrWhiteSpace(player.HomeWorldId))
            {
                player.HomeWorldId = WorldDefaults.DefaultWorldId;
                playerChanged = true;
            }

            foreach (var quest in player.QuestLog.Where(q => string.IsNullOrWhiteSpace(q.WorldId)))
            {
                quest.WorldId = player.ActiveWorldId;
                playerChanged = true;
            }

            if (playerChanged)
                await _stateManager.SavePlayerAsync(player, ct);

            var existingWorldState = await _worldRepository.GetPlayerWorldStateAsync(player.Id, player.ActiveWorldId, ct);
            if (existingWorldState is null)
            {
                await _worldRepository.SavePlayerWorldStateAsync(new PlayerWorldState
                {
                    PlayerId = player.Id,
                    WorldId = player.ActiveWorldId,
                    CurrentRoomId = string.IsNullOrWhiteSpace(player.CurrentRoomId) ? WorldDefaults.DefaultSpawnRoomId : player.CurrentRoomId,
                    HasVisited = true,
                    FirstVisitedAt = player.CreatedAt,
                    LastVisitedAt = player.LastActiveAt
                }, ct);
            }
        }
    }

    private static GameRulesConfig CloneRules(GameRulesConfig rules)
        => JsonSerializer.Deserialize<GameRulesConfig>(JsonSerializer.Serialize(rules)) ?? new GameRulesConfig();
}