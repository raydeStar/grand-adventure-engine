using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Easter eggs must be delightful without being disruptive: they resolve instantly, never cost a
/// turn, and never shadow a real command.
/// </summary>
public class EasterEggTests
{
    private const string PlayerId = "egg-hunter";

    [Theory]
    // Text adventures and old-school crawlers
    [InlineData("xyzzy")]
    [InlineData("plugh")]
    [InlineData("elbereth")]
    [InlineData("grue")]
    // Monty Python and the Holy Grail
    [InlineData("ni")]
    [InlineData("shrubbery")]
    [InlineData("holy hand grenade")]
    [InlineData("airspeed velocity")]
    // Final Fantasy
    [InlineData("fanfare")]
    [InlineData("save point")]
    [InlineData("chocobo")]
    [InlineData("excalipoor")]
    // Weird Al
    [InlineData("accordion")]
    [InlineData("polka")]
    // Dungeon Crawler Carl
    [InlineData("crawler")]
    [InlineData("sponsor")]
    [InlineData("achievement")]
    public void MagicWords_ParseAsCommandsAndProduceAResponse(string word)
    {
        var engine = CreateEngine(new InMemoryStateManager(), CreateNarrator().Object);

        var action = engine.ParseCommand(PlayerId, word);
        Assert.Equal(ActionType.MagicWord, action.Type);

        Assert.True(EasterEggs.TryGetMagicWordResponse(word, action.Id, out var response));
        Assert.False(string.IsNullOrWhiteSpace(response));
    }

    [Fact]
    public void EveryMagicWord_HasANonEmptyResponse()
    {
        foreach (var word in EasterEggs.AllMagicWords)
        {
            Assert.True(EasterEggs.TryGetMagicWordResponse(word, "seed", out var response), word);
            Assert.False(string.IsNullOrWhiteSpace(response), word);
        }
    }

    /// <summary>
    /// Magic words are matched on the whole input. Short triggers like "ni" must not hijack an
    /// ordinary sentence that happens to contain them.
    /// </summary>
    [Theory]
    [InlineData("ni hao to the innkeeper")]
    [InlineData("i want to talk about the shrubbery in the garden")]
    [InlineData("ask cid about the airship")]
    public void MagicWords_DoNotHijackOrdinarySentences(string input)
    {
        var engine = CreateEngine(new InMemoryStateManager(), CreateNarrator().Object);

        Assert.NotEqual(ActionType.MagicWord, engine.ParseCommand(PlayerId, input).Type);
    }

    /// <summary>A magic word must never shadow a real command of the same or similar name.</summary>
    [Theory]
    [InlineData("look", ActionType.Look)]
    [InlineData("help", ActionType.Help)]
    [InlineData("inventory", ActionType.Inventory)]
    [InlineData("rest", ActionType.Rest)]
    [InlineData("flee", ActionType.Flee)]
    public void RealCommands_WinOverMagicWords(string input, ActionType expected)
    {
        var engine = CreateEngine(new InMemoryStateManager(), CreateNarrator().Object);

        Assert.Equal(expected, engine.ParseCommand(PlayerId, input).Type);
    }

    /// <summary>Saying a magic word mid-fight must not hand the enemy a free round.</summary>
    [Fact]
    public async Task MagicWordInCombat_CostsNoHpAndKeepsCombatActive()
    {
        var (stateManager, engine) = await StartCombatAsync();
        var before = await stateManager.GetPlayerAsync(PlayerId);
        var hpBefore = before!.Hp;

        var result = await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "xyzzy"));

        Assert.False(string.IsNullOrWhiteSpace(result.MechanicalSummary));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(hpBefore, after!.Hp);
        Assert.Equal(InteractionMode.Combat, after.Interaction.Mode);
    }

    [Fact]
    public async Task MagicWordInConversation_DoesNotEndTheConversation()
    {
        var stateManager = new InMemoryStateManager();
        await SeedAsync(stateManager, new Npc { Id = "mara", Name = "Mara", Disposition = "friendly" });
        var engine = CreateEngine(stateManager, CreateNarrator().Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "talk to mara"));

        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "xyzzy"));

        var after = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Conversation, after!.Interaction.Mode);
    }

    /// <summary>Responses vary between invocations rather than repeating one line forever.</summary>
    [Fact]
    public void MagicWordsWithSeveralResponses_Vary()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 40; i++)
        {
            EasterEggs.TryGetMagicWordResponse("xyzzy", $"action-{i}", out var response);
            seen.Add(response);
        }

        Assert.True(seen.Count > 1, "Expected xyzzy to have more than one possible reply.");
    }

    /// <summary>The same action id must always produce the same reply, so replays stay stable.</summary>
    [Fact]
    public void MagicWordResponse_IsStableForAGivenActionId()
    {
        EasterEggs.TryGetMagicWordResponse("xyzzy", "fixed-id", out var first);
        EasterEggs.TryGetMagicWordResponse("xyzzy", "fixed-id", out var second);

        Assert.Equal(first, second);
    }

    // ── Combat flavour ──

    [Fact]
    public void WoundedBravado_OnlyFiresForBadlyHurtEnemies()
    {
        // Healthy enemies never boast, whatever the seed.
        for (var seed = 0; seed < 60; seed++)
            Assert.Null(EasterEggs.TryBuildDefiantWoundedTaunt("Knight", hp: 90, maxHp: 100, seed));

        // Badly wounded ones sometimes do.
        var fired = Enumerable.Range(0, 60)
            .Select(seed => EasterEggs.TryBuildDefiantWoundedTaunt("Knight", hp: 5, maxHp: 100, seed))
            .Count(taunt => taunt is not null);

        Assert.InRange(fired, 1, 59);
    }

    [Fact]
    public void WoundedBravado_NamesTheEnemy()
    {
        var taunt = Enumerable.Range(0, 60)
            .Select(seed => EasterEggs.TryBuildDefiantWoundedTaunt("Black Knight", hp: 1, maxHp: 100, seed))
            .First(t => t is not null);

        Assert.Contains("Black Knight", taunt!, StringComparison.Ordinal);
    }

    [Fact]
    public void VictoryFlourish_IsOccasionalNotConstant()
    {
        var fired = Enumerable.Range(0, 100)
            .Count(seed => EasterEggs.TryBuildVictoryFlourish(seed) is not null);

        Assert.InRange(fired, 1, 99);
    }

    [Fact]
    public void DeathEpitaph_AlwaysProducesText()
    {
        for (var seed = -20; seed < 20; seed++)
            Assert.False(string.IsNullOrWhiteSpace(EasterEggs.BuildDeathEpitaph(seed)));
    }

    // ── Harness ──

    private static async Task<(InMemoryStateManager, GameEngine)> StartCombatAsync()
    {
        var stateManager = new InMemoryStateManager();
        await SeedAsync(stateManager, new Npc
        {
            Id = "goblin", Name = "Goblin", IsHostile = true, Level = 1,
            Hp = 400, MaxHp = 400, AttackBonus = 0, DamageDice = "1d2", Defense = 99
        });

        var engine = CreateEngine(stateManager, CreateNarrator().Object);
        await engine.ProcessActionAsync(PlayerId, engine.ParseCommand(PlayerId, "attack goblin"));

        var player = await stateManager.GetPlayerAsync(PlayerId);
        Assert.Equal(InteractionMode.Combat, player!.Interaction.Mode);
        return (stateManager, engine);
    }

    private static async Task SeedAsync(InMemoryStateManager stateManager, Npc npc)
    {
        await stateManager.SaveRoomAsync(new Room
        {
            Id = "tavern",
            Name = "The Rusted Flagon",
            Description = "A creaky tavern.",
            Npcs = [npc],
            Exits = new Dictionary<string, string> { ["north"] = "street" }
        });

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId, Name = "Probe", Race = "Human", Class = "Warrior", Level = 3,
            CurrentRoomId = "tavern", Hp = 40, MaxHp = 40, Mp = 10, MaxMp = 10, Gold = 50,
            Str = 12, Dex = 10, Con = 11, Int = 10, Wis = 10, Cha = 10
        });
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

        narrator.Setup(s => s.ProcessConversationTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, Room _, Npc n, InteractionState _, string _, CancellationToken _) => new FreeFormResponse
            {
                Success = true,
                Narration = $"\"{n.Name} answers.\"",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Conversation }
            });

        narrator.Setup(s => s.ProcessCombatTurnAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<Npc>(),
                It.IsAny<InteractionState>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "Steel rings.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Combat }
            });

        narrator.Setup(s => s.ProcessFreeFormAsync(
                It.IsAny<PlayerCharacter>(), It.IsAny<Room>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<StoryEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FreeFormResponse
            {
                Success = true,
                Narration = "The world responds.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            });

        narrator.Setup(s => s.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Narration.");

        return narrator;
    }
}
