using System.Text;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.Data;

/// <summary>
/// PostgreSQL-backed implementation of IConversationLogger.
/// Replaces FileConversationLogger's JSONL append-only file.
/// Uses IDbContextFactory so this service can be registered as a singleton.
/// </summary>
public class EfCoreConversationLogger : IConversationLogger
{
    private readonly IDbContextFactory<GaeDbContext> _dbFactory;
    private readonly ILogger<EfCoreConversationLogger> _logger;

    public EfCoreConversationLogger(IDbContextFactory<GaeDbContext> dbFactory, ILogger<EfCoreConversationLogger> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task LogAsync(ConversationLog entry, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.ConversationLogs.Add(new ConversationLogEntity
        {
            LogId = entry.Id,
            Operation = entry.Operation,
            PlayerId = entry.PlayerId,
            RoomId = entry.RoomId,
            Model = entry.Model,
            SystemPrompt = entry.SystemPrompt,
            UserPrompt = entry.UserPrompt,
            Response = entry.Response,
            Temperature = entry.Temperature,
            MaxTokens = entry.MaxTokens,
            LatencyMs = entry.LatencyMs,
            Success = entry.Success,
            ErrorMessage = entry.ErrorMessage,
            Timestamp = entry.Timestamp
        });
        await db.SaveChangesAsync(ct);

        _logger.LogDebug("Logged conversation {Id} op={Operation} latency={LatencyMs}ms",
            entry.Id, entry.Operation, entry.LatencyMs);
    }

    public async Task<IReadOnlyList<ConversationLog>> GetLogsAsync(
        string? operation = null,
        string? playerId = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<ConversationLogEntity> query = db.ConversationLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(operation))
            query = query.Where(e => e.Operation == operation);

        if (!string.IsNullOrWhiteSpace(playerId))
            query = query.Where(e => e.PlayerId == playerId);

        var entities = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    public async Task<long> CountAsync(string? operation = null, string? playerId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        IQueryable<ConversationLogEntity> query = db.ConversationLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(operation))
            query = query.Where(e => e.Operation == operation);

        if (!string.IsNullOrWhiteSpace(playerId))
            query = query.Where(e => e.PlayerId == playerId);

        return await query.LongCountAsync(ct);
    }

    public async Task ExportJsonlAsync(Stream output, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        // Stream rows in batches to avoid loading everything into memory
        const int batchSize = 500;
        long lastId = 0;

        while (true)
        {
            var batch = await db.ConversationLogs.AsNoTracking()
                .Where(c => c.Id > lastId)
                .OrderBy(c => c.Id)
                .Take(batchSize)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            foreach (var entity in batch)
            {
                var log = ToDomain(entity);
                var line = JsonSerializer.Serialize(log, options) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                await output.WriteAsync(bytes, ct);
            }

            lastId = batch[^1].Id;
            if (batch.Count < batchSize) break;
        }
    }

    private static ConversationLog ToDomain(ConversationLogEntity e) => new()
    {
        Id = e.LogId,
        Operation = e.Operation,
        PlayerId = e.PlayerId,
        RoomId = e.RoomId,
        Model = e.Model,
        SystemPrompt = e.SystemPrompt,
        UserPrompt = e.UserPrompt,
        Response = e.Response,
        Temperature = e.Temperature,
        MaxTokens = e.MaxTokens,
        LatencyMs = e.LatencyMs,
        Success = e.Success,
        ErrorMessage = e.ErrorMessage,
        Timestamp = e.Timestamp
    };
}
