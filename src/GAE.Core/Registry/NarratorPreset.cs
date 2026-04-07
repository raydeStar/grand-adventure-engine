using GAE.Core.Models;

namespace GAE.Core.Registry;

/// <summary>
/// A narrator personality preset that can be assigned to a player character.
/// Controls the tone, voice, and personality of the AI narrator/companion.
/// </summary>
public class NarratorPreset : IRegistryEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> WorldIds { get; set; } = [WorldDefaults.DefaultWorldId];

    /// <summary>
    /// The personality archetype. Examples: "sassy", "stoic", "kind", "sardonic", "mysterious", "cheerful".
    /// Used as a quick reference for the narrator's general vibe.
    /// </summary>
    public string Archetype { get; set; } = "sardonic";

    /// <summary>
    /// The system prompt injection that defines this narrator's personality.
    /// Inserted into every NarratorService prompt to color all AI output.
    /// Example: "You are a sassy, slightly condescending narrator who finds the player's struggles amusing
    /// but secretly roots for them. Use dry wit and backhanded compliments."
    /// </summary>
    public string PersonalityPrompt { get; set; } = string.Empty;

    /// <summary>
    /// How the narrator introduces themselves or greets the player.
    /// Shown on character creation or when the narrator preset is changed.
    /// </summary>
    public string? GreetingText { get; set; }

    /// <summary>
    /// How the narrator responds when the player asks about lore.
    /// Injected into lore-query prompts to maintain personality.
    /// Example: "When explaining lore, be dramatic and over-the-top, as if telling a campfire story."
    /// </summary>
    public string? LoreDeliveryStyle { get; set; }

    /// <summary>
    /// How the narrator reacts to player deaths/failures.
    /// Example: "Mock them gently but offer encouragement."
    /// </summary>
    public string? FailureReactionStyle { get; set; }

    /// <summary>
    /// How the narrator reacts to player victories/successes.
    /// Example: "Be genuinely impressed but try to hide it behind sarcasm."
    /// </summary>
    public string? SuccessReactionStyle { get; set; }

    /// <summary>
    /// Whether this preset is available for player selection (vs. admin-only or world-default).
    /// </summary>
    public bool IsSelectable { get; set; } = true;

    /// <summary>
    /// Display order when listing available presets. Lower = shown first.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>Tags for search/filter.</summary>
    public List<string> Tags { get; set; } = [];
}
