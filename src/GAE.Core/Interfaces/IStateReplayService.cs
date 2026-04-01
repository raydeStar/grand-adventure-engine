namespace GAE.Core.Interfaces;

/// <summary>
/// Orchestrates state recovery at startup: loads the latest
/// checkpoint, replays journal events since that checkpoint,
/// and rebuilds in-memory projections.
/// </summary>
public interface IStateReplayService
{
    Task ReplayAsync(CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
}
