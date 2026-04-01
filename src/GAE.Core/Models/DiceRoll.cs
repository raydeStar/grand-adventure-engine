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
}
