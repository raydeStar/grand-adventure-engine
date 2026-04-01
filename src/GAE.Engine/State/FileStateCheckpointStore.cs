using System.Text.Json;
using GAE.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.State;

/// <summary>
/// File-based checkpoint store. Serializes the full state snapshot
/// to a JSON file for fast recovery.
/// </summary>
public class FileStateCheckpointStore : IStateCheckpointStore
{
    private readonly string _checkpointDir;
    private readonly ILogger<FileStateCheckpointStore> _logger;

    public FileStateCheckpointStore(string checkpointDir, ILogger<FileStateCheckpointStore> logger)
    {
        _checkpointDir = checkpointDir;
        _logger = logger;
        Directory.CreateDirectory(checkpointDir);
    }

    public async Task SaveCheckpointAsync(StateCheckpoint checkpoint, CancellationToken ct = default)
    {
        var path = Path.Combine(_checkpointDir, $"checkpoint-{checkpoint.SequenceNumber}.json");
        await File.WriteAllBytesAsync(path, checkpoint.Payload, ct);
        _logger.LogInformation("Saved checkpoint at seq={Seq} to {Path}", checkpoint.SequenceNumber, path);
    }

    public Task<StateCheckpoint?> LoadLatestCheckpointAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_checkpointDir))
            return Task.FromResult<StateCheckpoint?>(null);

        var files = Directory.GetFiles(_checkpointDir, "checkpoint-*.json")
            .OrderByDescending(f => f)
            .ToList();

        if (files.Count == 0)
            return Task.FromResult<StateCheckpoint?>(null);

        var latestFile = files[0];
        var payload = File.ReadAllBytes(latestFile);

        // Extract sequence number from filename
        var fileName = Path.GetFileNameWithoutExtension(latestFile);
        var seqStr = fileName.Replace("checkpoint-", "");
        if (!long.TryParse(seqStr, out var seq))
        {
            _logger.LogWarning("Could not parse sequence from checkpoint file {File}", latestFile);
            return Task.FromResult<StateCheckpoint?>(null);
        }

        var checkpoint = new StateCheckpoint
        {
            SequenceNumber = seq,
            Payload = payload,
            CreatedAt = File.GetCreationTimeUtc(latestFile)
        };

        _logger.LogInformation("Loaded checkpoint at seq={Seq} from {Path}", seq, latestFile);
        return Task.FromResult<StateCheckpoint?>(checkpoint);
    }
}
