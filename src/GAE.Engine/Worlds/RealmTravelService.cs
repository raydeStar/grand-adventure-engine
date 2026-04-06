using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Worlds;

/// <summary>
/// Phase-3 travel service: snapshot source-world stats, apply AI-mediated or deterministic
/// stat translation, move player to destination world, persist per-world state, and produce
/// transition narration.
/// </summary>
public class RealmTravelService : IRealmTravelService
{
    private readonly IStateManager _stateManager;
    private readonly IWorldRepository _worldRepository;
    private readonly INarratorService? _narrator;
    private readonly ILogger<RealmTravelService> _logger;
    private readonly GameRulesConfig _rules;

    public RealmTravelService(
        IStateManager stateManager,
        IWorldRepository worldRepository,
        ILogger<RealmTravelService> logger,
        GameRulesConfig? rules = null,
        INarratorService? narrator = null)
    {
        _stateManager = stateManager;
        _worldRepository = worldRepository;
        _logger = logger;
        _rules = rules ?? new GameRulesConfig();
        _narrator = narrator;
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

        var sourceWorld = await _worldRepository.GetWorldAsync(sourceWorldId, ct);
        var sourceRoomId = player.CurrentRoomId;
        var oldWorld = player.ActiveWorldId;

        await SaveCurrentWorldStateAsync(player, sourceWorldId, ct);
        var (translationNotes, transitionNarrative) = await ApplyDestinationStatsAsync(
            player, sourceWorldId, destinationWorldId, sourceWorld, destinationWorld, ct);

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

        // If we have no AI transition narrative yet, try to generate one
        if (string.IsNullOrWhiteSpace(transitionNarrative) && _narrator is not null)
        {
            // Find portal hint for the narrator
            string? portalHint = null;
            if (sourceWorld is not null)
            {
                portalHint = sourceWorld.Portals
                    .FirstOrDefault(p =>
                        string.Equals(p.SourceRoomId, sourceRoomId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.DestinationWorldId, destinationWorldId, StringComparison.OrdinalIgnoreCase))
                    ?.NarratorHint;
            }

            transitionNarrative = await _narrator.NarrateRealmTransitionAsync(
                player.Name, sourceWorld?.Name ?? sourceWorldId,
                destinationWorld.Name, portalHint, ct);
        }

        return new ActionResult
        {
            Success = true,
            Narration = transitionNarrative,
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

    private async Task<(string TranslationNotes, string? TransitionNarrative)> ApplyDestinationStatsAsync(
        PlayerCharacter player,
        string sourceWorldId,
        string destinationWorldId,
        World? sourceWorld,
        World destinationWorld,
        CancellationToken ct)
    {
        var preservedLevel = player.Level;
        var preservedXp = player.Xp;

        // Return-trip path: restore original snapshot when re-entering home world.
        if (string.Equals(destinationWorldId, player.HomeWorldId, StringComparison.OrdinalIgnoreCase))
        {
            return await ApplyReturnHomeAsync(player, sourceWorldId, destinationWorldId, sourceWorld, destinationWorld, preservedLevel, preservedXp, ct);
        }

        // Check for a cached translation and whether it's still valid.
        var history = await _worldRepository.GetTranslationHistoryAsync(player.Id, sourceWorldId, destinationWorldId, ct);
        if (history is not null && IsCacheValid(player, history))
        {
            ApplyStats(player, history.TranslatedStats);
            RecalculateMaxHpMp(player, destinationWorld.Rules);
            return ("Applied cached stat translation.", history.TransitionNarrative);
        }

        // AI-mediated translation attempt.
        var aiResult = await TryAiTranslationAsync(player, sourceWorldId, destinationWorldId, sourceWorld, destinationWorld, history, ct);
        if (aiResult is not null)
        {
            ApplyStats(player, aiResult.TranslatedStats);
            RecalculateMaxHpMp(player, destinationWorld.Rules);

            await SaveTranslationHistoryAsync(player, sourceWorldId, destinationWorldId, aiResult, ct);
            return (aiResult.TranslationNotes, aiResult.Narrative);
        }

        // Deterministic fallback: semantic-tag-aware mapping.
        var translatedStats = BuildDeterministicTranslation(player, sourceWorld, destinationWorld);
        ApplyStats(player, translatedStats);
        RecalculateMaxHpMp(player, destinationWorld.Rules);

        await _worldRepository.SaveTranslationHistoryAsync(new StatTranslationHistory
        {
            Id = $"{player.Id}:{sourceWorldId}:{destinationWorldId}",
            PlayerId = player.Id,
            SourceWorldId = sourceWorldId,
            DestinationWorldId = destinationWorldId,
            TranslatedStats = translatedStats,
            TranslationNotes = "Deterministic stat translation (AI unavailable).",
            SourceLevel = player.Level,
            SourceStats = GetPlayerStatDict(player),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

        return ("Deterministic stat translation applied.", null);
    }

    /// <summary>
    /// Handles the return-home path with smart re-translation when stats have changed.
    /// </summary>
    private async Task<(string, string?)> ApplyReturnHomeAsync(
        PlayerCharacter player,
        string sourceWorldId,
        string destinationWorldId,
        World? sourceWorld,
        World destinationWorld,
        int preservedLevel,
        int preservedXp,
        CancellationToken ct)
    {
        var homeSnapshot = await _worldRepository.GetStatSnapshotAsync(player.Id, destinationWorldId, ct);
        if (homeSnapshot is not null)
        {
            // Check if the player gained levels or stats while abroad — if so, re-translate
            bool statsChanged = preservedLevel > homeSnapshot.Level;

            if (statsChanged && _narrator is not null)
            {
                // Re-translate: the player is more powerful than when they left home
                var aiResult = await TryAiTranslationAsync(
                    player, sourceWorldId, destinationWorldId, sourceWorld, destinationWorld, null, ct);
                if (aiResult is not null)
                {
                    ApplyStats(player, aiResult.TranslatedStats);
                    player.Level = Math.Max(preservedLevel, homeSnapshot.Level);
                    player.Xp = Math.Max(preservedXp, player.Xp);
                    RecalculateMaxHpMp(player, destinationWorld.Rules);
                    await SaveTranslationHistoryAsync(player, sourceWorldId, destinationWorldId, aiResult, ct);
                    return ("Re-translated stats for your homecoming — you've grown stronger abroad.", aiResult.Narrative);
                }
            }

            // Fast path: restore snapshot directly
            ApplyStats(player, homeSnapshot.Stats);
            player.Level = Math.Max(preservedLevel, homeSnapshot.Level);
            player.Xp = Math.Max(preservedXp, player.Xp);
            player.Hp = homeSnapshot.Hp;
            player.Mp = homeSnapshot.Mp;
            RecalculateMaxHpMp(player, destinationWorld.Rules);

            if (preservedLevel > homeSnapshot.Level)
                return ("Restored native home-world stats and preserved your higher level progression.", null);
            return ("Restored native home-world stats from snapshot.", null);
        }

        return ("Returning home — no snapshot found, stats unchanged.", null);
    }

    /// <summary>
    /// Attempts AI-mediated stat translation via the narrator service.
    /// Returns null if the narrator is unavailable or the response is invalid.
    /// </summary>
    private async Task<StatTranslationResponse?> TryAiTranslationAsync(
        PlayerCharacter player,
        string sourceWorldId,
        string destinationWorldId,
        World? sourceWorld,
        World destinationWorld,
        StatTranslationHistory? previousHistory,
        CancellationToken ct)
    {
        if (_narrator is null) return null;

        var sourceRules = sourceWorld?.Rules ?? _rules;
        var destRules = destinationWorld.Rules;

        // Build source stats with semantic tags from the source world's rules
        var sourceStats = new Dictionary<string, StatTranslationStat>();
        var playerStats = GetPlayerStatDict(player);
        foreach (var (statId, value) in playerStats)
        {
            var statDef = sourceRules.Stats.GetValueOrDefault(statId);
            sourceStats[statId] = new StatTranslationStat
            {
                Display = statDef?.Display ?? statId.ToUpperInvariant(),
                Value = value,
                Min = statDef?.Min ?? 1,
                Max = statDef?.Max ?? 20,
                SemanticTags = statDef?.SemanticTags ?? []
            };
        }

        // Build destination stat definitions with semantic tags
        var destStatDefs = new Dictionary<string, StatTranslationStat>();
        foreach (var (statId, statDef) in destRules.Stats)
        {
            if (string.Equals(statDef.Category, "resource", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(statDef.Category, "currency", StringComparison.OrdinalIgnoreCase))
                continue; // Skip HP/MP/gold — those are recalculated, not translated

            destStatDefs[statId] = new StatTranslationStat
            {
                Display = statDef.Display,
                Min = statDef.Min,
                Max = statDef.Max,
                SemanticTags = statDef.SemanticTags
            };
        }

        // If the destination world has no attribute stats defined, fall back to source stat IDs
        if (destStatDefs.Count == 0)
        {
            foreach (var (statId, _) in sourceStats)
            {
                var statDef = destRules.Stats.GetValueOrDefault(statId);
                destStatDefs[statId] = new StatTranslationStat
                {
                    Display = statDef?.Display ?? statId.ToUpperInvariant(),
                    Min = statDef?.Min ?? 1,
                    Max = statDef?.Max ?? 20,
                    SemanticTags = statDef?.SemanticTags ?? []
                };
            }
        }

        string? previousTranslation = null;
        if (previousHistory is not null)
        {
            var prevLines = previousHistory.TranslatedStats
                .Select(kvp => $"  {kvp.Key}: {kvp.Value}")
                .ToList();
            previousTranslation = $"Previous translation notes: {previousHistory.TranslationNotes}\n" +
                                  $"Previous values:\n{string.Join("\n", prevLines)}";
        }

        var request = new StatTranslationRequest
        {
            CharacterName = player.Name,
            Class = player.Class,
            Race = player.Race,
            Level = player.Level,
            SourceWorldName = sourceWorld?.Name ?? sourceWorldId,
            DestinationWorldName = destinationWorld.Name,
            SourceStats = sourceStats,
            DestinationStatDefs = destStatDefs,
            PreviousTranslation = previousTranslation
        };

        var response = await _narrator.TranslateStatsAsync(request, ct);
        if (response is null) return null;

        // Validate: clamp all translated stats to destination ranges
        foreach (var (statId, value) in response.TranslatedStats)
        {
            var def = destStatDefs.GetValueOrDefault(statId);
            if (def is not null)
                response.TranslatedStats[statId] = Math.Clamp(value, def.Min, def.Max);
        }

        return response;
    }

    /// <summary>
    /// Checks whether a cached translation is still valid for the current player state.
    /// Invalidates if level changed or any source stats differ.
    /// </summary>
    private static bool IsCacheValid(PlayerCharacter player, StatTranslationHistory history)
    {
        if (history.SourceLevel != 0 && history.SourceLevel != player.Level)
            return false;

        if (history.SourceStats.Count > 0)
        {
            var currentStats = GetPlayerStatDict(player);
            foreach (var (statId, cachedValue) in history.SourceStats)
            {
                if (currentStats.TryGetValue(statId, out var currentValue) && currentValue != cachedValue)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds a deterministic stat translation using semantic tag overlap when available.
    /// Falls back to identity mapping with clamping.
    /// </summary>
    private Dictionary<string, int> BuildDeterministicTranslation(
        PlayerCharacter player, World? sourceWorld, World destinationWorld)
    {
        var sourceRules = sourceWorld?.Rules ?? _rules;
        var destRules = destinationWorld.Rules;
        var playerStats = GetPlayerStatDict(player);
        var translated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Collect destination attribute stats
        var destAttributes = destRules.Stats
            .Where(kvp => !string.Equals(kvp.Value.Category, "resource", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(kvp.Value.Category, "currency", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        // If the destination world has no custom stat definitions, use identity mapping
        if (destAttributes.Count == 0)
        {
            foreach (var (statId, value) in playerStats)
                translated[statId] = ClampStat(value);
            return translated;
        }

        // Try semantic tag matching
        foreach (var (destStatId, destDef) in destAttributes)
        {
            if (destDef.SemanticTags.Count == 0)
            {
                // No tags — try direct name match
                if (playerStats.TryGetValue(destStatId, out var directValue))
                {
                    translated[destStatId] = Math.Clamp(directValue, destDef.Min, destDef.Max);
                    continue;
                }
                translated[destStatId] = destDef.Base;
                continue;
            }

            // Find source stat with best tag overlap
            int bestOverlap = 0;
            int bestValue = destDef.Base;
            foreach (var (srcStatId, srcValue) in playerStats)
            {
                var srcDef = sourceRules.Stats.GetValueOrDefault(srcStatId);
                var srcTags = srcDef?.SemanticTags ?? [];
                int overlap = srcTags.Count(t => destDef.SemanticTags.Contains(t, StringComparer.OrdinalIgnoreCase));
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    // Scale proportionally if ranges differ
                    var srcMax = srcDef?.Max ?? 20;
                    var srcMin = srcDef?.Min ?? 1;
                    double ratio = srcMax != srcMin ? (double)(srcValue - srcMin) / (srcMax - srcMin) : 0.5;
                    bestValue = (int)Math.Round(destDef.Min + ratio * (destDef.Max - destDef.Min));
                }
            }
            translated[destStatId] = Math.Clamp(bestValue, destDef.Min, destDef.Max);
        }

        return translated;
    }

    private async Task SaveTranslationHistoryAsync(
        PlayerCharacter player, string sourceWorldId, string destinationWorldId,
        StatTranslationResponse aiResult, CancellationToken ct)
    {
        await _worldRepository.SaveTranslationHistoryAsync(new StatTranslationHistory
        {
            Id = $"{player.Id}:{sourceWorldId}:{destinationWorldId}",
            PlayerId = player.Id,
            SourceWorldId = sourceWorldId,
            DestinationWorldId = destinationWorldId,
            TranslatedStats = aiResult.TranslatedStats,
            TranslationNotes = aiResult.TranslationNotes,
            TransitionNarrative = aiResult.Narrative,
            SourceLevel = player.Level,
            SourceStats = GetPlayerStatDict(player),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    private static int ClampStat(int value)
        => Math.Clamp(value, 3, 30);

    private void RecalculateMaxHpMp(PlayerCharacter player, GameRulesConfig? worldRules = null)
    {
        var rules = worldRules ?? _rules;
        int statMax = rules.Stats.GetValueOrDefault("str")?.Max ?? 20;
        player.Str = Math.Clamp(player.Str, 1, statMax);
        player.Dex = Math.Clamp(player.Dex, 1, statMax);
        player.Con = Math.Clamp(player.Con, 1, statMax);
        player.Int = Math.Clamp(player.Int, 1, statMax);
        player.Wis = Math.Clamp(player.Wis, 1, statMax);
        player.Cha = Math.Clamp(player.Cha, 1, statMax);
        player.Luck = Math.Clamp(player.Luck, 1, statMax);

        int hpBase = rules.Stats.GetValueOrDefault("hp")?.Base ?? 20;
        int mpBase = rules.Stats.GetValueOrDefault("mp")?.Base ?? 10;
        int conMod = PlayerCharacter.GetStatModifier(player.Con);
        int intMod = PlayerCharacter.GetStatModifier(player.Int);
        double hpScale = rules.Leveling.HpScalePerLevel;
        double mpScale = rules.Leveling.MpScalePerLevel;
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

    private static Dictionary<string, int> GetPlayerStatDict(PlayerCharacter player)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["str"] = player.Str,
            ["dex"] = player.Dex,
            ["con"] = player.Con,
            ["int"] = player.Int,
            ["wis"] = player.Wis,
            ["cha"] = player.Cha,
            ["luck"] = player.Luck
        };
}
