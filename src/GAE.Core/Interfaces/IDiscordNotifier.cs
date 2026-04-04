namespace GAE.Core.Interfaces;

/// <summary>
/// Sends messages to Discord player threads and channels.
/// Implemented by the Discord bot; null/no-op when Discord is not configured.
/// </summary>
public interface IDiscordNotifier
{
    /// <summary>Posts a plain-text message to a player's adventure thread.</summary>
    Task PostToPlayerThreadAsync(string playerId, string message, CancellationToken ct = default);

    /// <summary>Posts a message to the admin (DM) channel.</summary>
    Task PostToAdminChannelAsync(string message, CancellationToken ct = default);
}
