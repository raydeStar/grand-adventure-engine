using System.Text;
using System.Text.Json;
using GAE.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace GAE.Engine.State;

/// <summary>
/// File-based journal for game events. Append-only; each event is
/// written as a single JSON line to the journal file.
/// </summary>
public class FileStateJournal : IStateJournal
{
    private readonly string _journalPath;
    private readonly Lock _writeLock = new();
    private readonly ILogger<FileStateJournal> _logger;
    private long _currentSequence;

    public FileStateJournal(string journalPath, ILogger<FileStateJournal> logger)
    {
        _journalPath = journalPath;
        _logger = logger;

        var dir = Path.GetDirectoryName(journalPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _currentSequence = CountExistingEntries();
    }

    public Task AppendAsync(Core.Models.GameEvent gameEvent, CancellationToken ct = default)
    {
        lock (_writeLock)
        {
            gameEvent.SequenceNumber = ++_currentSequence;
            var line = JsonSerializer.Serialize(gameEvent);
            File.AppendAllText(_journalPath, line + Environment.NewLine, Encoding.UTF8);
        }

        _logger.LogDebug("Journaled event {EventId} seq={Seq}", gameEvent.EventId, gameEvent.SequenceNumber);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Core.Models.GameEvent>> ReadFromAsync(long afterSequenceNumber, CancellationToken ct = default)
    {
        var events = new List<Core.Models.GameEvent>();

        if (!File.Exists(_journalPath))
            return Task.FromResult<IReadOnlyList<Core.Models.GameEvent>>(events);

        foreach (var line in File.ReadLines(_journalPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = JsonSerializer.Deserialize<Core.Models.GameEvent>(line);
            if (evt is not null && evt.SequenceNumber > afterSequenceNumber)
                events.Add(evt);
        }

        return Task.FromResult<IReadOnlyList<Core.Models.GameEvent>>(events);
    }

    public Task<long> GetLatestSequenceNumberAsync(CancellationToken ct = default)
        => Task.FromResult(_currentSequence);

    private long CountExistingEntries()
    {
        if (!File.Exists(_journalPath)) return 0;

        long max = 0;
        foreach (var line in File.ReadLines(_journalPath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var evt = JsonSerializer.Deserialize<Core.Models.GameEvent>(line);
            if (evt is not null && evt.SequenceNumber > max)
                max = evt.SequenceNumber;
        }
        return max;
    }
}
