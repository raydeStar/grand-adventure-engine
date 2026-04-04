using System.Text;
using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.State;

/// <summary>
/// Append-only JSONL logger for LLM conversation exchanges.
/// Designed for training-data collection — every prompt and response
/// is stored in a single file that can be exported as-is.
/// </summary>
public class FileConversationLogger : IConversationLogger
{
    private readonly string _logPath;
    private readonly Lock _writeLock = new();
    private readonly ILogger<FileConversationLogger> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public FileConversationLogger(string logPath, ILogger<FileConversationLogger> logger)
    {
        _logPath = logPath;
        _logger = logger;

        var dir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public Task LogAsync(ConversationLog entry, CancellationToken ct = default)
    {
        lock (_writeLock)
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }

        _logger.LogDebug("Logged conversation {Id} op={Operation} latency={LatencyMs}ms",
            entry.Id, entry.Operation, entry.LatencyMs);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationLog>> GetLogsAsync(
        string? operation = null,
        string? playerId = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        var allEntries = ReadAll();

        IEnumerable<ConversationLog> filtered = allEntries;

        if (!string.IsNullOrWhiteSpace(operation))
            filtered = filtered.Where(e => e.Operation.Equals(operation, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(playerId))
            filtered = filtered.Where(e => e.PlayerId == playerId);

        var result = filtered
            .OrderByDescending(e => e.Timestamp)
            .Skip(offset)
            .Take(limit)
            .ToList();

        return Task.FromResult<IReadOnlyList<ConversationLog>>(result);
    }

    public Task<long> CountAsync(string? operation = null, string? playerId = null, CancellationToken ct = default)
    {
        var allEntries = ReadAll();
        IEnumerable<ConversationLog> filtered = allEntries;

        if (!string.IsNullOrWhiteSpace(operation))
            filtered = filtered.Where(e => e.Operation.Equals(operation, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(playerId))
            filtered = filtered.Where(e => e.PlayerId == playerId);

        return Task.FromResult((long)filtered.Count());
    }

    public async Task ExportJsonlAsync(Stream output, CancellationToken ct = default)
    {
        if (!File.Exists(_logPath))
            return;

        await using var fileStream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await fileStream.CopyToAsync(output, ct);
    }

    private List<ConversationLog> ReadAll()
    {
        var entries = new List<ConversationLog>();
        if (!File.Exists(_logPath))
            return entries;

        foreach (var line in File.ReadLines(_logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<ConversationLog>(line, JsonOptions);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed conversation log line");
            }
        }

        return entries;
    }
}
