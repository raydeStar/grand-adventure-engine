using System.Text.Json.Serialization;

namespace GAE.Core.Models;

public class DiceRoll
{
    public string Expression { get; set; } = string.Empty;
    public int[] IndividualRolls { get; set; } = [];
    public int Modifier { get; set; }
    public int Total { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public bool IsCritical { get; set; }
    public bool IsFumble { get; set; }

    /// <summary>
    /// Outcome tier: CriticalMiss, Miss, GlancingHit, Hit, CriticalHit.
    /// Set after comparing roll vs target DC/defense.
    /// </summary>
    public RollOutcome Outcome { get; set; } = RollOutcome.None;

    /// <summary>
    /// The target number (defense, DC) this roll was checked against.
    /// </summary>
    public int? TargetNumber { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RollOutcome
{
    None,
    CriticalMiss,
    Miss,
    GlancingHit,
    Hit,
    CriticalHit
}
