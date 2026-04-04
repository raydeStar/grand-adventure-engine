namespace GAE.Core.Models;

public class GameAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PlayerId { get; set; } = string.Empty;
    public string RawInput { get; set; } = string.Empty;
    public ActionType Type { get; set; }
    public string? Target { get; set; }
    public string? Direction { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

public enum ActionType
{
    Move,
    Look,
    Attack,
    Talk,
    Use,
    Take,
    Drop,
    Buy,
    Sell,
    Equip,
    Unequip,
    Rest,
    ShortRest,
    LongRest,
    Cast,
    Inventory,
    Stats,
    Help,
    Shop,
    Unknown
}
