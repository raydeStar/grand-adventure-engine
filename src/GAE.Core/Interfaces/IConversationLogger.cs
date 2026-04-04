using GAE.Core.Models;

namespace GAE.Core.Interfaces;

/// <summary>
/// Persists LLM prompt/response exchanges for training-data export and audit.
/// </summary>
public interface IConversationLogger
{
    /// <summary>Append a completed exchange to the log.</summary>
    Task LogAsync(ConversationLog entry, CancellationToken ct = default);

    /// <summary>Read log entries with optional filters, newest first.</summary>
    Task<IReadOnlyList<ConversationLog>> GetLogsAsync(
        string? operation = null,
        string? playerId = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>Total count of log entries (optionally filtered).</summary>
    Task<long> CountAsync(string? operation = null, string? playerId = null, CancellationToken ct = default);

    /// <summary>Stream all entries as JSONL for bulk export.</summary>
    Task ExportJsonlAsync(Stream output, CancellationToken ct = default);
}
