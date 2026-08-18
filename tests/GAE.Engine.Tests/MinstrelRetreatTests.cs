using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// The retreat minstrels: fleeing combat is commemorated in song, whichever words the player used
/// to run away.
/// </summary>
public class MinstrelRetreatTests
{
    private const string PlayerId = "coward";
    private const string PlayerName = "Robin";

    [Theory]
    [InlineData("flee")]
    [InlineData("run")]
    [InlineData("give up")]
    [InlineData("go north")]
    [InlineData("leave")]
    public async Task FleeingCombat_SummonsTheMinstrels(string input)
    {
        var (stateManager, engine) = await StartCombatAsync();

        var result = await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, input));

        Assert.Contains("\U0001F3B5", result.MechanicalSummary, StringComparison.Ordinal);
        Assert.Contains($"Sir {PlayerName}", result.MechanicalSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMechanicalOutcomeSurvivesTheSong()
    {
        var (stateManager, engine) = await StartCombatAsync();

        var result = await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "flee"));

        // The taunt is an addition, not a replacement — the player still needs to know they escaped.
        Assert.Contains("flee", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Explore, player!.Interaction.Mode);
    }

    /// <summary>A character already styled "Sir Lancelot" must not become "Sir Sir Lancelot".</summary>
    [Fact]
    public async Task NamesAlreadyStyledSir_AreNotDoubledUp()
    {
        var (_, engine) = await StartCombatAsync(playerName: "Sir Lancelot");

        var result = await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "flee"));

        Assert.Contains("Sir Lancelot", result.MechanicalSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("Sir Sir", result.MechanicalSummary, StringComparison.Ordinal);
    }

    /// <summary>Repeat cowardice should not repeat the same line every single time.</summary>
    [Fact]
    public async Task RepeatedRetreats_ProduceMoreThanOneTaunt()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            var (_, engine) = await StartCombatAsync();
            var result = await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "flee"));
            var taunt = result.MechanicalSummary
                .Split('\n')
                .FirstOrDefault(line => line.Contains("\U0001F3B5", StringComparison.Ordinal));
            if (taunt is not null)
                seen.Add(taunt.Trim());
        }

        Assert.True(seen.Count > 1, $"Expected varied taunts across retreats but only saw: {string.Join(" | ", seen)}");
    }

    // ── Harness ──

    private static async Task<(InMemoryStateManager, GameEngine)> StartCombatAsync(string playerName = PlayerName)
    {
        var stateManager = new InMemoryStateManager();

        await stateManager.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "The Rusted Flagon",
            Description = "A creaky tavern.",
            Npcs =
            [
                new Npc
                {
                    Id = "goblin", Name = "Goblin", IsHostile = true, Level = 1,
                    Hp = 400, MaxHp = 400, AttackBonus = 0, DamageDice = "1d2", Defense = 99
                }
            ],
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        });

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId, Name = playerName, Race = "Human", Class = "Warrior", Level = 3,
            CurrentRoomId = "tavern", Hp = 40, MaxHp = 40, Mp = 10, MaxMp = 10,
            Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10
        });

        var engine = CreateEngine(stateManager, CreateNarrator().Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "attack goblin"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Combat, player!.Interaction.Mode);
        return (stateManager, engine);
    }

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string e, string p) => new DiceRoll { Expression = e, Purpose = p, IndividualRolls = [1], Total = 1 });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string e, int m) => new DiceRoll { Expression = e, Purpose = "dmg", IndividualRolls = [1], Modifier = m, Total = 1 + m });
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns((int m) => new DiceRoll { Expression = "1d20", Purpose = "atk", IndividualRolls = [2], Modifier = m, Total = 2 + m });
        dice.Setup(d => d.RollSkillCheck(It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string s, int m) => new DiceRoll { Expression = "1d20", Purpose = s, IndividualRolls = [10], Modifier = m, Total = 10 + m });
        dice.Setup(d => d.RollInitiative(It.IsAny<int>()))
            .Returns((int m) => new DiceRoll { Expression = "1d20", Purpose = "init", IndividualRolls = [10], Modifier = m, Total = 10 + m });

        return new GameEngine(
            stateManager,
            dice.Object,
            narrator,
            new CommandParser(NullLogger<CommandParser>.Instance),
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }

    private static Mock<INarratorService> CreateNarrator()
    {
        var narrator = new Mock<INarratorService>();

        narrator.Setup(s => s.ProcessCombatTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "Steel rings.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Combat }
            });

        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narration.");

        return narrator;
    }
}
