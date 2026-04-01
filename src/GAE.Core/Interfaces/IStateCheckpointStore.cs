namespace GAE.Core.Interfaces;

/// <summary>
/// Periodic full-state snapshots. Checkpoints truncate
/// the journal replay window on startup.
/// </summary>
public interface IStateCheckpointStore
{
    Task SaveCheckpointAsync(StateCheckpoint checkpoint, CancellationToken ct = default);
    Task<StateCheckpoint?> LoadLatestCheckpointAsync(CancellationToken ct = default);
}

public class StateCheckpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public long SequenceNumber { get; set; }
    public byte[] Payload { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
