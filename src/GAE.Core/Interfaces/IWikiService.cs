using GAE.Core.Models;

namespace GAE.Core.Interfaces;

public interface IWikiService
{
    Task<bool> CreateOrUpdatePageAsync(string path, string title, string content, CancellationToken ct = default);
    Task<string?> GetPageContentAsync(string path, CancellationToken ct = default);
    Task<bool> PageExistsAsync(string path, CancellationToken ct = default);
    Task SyncPlayerPageAsync(PlayerCharacter player, CancellationToken ct = default);
    Task SyncRoomPageAsync(Room room, CancellationToken ct = default);
    Task SyncStoryEntryAsync(StoryEntry entry, CancellationToken ct = default);
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}
