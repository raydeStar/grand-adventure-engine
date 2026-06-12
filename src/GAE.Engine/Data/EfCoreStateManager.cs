using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Data;

/// <summary>
/// PostgreSQL-backed implementation of IStateManager using EF Core.
/// Replaces the InMemoryStateManager + JournaledStateManager + file journal chain.
/// Uses IDbContextFactory so this service can be registered as a singleton.
/// Thread safety is handled by PostgreSQL transactions rather than ConcurrentDictionary.
/// </summary>
public class EfCoreStateManager : IStateManager
{
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;
    private readonly ILogger<EfCoreStateManager> _logger;
    private readonly IWorldContext _worldContext;

    public EfCoreStateManager(
        IDbContextFactory<GaeDbContext> dbFactory,
        ILogger<EfCoreStateManager> logger,
        IWorldContext worldContext)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _worldContext = worldContext;
    }

    // ── Player operations ──────────────────────────────────────────

    public async Task<PlayerCharacter?> GetPlayerAsync(string playerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, ct);
        return entity?.ToDomain();
    }

    public async Task<PlayerCharacter?> GetPlayerByDiscordIdAsync(string discordId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.DiscordId == discordId, ct);
        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<PlayerCharacter>> GetAllPlayersAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.Players.AsNoTracking().ToListAsync(ct);
        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task SavePlayerAsync(PlayerCharacter player, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Players.FindAsync([player.Id], ct);
        if (existing is not null)
        {
            existing.UpdateFrom(player);
        }
        else
        {
            db.Players.Add(PlayerEntity.FromDomain(player));
        }
        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Saved player {PlayerId} ({PlayerName})", player.Id, player.Name);
    }

    public async Task<bool> RemovePlayerAsync(string playerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Clean up per-player room instances first (orphaned rooms cause stale state on re-create)
        await db.PlayerRooms.Where(pr => pr.PlayerId == playerId).ExecuteDeleteAsync(ct);
        var rows = await db.Players.Where(p => p.Id == playerId).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    // ── Room operations (templates) ────────────────────────────────

    public async Task<Room?> GetRoomAsync(string roomId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var worldId = _worldContext.GetCurrentWorldOrDefault();
        var candidates = await db.Rooms.AsNoTracking()
            .Where(r => r.Id == roomId)
            .ToListAsync(ct);

        var entity = candidates.FirstOrDefault(r => IsTaggedForWorld(r.WorldIds, worldId));

        // Backward-compatible fallback for legacy/untagged room records.
        if (entity is null)
            entity = candidates.FirstOrDefault();

        return entity?.ToDomain();
    }

    public async Task<IReadOnlyList<Room>> GetAllRoomsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var worldId = _worldContext.GetCurrentWorldOrDefault();
        var entities = await db.Rooms.AsNoTracking()
            .Where(r => r.IsTemplate)
            .ToListAsync(ct);

        return entities
            .Where(r => IsTaggedForWorld(r.WorldIds, worldId))
            .Select(e => e.ToDomain())
            .ToList();
    }

    public async Task SaveRoomAsync(Room room, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Player room instances have IDs in the format "playerId:roomId"
        if (room.Id.Contains(':'))
        {
            await SavePlayerRoomFromSaveRoomAsync(db, room, ct);
            return;
        }

        var existing = await db.Rooms.FindAsync([room.Id], ct);
        if (existing is not null)
        {
            existing.UpdateFrom(room);
        }
        else
        {
            db.Rooms.Add(RoomEntity.FromDomain(room, isTemplate: true));
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Handles the case where SaveRoomAsync is called with a player room instance
    /// (ID format "playerId:roomId"). Routes to the player_rooms table.
    /// </summary>
    private static async Task SavePlayerRoomFromSaveRoomAsync(GaeDbContext db, Room room, CancellationToken ct)
    {
        var parts = room.Id.Split(':', 2);
        var playerId = parts[0];
        var roomId = parts[1];
        var worldId = await ResolvePlayerWorldIdAsync(db, playerId, ct);

        var existing = await db.PlayerRooms.FindAsync([room.Id], ct);
        if (existing is not null)
        {
            existing.WorldId = worldId;
            existing.UpdateFrom(room);
        }
        else
        {
            db.PlayerRooms.Add(PlayerRoomEntity.FromDomain(room, playerId, roomId, worldId));
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveRoomAsync(string roomId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Rooms.Where(r => r.Id == roomId).ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task RemoveAllRoomsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Rooms.ExecuteDeleteAsync(ct);
        await db.PlayerRooms.ExecuteDeleteAsync(ct);
    }

    private static bool IsTaggedForWorld(IReadOnlyCollection<string>? worldIds, string worldId)
    {
        if (worldIds is null || worldIds.Count == 0)
        {
            return true;
        }

        return worldIds.Contains(worldId, StringComparer.OrdinalIgnoreCase);
    }

    // ── Per-player room instances ──────────────────────────────────

    public async Task<Room?> GetPlayerRoomAsync(string playerId, string roomId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var worldId = await ResolvePlayerWorldIdAsync(db, playerId, ct);
        var key = $"{playerId}:{roomId}";
        var existing = await db.PlayerRooms.AsNoTracking()
            .FirstOrDefaultAsync(pr => pr.Id == key && pr.WorldId == worldId, ct);
        if (existing is not null)
        {
            var room = existing.ToDomain();

            // Sync shopkeeper data from template (same logic as InMemoryStateManager)
            var templateCandidates = await db.Rooms.AsNoTracking()
                .Where(r => r.Id == roomId)
                .ToListAsync(ct);
            var template = templateCandidates.FirstOrDefault(r => IsTaggedForWorld(r.WorldIds, worldId))
                ?? templateCandidates.FirstOrDefault();
            if (template is not null)
                SyncShopkeeperData(room, template.ToDomain());

            return room;
        }

        // Clone from template
        var templateEntities = await db.Rooms.AsNoTracking()
            .Where(r => r.Id == roomId)
            .ToListAsync(ct);
        var tmplEntity = templateEntities.FirstOrDefault(r => IsTaggedForWorld(r.WorldIds, worldId))
            ?? templateEntities.FirstOrDefault();
        if (tmplEntity is null) return null;

        var templateRoom = tmplEntity.ToDomain();
        var clone = templateRoom.DeepClone(key);
        clone.IsDiscovered = true;
        clone.DiscoveredAt = DateTimeOffset.UtcNow;
        clone.WorldIds = [worldId];

        db.PlayerRooms.Add(PlayerRoomEntity.FromDomain(clone, playerId, roomId, worldId));
        await db.SaveChangesAsync(ct);

        return clone;
    }

    /// <summary>
    /// Ensures shopkeeper NPCs in a player's room copy stay in sync with the template.
    /// Mirrors InMemoryStateManager.SyncShopkeeperData logic.
    /// </summary>
    private static void SyncShopkeeperData(Room playerRoom, Room template)
    {
        foreach (var templateNpc in template.Npcs.Where(n => n.IsShopkeeper))
        {
            var playerNpc = playerRoom.Npcs.FirstOrDefault(n =>
                string.Equals(n.Name, templateNpc.Name, StringComparison.OrdinalIgnoreCase));
            if (playerNpc is null) continue;

            if (!playerNpc.IsShopkeeper)
                playerNpc.IsShopkeeper = true;

            if (playerNpc.ShopInventory.Count == 0 && templateNpc.ShopInventory.Count > 0)
            {
                playerNpc.ShopInventory.Clear();
                foreach (var item in templateNpc.ShopInventory)
                {
                    var cloned = JsonSerializer.Deserialize<InventoryItem>(JsonSerializer.Serialize(item))!;
                    playerNpc.ShopInventory.Add(cloned);
                }
            }
        }
    }

    public async Task RemovePlayerRoomsAsync(string playerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.PlayerRooms.Where(pr => pr.PlayerId == playerId).ExecuteDeleteAsync(ct);
    }

    // ── Story operations ───────────────────────────────────────────

    public async Task AddStoryEntryAsync(StoryEntry entry, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.StoryEntries.Add(StoryEntryEntity.FromDomain(entry));
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<StoryEntry>> GetStoryEntriesAsync(string? playerId = null, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var worldId = _worldContext.GetCurrentWorldOrDefault();
        IQueryable<StoryEntryEntity> query = db.StoryEntries.AsNoTracking()
            .Where(e => e.WorldId == worldId)
            .OrderByDescending(e => e.Timestamp);

        if (playerId is not null)
            query = query.Where(e => e.PlayerId == playerId);

        var entities = await query.Take(limit).ToListAsync(ct);
        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<StoryEntry>> GetRecentStoryForRoomAsync(string roomId, string worldId, int limit = 10, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.StoryEntries.AsNoTracking()
            .Where(e => e.RoomId == roomId && e.WorldId == worldId)
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .ToListAsync(ct);
        return entities.Select(e => e.ToDomain()).ToList();
    }

    public async Task ClearStoryAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.StoryEntries.ExecuteDeleteAsync(ct);
    }

    // ── Combat operations ──────────────────────────────────────────

    public async Task<CombatState?> GetCombatStateAsync(string roomId, string worldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.CombatStates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.RoomId == roomId && c.WorldId == worldId, ct);
        return entity?.State;
    }

    public async Task SaveCombatStateAsync(CombatState combat, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.CombatStates.FindAsync([combat.RoomId, combat.WorldId], ct);
        if (existing is not null)
        {
            existing.State = combat;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.CombatStates.Add(new CombatStateEntity
            {
                RoomId = combat.RoomId,
                WorldId = combat.WorldId,
                State = combat,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveCombatStateAsync(string roomId, string worldId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.CombatStates.Where(c => c.RoomId == roomId && c.WorldId == worldId).ExecuteDeleteAsync(ct);
    }

    public async Task RemoveAllCombatStatesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.CombatStates.ExecuteDeleteAsync(ct);
    }

    public async Task<PartyQuestProgress?> GetPartyQuestAsync(string groupId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PartyQuests.AsNoTracking().FirstOrDefaultAsync(p => p.GroupId == groupId, ct);
        return entity?.State;
    }

    public async Task SavePartyQuestAsync(PartyQuestProgress progress, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.PartyQuests.FindAsync([progress.GroupId], ct);
        if (existing is not null)
        {
            existing.State = progress;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.PartyQuests.Add(new PartyQuestEntity
            {
                GroupId = progress.GroupId,
                State = progress,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task RemovePartyQuestAsync(string groupId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.PartyQuests.Where(p => p.GroupId == groupId).ExecuteDeleteAsync(ct);
    }

    // ── Audit log (game events) ────────────────────────────────────

    /// <summary>Appends a game event to the audit log. Called by the journaling layer.</summary>
    public async Task AppendEventAsync(GameEvent evt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.GameEvents.Add(new GameEventEntity
        {
            EventId = evt.EventId,
            ActionId = evt.ActionId,
            CorrelationId = evt.CorrelationId,
            Type = (int)evt.Type,
            PlayerId = evt.PlayerId,
            RoomId = evt.RoomId,
            Summary = evt.Summary,
            Narration = evt.Narration,
            Data = evt.Data,
            CreatedAt = evt.Timestamp
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> ResolvePlayerWorldIdAsync(GaeDbContext db, string playerId, CancellationToken ct)
        => await db.Players.AsNoTracking()
            .Where(p => p.Id == playerId)
            .Select(p => p.ActiveWorldId)
            .FirstOrDefaultAsync(ct)
            ?? WorldDefaults.DefaultWorldId;
}
