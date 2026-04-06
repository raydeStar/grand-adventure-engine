using GAE.Core.Interfaces;
using GAE.Core.Models;

namespace GAE.Engine.Worlds;

/// <summary>
/// Default scoped world context implementation used by API requests.
/// </summary>
public class WorldContext : IWorldContext
{
    private static readonly AsyncLocal<string?> CurrentWorld = new();

    /// <inheritdoc />
    public string? CurrentWorldId => CurrentWorld.Value;

    /// <inheritdoc />
    public bool IsExplicitlySet => !string.IsNullOrWhiteSpace(CurrentWorld.Value);

    /// <inheritdoc />
    public void SetCurrentWorld(string? worldId)
    {
        CurrentWorld.Value = string.IsNullOrWhiteSpace(worldId)
            ? null
            : worldId.Trim();
    }

    /// <inheritdoc />
    public string GetCurrentWorldOrDefault(string fallbackWorldId = WorldDefaults.DefaultWorldId)
        => string.IsNullOrWhiteSpace(CurrentWorld.Value) ? fallbackWorldId : CurrentWorld.Value;
}
