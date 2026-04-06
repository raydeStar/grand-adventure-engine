using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
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
    private readonly GameRulesConfig _rules;

    public RealmTravelService(
        IStateManager stateManager,
        IWorldRepository worldRepository,
        ILogger<RealmTravelService> logger,
        GameRulesConfig? rules = null)
    {
        _stateManager = stateManager;
        _worldRepository = worldRepository;
        _logger = logger;
        _rules = rules ?? new GameRulesConfig();
    }

    /// <inheritdoc />
    public async Task<ActionResult> TransferPlayerAsync(
        string playerId,
        string destinationWorldId,
        string initiator,
        string? destinationRoomOverride = null,
        CancellationToken ct = default)
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
        var destinationRoomId = !string.IsNullOrWhiteSpace(destinationRoomOverride)
            ? destinationRoomOverride
            : string.IsNullOrWhiteSpace(destinationState?.CurrentRoomId)
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
        var preservedLevel = player.Level;
        var preservedXp = player.Xp;

        // Return-trip path: restore original snapshot when re-entering home world.
        if (string.Equals(destinationWorldId, player.HomeWorldId, StringComparison.OrdinalIgnoreCase))
        {
            var homeSnapshot = await _worldRepository.GetStatSnapshotAsync(player.Id, destinationWorldId, ct);
            if (homeSnapshot is not null)
            {
                ApplyStats(player, homeSnapshot.Stats);
                player.Level = Math.Max(preservedLevel, homeSnapshot.Level);
                player.Xp = Math.Max(preservedXp, player.Xp);
                player.Hp = homeSnapshot.Hp;
                player.Mp = homeSnapshot.Mp;
                RecalculateMaxHpMp(player);
                if (preservedLevel > homeSnapshot.Level)
                    return "Restored native home-world stats and preserved your higher level progression.";
                return "Restored native home-world stats from snapshot.";
            }
        }

        // Cached translation path.
        var history = await _worldRepository.GetTranslationHistoryAsync(player.Id, sourceWorldId, destinationWorldId, ct);
        if (history is not null)
        {
            ApplyStats(player, history.TranslatedStats);
            RecalculateMaxHpMp(player);
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
        RecalculateMaxHpMp(player);

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

    private void RecalculateMaxHpMp(PlayerCharacter player)
    {
        int statMax = _rules.Stats.GetValueOrDefault("str")?.Max ?? 20;
        player.Str = Math.Clamp(player.Str, 1, statMax);
        player.Dex = Math.Clamp(player.Dex, 1, statMax);
        player.Con = Math.Clamp(player.Con, 1, statMax);
        player.Int = Math.Clamp(player.Int, 1, statMax);
        player.Wis = Math.Clamp(player.Wis, 1, statMax);
        player.Cha = Math.Clamp(player.Cha, 1, statMax);
        player.Luck = Math.Clamp(player.Luck, 1, statMax);

        int hpBase = _rules.Stats.GetValueOrDefault("hp")?.Base ?? 20;
        int mpBase = _rules.Stats.GetValueOrDefault("mp")?.Base ?? 10;
        int conMod = PlayerCharacter.GetStatModifier(player.Con);
        int intMod = PlayerCharacter.GetStatModifier(player.Int);
        double hpScale = _rules.Leveling.HpScalePerLevel;
        double mpScale = _rules.Leveling.MpScalePerLevel;
        int bonusLevels = Math.Max(0, player.Level - 1);

        int baseHp = hpBase + conMod;
        int baseMp = mpBase + intMod;
        player.MaxHp = Math.Max(1, (int)(baseHp * (1.0 + hpScale * bonusLevels)));
        player.MaxMp = Math.Max(0, (int)(baseMp * (1.0 + mpScale * bonusLevels)));
        player.Hp = Math.Clamp(player.Hp, 0, player.MaxHp);
        player.Mp = Math.Clamp(player.Mp, 0, player.MaxMp);
    }

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
