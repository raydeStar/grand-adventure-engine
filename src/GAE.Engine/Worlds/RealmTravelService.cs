using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Worlds;

/// <summary>
/// Phase-3 travel service: snapshot source-world stats, apply translation or restore,
/// move player to destination world spawn/current room, and persist per-world state.
/// </summary>
public class RealmTravelService : IRealmTravelService
{
    private readonly IStateManager _stateManager;
    private readonly IWorldRepository _worldRepository;
    private readonly ILogger<RealmTravelService> _logger;

    public RealmTravelService(
        IStateManager stateManager,
        IWorldRepository worldRepository,
        ILogger<RealmTravelService> logger)
    {
        _stateManager = stateManager;
        _worldRepository = worldRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ActionResult> TransferPlayerAsync(string playerId, string destinationWorldId, string initiator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(destinationWorldId))
        {
            return new ActionResult
            {
                Success = false,
                MechanicalSummary = "Player ID and destination world ID are required."
            };
        }

        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        if (player is null)
        {
            return new ActionResult
            {
                Success = false,
                MechanicalSummary = "Player not found."
            };
        }

        var sourceWorldId = string.IsNullOrWhiteSpace(player.ActiveWorldId)
            ? WorldDefaults.DefaultWorldId
            : player.ActiveWorldId;

        if (string.Equals(sourceWorldId, destinationWorldId, StringComparison.OrdinalIgnoreCase))
        {
            return new ActionResult
            {
                Success = false,
                MechanicalSummary = $"You are already in world '{destinationWorldId}'."
            };
        }

        var destinationWorld = await _worldRepository.GetWorldAsync(destinationWorldId, ct);
        if (destinationWorld is null || !destinationWorld.IsActive)
        {
            return new ActionResult
            {
                Success = false,
                MechanicalSummary = $"Destination world '{destinationWorldId}' is not available."
            };
        }

        var sourceRoomId = player.CurrentRoomId;
        var oldWorld = player.ActiveWorldId;

        await SaveCurrentWorldStateAsync(player, sourceWorldId, ct);
        var translationNotes = await ApplyDestinationStatsAsync(player, sourceWorldId, destinationWorldId, ct);

        player.ActiveWorldId = destinationWorldId;

        var destinationState = await _worldRepository.GetPlayerWorldStateAsync(player.Id, destinationWorldId, ct);
        var destinationRoomId = string.IsNullOrWhiteSpace(destinationState?.CurrentRoomId)
            ? destinationWorld.SpawnRoomId
            : destinationState.CurrentRoomId;

        if (string.IsNullOrWhiteSpace(destinationRoomId))
            destinationRoomId = WorldDefaults.DefaultSpawnRoomId;

        player.CurrentRoomId = destinationRoomId;
        player.LastActiveAt = DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(player.HomeWorldId))
            player.HomeWorldId = WorldDefaults.DefaultWorldId;

        await _stateManager.SavePlayerAsync(player, ct);

        await _worldRepository.SavePlayerWorldStateAsync(new PlayerWorldState
        {
            PlayerId = player.Id,
            WorldId = destinationWorldId,
            CurrentRoomId = destinationRoomId,
            HasVisited = true,
            FirstVisitedAt = destinationState?.FirstVisitedAt ?? DateTimeOffset.UtcNow,
            LastVisitedAt = DateTimeOffset.UtcNow
        }, ct);

        _logger.LogInformation(
            "Transferred player {PlayerId} from {SourceWorld} to {DestinationWorld} via {Initiator}",
            player.Id,
            sourceWorldId,
            destinationWorldId,
            initiator);

        return new ActionResult
        {
            Success = true,
            MechanicalSummary = $"Realm transfer complete: {sourceWorldId} -> {destinationWorldId}. {translationNotes}",
            StateChanges =
            [
                new StateChange
                {
                    EntityType = "Player",
                    EntityId = player.Id,
                    Property = "ActiveWorldId",
                    OldValue = oldWorld,
                    NewValue = destinationWorldId
                },
                new StateChange
                {
                    EntityType = "Player",
                    EntityId = player.Id,
                    Property = "CurrentRoomId",
                    OldValue = sourceRoomId,
                    NewValue = destinationRoomId
                }
            ]
        };
    }

    private async Task SaveCurrentWorldStateAsync(PlayerCharacter player, string sourceWorldId, CancellationToken ct)
    {
        await _worldRepository.SavePlayerWorldStateAsync(new PlayerWorldState
        {
            PlayerId = player.Id,
            WorldId = sourceWorldId,
            CurrentRoomId = player.CurrentRoomId,
            HasVisited = true,
            FirstVisitedAt = player.CreatedAt,
            LastVisitedAt = DateTimeOffset.UtcNow
        }, ct);

        var existingSnapshot = await _worldRepository.GetStatSnapshotAsync(player.Id, sourceWorldId, ct);
        var snapshot = existingSnapshot ?? new WorldStatSnapshot
        {
            Id = $"{player.Id}:{sourceWorldId}",
            PlayerId = player.Id,
            WorldId = sourceWorldId
        };

        snapshot.Class = player.Class;
        snapshot.Race = player.Race;
        snapshot.Level = player.Level;
        snapshot.Hp = player.Hp;
        snapshot.MaxHp = player.MaxHp;
        snapshot.Mp = player.Mp;
        snapshot.MaxMp = player.MaxMp;
        snapshot.CreatedAt = DateTimeOffset.UtcNow;
        snapshot.Stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["str"] = player.Str,
            ["dex"] = player.Dex,
            ["con"] = player.Con,
            ["int"] = player.Int,
            ["wis"] = player.Wis,
            ["cha"] = player.Cha,
            ["luck"] = player.Luck
        };

        await _worldRepository.SaveStatSnapshotAsync(snapshot, ct);
    }

    private async Task<string> ApplyDestinationStatsAsync(PlayerCharacter player, string sourceWorldId, string destinationWorldId, CancellationToken ct)
    {
        // Return-trip path: restore original snapshot when re-entering home world.
        if (string.Equals(destinationWorldId, player.HomeWorldId, StringComparison.OrdinalIgnoreCase))
        {
            var homeSnapshot = await _worldRepository.GetStatSnapshotAsync(player.Id, destinationWorldId, ct);
            if (homeSnapshot is not null)
            {
                ApplyStats(player, homeSnapshot.Stats);
                return "Restored native home-world stats from snapshot.";
            }
        }

        // Cached translation path.
        var history = await _worldRepository.GetTranslationHistoryAsync(player.Id, sourceWorldId, destinationWorldId, ct);
        if (history is not null)
        {
            ApplyStats(player, history.TranslatedStats);
            return "Applied cached stat translation.";
        }

        // Phase-3 foundation translator: deterministic baseline mapping.
        var translatedStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["str"] = ClampStat(player.Str),
            ["dex"] = ClampStat(player.Dex),
            ["con"] = ClampStat(player.Con),
            ["int"] = ClampStat(player.Int),
            ["wis"] = ClampStat(player.Wis),
            ["cha"] = ClampStat(player.Cha),
            ["luck"] = ClampStat(player.Luck)
        };

        ApplyStats(player, translatedStats);

        await _worldRepository.SaveTranslationHistoryAsync(new StatTranslationHistory
        {
            Id = $"{player.Id}:{sourceWorldId}:{destinationWorldId}",
            PlayerId = player.Id,
            SourceWorldId = sourceWorldId,
            DestinationWorldId = destinationWorldId,
            TranslatedStats = translatedStats,
            TranslationNotes = "Phase-3 baseline deterministic translation (semantic AI translation pending)",
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

        return "Generated and cached a baseline stat translation.";
    }

    private static int ClampStat(int value)
        => Math.Clamp(value, 3, 30);

    private static void ApplyStats(PlayerCharacter player, IReadOnlyDictionary<string, int> stats)
    {
        if (stats.TryGetValue("str", out var str)) player.Str = str;
        if (stats.TryGetValue("dex", out var dex)) player.Dex = dex;
        if (stats.TryGetValue("con", out var con)) player.Con = con;
        if (stats.TryGetValue("int", out var intel)) player.Int = intel;
        if (stats.TryGetValue("wis", out var wis)) player.Wis = wis;
        if (stats.TryGetValue("cha", out var cha)) player.Cha = cha;
        if (stats.TryGetValue("luck", out var luck)) player.Luck = luck;
    }
}
