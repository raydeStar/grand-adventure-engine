namespace GAE.Core.Models;

public class NarratorContext
{
    public PlayerCharacter Player { get; set; } = null!;
    public Room CurrentRoom { get; set; } = null!;
    public GameAction Action { get; set; } = null!;
    public ActionResult MechanicalResult { get; set; } = null!;
    public List<StoryEntry> RecentStory { get; set; } = [];
    public List<PlayerCharacter> NearbyPlayers { get; set; } = [];
    public string? CombatSummary { get; set; }
    public InteractionState? InteractionState { get; set; }
}
