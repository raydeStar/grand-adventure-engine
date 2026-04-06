namespace GAE.Engine.Data;

/// <summary>
/// Generic content registry entity. Stores any IRegistryEntry as a JSONB blob.
/// Uses a composite key (ContentType + Id) so different content types can share a table
/// while IDs remain unique within each type.
/// </summary>
public class ContentRegistryEntity
{
    /// <summary>The content type discriminator: spell, class, race, item, monster, quest.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The entry's unique ID within its content type.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name for quick lookups without deserializing JSONB.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full serialized domain object as JSON.</summary>
    public string Data { get; set; } = "{}";
}

/// <summary>
/// Stores singleton configuration objects (e.g. GameRulesConfig) as JSONB.
/// Only one row per config key.
/// </summary>
public class GameConfigEntity
{
    /// <summary>Config key, e.g. "game_rules".</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Full serialized config object as JSON.</summary>
    public string Data { get; set; } = "{}";

    /// <summary>Last time this config was updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
