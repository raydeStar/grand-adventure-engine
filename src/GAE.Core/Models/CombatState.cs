namespace GAE.Core.Models;

public class CombatState
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string RoomId { get; set; } = string.Empty;
    public CombatPhase Phase { get; set; } = CombatPhase.Initiative;
    public List<CombatParticipant> TurnOrder { get; set; } = [];
    public int CurrentTurnIndex { get; set; }
    public int RoundNumber { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public CombatParticipant? CurrentTurn =>
        TurnOrder.Count > 0 ? TurnOrder[CurrentTurnIndex % TurnOrder.Count] : null;
}

public class CombatParticipant
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsPlayer { get; set; }
    public int Initiative { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public bool IsConscious => Hp > 0;
}

public enum CombatPhase
{
    Initiative,
    PlayerTurn,
    EnemyTurn,
    Resolution,
    Ended
}
