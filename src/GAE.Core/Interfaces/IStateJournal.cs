using GAE.Core.Models;

namespace GAE.Core.Interfaces;

/// <summary>
/// Append-only journal for game events. Provides durability
/// so in-memory state can be replayed after restart.
/// </summary>
public interface IStateJournal
{
    Task AppendAsync(GameEvent gameEvent, CancellationToken ct = default);
    Task<IReadOnlyList<GameEvent>> ReadFromAsync(long afterSequenceNumber, CancellationToken ct = default);
    Task<long> GetLatestSequenceNumberAsync(CancellationToken ct = default);
}
