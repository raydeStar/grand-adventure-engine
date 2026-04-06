using GAE.Core.Models;
using GAE.Engine.State;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Data;

/// <summary>
/// One-time migration service that imports existing file-based state (journal + checkpoints)
/// into PostgreSQL. Uses the existing StateReplayService to hydrate InMemoryStateManager,
/// then bulk-inserts into the database.
/// </summary>
public class DataMigrationService
{
    private readonly InMemoryStateManager _inMemory;
    private readonly StateReplayService _replay;
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;
    private readonly ILogger<DataMigrationService> _logger;

    public DataMigrationService(
        InMemoryStateManager inMemory,
        StateReplayService replay,
        IDbContextFactory<GaeDbContext> dbFactory,
        ILogger<DataMigrationService> logger)
    {
        _inMemory = inMemory;
        _replay = replay;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Replays file-based state into memory, then bulk-inserts everything into PostgreSQL.
    /// Skips the migration if the database already has data (idempotent).
    /// </summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Check if database already has data — if so, skip migration
        if (await db.Players.AnyAsync(ct) || await db.Rooms.AnyAsync(ct))
        {
            _logger.LogInformation("Database already contains data — skipping file-based migration");
            return;
        }

        // Step 1: Replay from files into in-memory state
        _logger.LogInformation("Replaying file-based state for migration...");
        try
        {
            await _replay.ReplayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "File replay encountered issues — migrating available state");
        }

        // Step 2: Snapshot the in-memory state
        var snapshot = _inMemory.TakeSnapshot();

        _logger.LogInformation(
            "Migrating state: {Players} players, {Rooms} rooms, {PlayerRooms} player rooms, {Stories} stories, {Combats} combats",
            snapshot.Players.Count, snapshot.Rooms.Count, snapshot.PlayerRooms.Count,
            snapshot.StoryEntries.Count, snapshot.CombatStates.Count);

        // Step 3: Bulk insert into PostgreSQL
        if (snapshot.Players.Count > 0)
        {
            db.Players.AddRange(snapshot.Players.Select(PlayerEntity.FromDomain));
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Migrated {Count} players", snapshot.Players.Count);
        }

        if (snapshot.Rooms.Count > 0)
        {
            db.Rooms.AddRange(snapshot.Rooms.Select(r => RoomEntity.FromDomain(r, isTemplate: true)));
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Migrated {Count} template rooms", snapshot.Rooms.Count);
        }

        if (snapshot.PlayerRooms.Count > 0)
        {
            db.PlayerRooms.AddRange(snapshot.PlayerRooms.Select(r =>
            {
                var parts = r.Id.Split(':', 2);
                return PlayerRoomEntity.FromDomain(r, parts[0], parts.Length > 1 ? parts[1] : r.Id);
            }));
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Migrated {Count} player room instances", snapshot.PlayerRooms.Count);
        }

        if (snapshot.StoryEntries.Count > 0)
        {
            // Batch inserts to avoid overwhelming SaveChanges
            const int batchSize = 500;
            for (int i = 0; i < snapshot.StoryEntries.Count; i += batchSize)
            {
                var batch = snapshot.StoryEntries.Skip(i).Take(batchSize);
                db.StoryEntries.AddRange(batch.Select(StoryEntryEntity.FromDomain));
                await db.SaveChangesAsync(ct);
            }
            _logger.LogInformation("Migrated {Count} story entries", snapshot.StoryEntries.Count);
        }

        if (snapshot.CombatStates.Count > 0)
        {
            db.CombatStates.AddRange(snapshot.CombatStates.Select(c => new CombatStateEntity
            {
                RoomId = c.RoomId,
                WorldId = c.WorldId,
                State = c,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }));
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Migrated {Count} combat states", snapshot.CombatStates.Count);
        }

        _logger.LogInformation("File-based state migration to PostgreSQL complete");
    }

    /// <summary>
    /// Imports journal.jsonl entries into the game_events audit log table.
    /// Optional — provides historical audit trail in the database.
    /// </summary>
    public async Task ImportJournalHistoryAsync(string journalPath, CancellationToken ct = default)
    {
        if (!File.Exists(journalPath))
        {
            _logger.LogInformation("No journal file found at {Path} — skipping journal import", journalPath);
            return;
        }

        _logger.LogInformation("Importing journal history from {Path}...", journalPath);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int count = 0;
        const int batchSize = 500;
        var batch = new List<GameEventEntity>();

        await foreach (var line in File.ReadLinesAsync(journalPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = System.Text.Json.JsonSerializer.Deserialize<GameEvent>(line);
                if (evt is null) continue;

                batch.Add(new GameEventEntity
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

                if (batch.Count >= batchSize)
                {
                    db.GameEvents.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                    count += batch.Count;
                    batch.Clear();
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed journal line during import");
            }
        }

        if (batch.Count > 0)
        {
            db.GameEvents.AddRange(batch);
            await db.SaveChangesAsync(ct);
            count += batch.Count;
        }

        _logger.LogInformation("Imported {Count} journal events into game_events table", count);
    }

    /// <summary>
    /// Imports conversations.jsonl into the conversation_logs table.
    /// Optional — provides queryable conversation history.
    /// </summary>
    public async Task ImportConversationHistoryAsync(string conversationsPath, CancellationToken ct = default)
    {
        if (!File.Exists(conversationsPath))
        {
            _logger.LogInformation("No conversations file found at {Path} — skipping", conversationsPath);
            return;
        }

        _logger.LogInformation("Importing conversation history from {Path}...", conversationsPath);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int count = 0;
        const int batchSize = 500;
        var batch = new List<ConversationLogEntity>();

        await foreach (var line in File.ReadLinesAsync(conversationsPath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var log = System.Text.Json.JsonSerializer.Deserialize<ConversationLog>(line,
                    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
                if (log is null) continue;

                batch.Add(new ConversationLogEntity
                {
                    LogId = log.Id,
                    Operation = log.Operation,
                    PlayerId = log.PlayerId,
                    RoomId = log.RoomId,
                    Model = log.Model,
                    SystemPrompt = log.SystemPrompt,
                    UserPrompt = log.UserPrompt,
                    Response = log.Response,
                    Temperature = log.Temperature,
                    MaxTokens = log.MaxTokens,
                    LatencyMs = log.LatencyMs,
                    Success = log.Success,
                    ErrorMessage = log.ErrorMessage,
                    Timestamp = log.Timestamp
                });

                if (batch.Count >= batchSize)
                {
                    db.ConversationLogs.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                    count += batch.Count;
                    batch.Clear();
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed conversation log line during import");
            }
        }

        if (batch.Count > 0)
        {
            db.ConversationLogs.AddRange(batch);
            await db.SaveChangesAsync(ct);
            count += batch.Count;
        }

        _logger.LogInformation("Imported {Count} conversation logs", count);
    }
}
