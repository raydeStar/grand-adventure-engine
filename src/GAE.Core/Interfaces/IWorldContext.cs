using GAE.Core.Models;

namespace GAE.Core.Interfaces;

/// <summary>
/// Scoped request context for the currently selected world.
/// This allows API and state components to apply default world scoping
/// without forcing worldId through every method signature.
/// </summary>
public interface IWorldContext
{
    /// <summary>Current world ID for this request scope, if set.</summary>
    string? CurrentWorldId { get; }

    /// <summary>True when the world was explicitly set for this scope.</summary>
    bool IsExplicitlySet { get; }

    /// <summary>Sets the active world for the current scope.</summary>
    void SetCurrentWorld(string? worldId);

    /// <summary>Returns the current world, or default-world if none is set.</summary>
    string GetCurrentWorldOrDefault(string fallbackWorldId = WorldDefaults.DefaultWorldId);
}
