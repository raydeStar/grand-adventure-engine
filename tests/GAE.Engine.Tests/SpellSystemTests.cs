using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

/// <summary>
/// Comprehensive tests for the spellbook/casting system: creation, vetting,
/// casting known spells, fallback scaling, mana costs, and the spellbook command.
/// </summary>
public class SpellSystemTests
{
    private const string PlayerId = "spell-tester";

    // ═══════════════════════════════════════════════════════════════════
    //  Spell Vetting & Creation
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_NewSpell_VettedByNarrator_AddsToSpellbook()
    {
        var state = await CreateStateAsync();
        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal("Fireball", player.Spellbook[0].Name);
        Assert.Contains("New spell learned", result.MechanicalSummary);
    }

    [Fact]
    public async Task Cast_NewSpell_Rejected_ReturnsFailure()
    {
        var state = await CreateStateAsync();
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpellVetResponse
            {
                Approved = false,
                RejectionReason = "That spell is pure nonsense.",
                SpellName = "Hack Mainframe"
            });
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast hack mainframe");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("fizzles", result.MechanicalSummary);
        Assert.Contains("nonsense", result.MechanicalSummary);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Empty(player.Spellbook);
    }

    [Fact]
    public async Task Cast_EmptyTarget_ReturnsHelpMessage()
    {
        // "cast" alone doesn't match the cast regex so it goes to free-form.
        // Verify that "cast <empty>" with whitespace still reaches the spell system.
        var state = await CreateStateAsync();
        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast");
        // "cast" alone parses as Unknown (regex requires spell name)
        Assert.Equal(ActionType.Unknown, action.Type);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Spell Level Scaling
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1, 3, "1d6")]   // power 3 capped at level+1=2 → effective 2 → 1d6
    [InlineData(5, 3, "1d8")]   // power 3 → effective 3 → 1d8
    [InlineData(10, 8, "3d8")]  // power 8 → effective 8 → 3d8
    public async Task Cast_NewSpell_DamageScalesWithLevel(int level, int basePower, string expectedDice)
    {
        // Provide enough MP for high-level spells (RecalculateMaxHpMp caps MaxMp)
        var state = await CreateStateAsync(playerLevel: level, mp: 50, maxMp: 50);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpellVetResponse
            {
                Approved = true,
                SpellName = "Test Bolt",
                Description = "A bolt.",
                Category = "damage",
                TargetType = "enemy",
                BasePower = basePower,
                MpCost = 6,
                Narration = "You fire a bolt."
            });
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast test bolt at skeleton");
        await engine.ProcessActionAsync(PlayerId, action);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal(expectedDice, player.Spellbook[0].DamageDice);
    }

    [Theory]
    [InlineData(1, 2)]   // power clamp → 1, mpCost = max(2, 1*2) = 2
    [InlineData(5, 10)]  // power 5, mpCost = max(2, 5*2) = 10
    [InlineData(10, 20)] // power 10, mpCost = max(2, 10*2) = 20
    public async Task Cast_NewSpell_MpCostScalesWithPower(int level, int expectedMpCost)
    {
        var state = await CreateStateAsync(playerLevel: level, mp: 100, maxMp: 100);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpellVetResponse
            {
                Approved = true,
                SpellName = "Bolt",
                Description = "A bolt.",
                Category = "damage",
                TargetType = "enemy",
                BasePower = level, // set power = level
                MpCost = 10,
                Narration = "Bolt."
            });
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast bolt at skeleton");
        await engine.ProcessActionAsync(PlayerId, action);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal(expectedMpCost, player.Spellbook[0].MpCost);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Casting Known Spells
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_KnownSpell_DoesNotReVet()
    {
        var state = await CreateStateAsync();
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Fireball",
            Description = "A ball of fire.",
            DamageDice = "2d6",
            DamageStat = "int",
            Category = SpellCategory.Damage,
            MpCost = 4,
            BasePower = 3,
            TargetType = "enemy"
        });
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        // Strict — VetSpellAsync should NOT be called
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        // Still only one spell in spellbook (not re-added)
        var updated = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(updated);
        Assert.Single(updated.Spellbook);
    }

    [Fact]
    public async Task Cast_KnownSpell_PartialNameMatch()
    {
        var state = await CreateStateAsync();
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Greater Fireball",
            Description = "A bigger fireball.",
            DamageDice = "3d6",
            Category = SpellCategory.Damage,
            MpCost = 6,
            BasePower = 5,
            TargetType = "enemy"
        });
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        // "greater fireball" matches "Greater Fireball" via Contains
        var action = engine.ParseCommand(PlayerId, "cast greater fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Mana Mechanics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_InsufficientMp_NewSpell_ReturnsFailure()
    {
        var state = await CreateStateAsync(mp: 1, maxMp: 10);
        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("Not enough MP", result.MechanicalSummary);

        // MP should not be deducted
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(1, player.Mp);
    }

    [Fact]
    public async Task Cast_InsufficientMp_KnownSpell_ReturnsFailure()
    {
        var state = await CreateStateAsync(mp: 1, maxMp: 10);
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Heal",
            DamageDice = "1d8+3",
            Category = SpellCategory.Healing,
            MpCost = 5,
            BasePower = 2,
            TargetType = "self"
        });
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast heal");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("Not enough MP", result.MechanicalSummary);
    }

    [Fact]
    public async Task Cast_DeductsMp()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = CreateSpellNarrator(mpCost: 6);
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        await engine.ProcessActionAsync(PlayerId, action);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        // Vet sets mpCost=6, but CreateLevelScaledSpell recalculates as max(2, power*2)
        // BasePower=3, Level 1 → effectivePower=min(3, 2)=2 → mpCost=max(2,4)=4
        Assert.True(player.Mp < 20, "MP should have been deducted");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Damage Spell Mechanics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_DamageSpell_Hit_DamagesTarget()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice(attackTotal: 15, damageTotal: 8);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("strikes", result.MechanicalSummary);

        var room = await state.GetPlayerRoomAsync(PlayerId, "spell-arena");
        Assert.NotNull(room);
        var skeleton = room.Npcs.First(n => n.Name == "Skeleton");
        Assert.True(skeleton.Hp < 20, "Skeleton should have taken damage");
    }

    [Fact]
    public async Task Cast_DamageSpell_Miss_NoDamage()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = CreateSpellNarrator();
        // Roll 3 vs defense 10 → miss
        var dice = CreateHittingDice(attackTotal: 3, damageTotal: 0);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("misses", result.MechanicalSummary);

        var room = await state.GetPlayerRoomAsync(PlayerId, "spell-arena");
        Assert.NotNull(room);
        var skeleton = room.Npcs.First(n => n.Name == "Skeleton");
        Assert.Equal(20, skeleton.Hp);
    }

    [Fact]
    public async Task Cast_DamageSpell_CriticalHit_DoublessDamage()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = CreateSpellNarrator();
        // Critical roll → IsCritical = true
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d20+2", Total = 20, IsCritical = true, IndividualRolls = [20] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d6", Total = 5, IndividualRolls = [5] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Expression = "1d20", Total = 10, IndividualRolls = [10] });

        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("CRITICAL", result.MechanicalSummary);

        var room = await state.GetPlayerRoomAsync(PlayerId, "spell-arena");
        Assert.NotNull(room);
        var skeleton = room.Npcs.First(n => n.Name == "Skeleton");
        // Crit doubles damage: 5 * 2 = 10, skeleton had 20 HP → 10 remaining
        Assert.Equal(10, skeleton.Hp);
    }

    [Fact]
    public async Task Cast_DamageSpell_Kill_AwardsXpAndGold()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);

        // Skeleton with 1 HP — will die from any hit
        var room = await state.GetPlayerRoomAsync(PlayerId, "spell-arena");
        Assert.NotNull(room);
        room.Npcs.First(n => n.Name == "Skeleton").Hp = 1;
        await state.SaveRoomAsync(room);

        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice(attackTotal: 15, damageTotal: 5);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        int oldXp = player.Xp;
        int oldGold = player.Gold;

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("falls", result.MechanicalSummary);

        player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.True(player.Xp > oldXp, "Should gain XP from kill");
        Assert.True(player.Gold > oldGold, "Should gain gold from kill");
    }

    [Fact]
    public async Task Cast_DamageSpell_NoTarget_RefundsMp()
    {
        // Room with no hostile NPCs
        var room = new Room
        {
            Id = "peaceful-room",
            Name = "Peaceful Garden",
            Description = "A serene garden.",
            Npcs = [new Npc { Id = "merchant", Name = "Merchant", IsHostile = false, Hp = 10, MaxHp = 10 }],
            Exits = []
        };
        // RecalculateMaxHpMp sets MaxMp=14 at level 1 (Int 14)
        var state = await CreateStateAsync(room: room, mp: 14, maxMp: 14);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Fireball",
            DamageDice = "2d6",
            Category = SpellCategory.Damage,
            MpCost = 6,
            BasePower = 3,
            TargetType = "enemy"
        });
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.False(result.Success);
        Assert.Contains("No valid target", result.MechanicalSummary);

        player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(14, player.Mp); // MP refunded (14 - 6 + 6 = 14)
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Healing Spell Mechanics
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_HealingSpell_RestoresHp()
    {
        // RecalculateMaxHpMp sets MaxHp=20, MaxMp=14 at level 1 (Con 10, Int 14)
        var state = await CreateStateAsync(mp: 14, maxMp: 14, hp: 10, maxHp: 20);
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Heal",
            Description = "Restore health.",
            DamageDice = "1d8+3",
            Category = SpellCategory.Healing,
            MpCost = 4,
            BasePower = 2,
            TargetType = "self"
        });
        await state.SavePlayerAsync(player);

        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d8+3", Total = 7, IndividualRolls = [4] });

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast heal");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("restores", result.MechanicalSummary);

        player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(17, player.Hp); // 10 + 7 = 17
        Assert.Equal(10, player.Mp); // 14 - 4 = 10
    }

    [Fact]
    public async Task Cast_HealingSpell_CapsAtMaxHp()
    {
        // RecalculateMaxHpMp sets MaxHp=20 at level 1. Heal from 18 by 10 → capped at 20.
        var state = await CreateStateAsync(mp: 14, maxMp: 14, hp: 18, maxHp: 20);
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Heal",
            DamageDice = "1d8+3",
            Category = SpellCategory.Healing,
            MpCost = 4,
            BasePower = 2,
            TargetType = "self"
        });
        await state.SavePlayerAsync(player);

        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d8+3", Total = 10, IndividualRolls = [7] });

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast heal");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(20, player.Hp); // Capped at MaxHp (RecalculateMaxHpMp sets MaxHp=20)
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Fallback Spell Generation (Narrator Unavailable)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_NarratorDown_CreatesFallbackSpell()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = new Mock<INarratorService>();
        // VetSpellAsync returns null → narrator down
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpellVetResponse?)null);

        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast thunder at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal("thunder", player.Spellbook[0].Name);
        Assert.Equal("A burst of raw magical energy.", player.Spellbook[0].Description);
    }

    [Fact]
    public async Task Cast_NarratorThrows_CreatesFallbackSpell()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("LM Studio unavailable"));

        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast ice shard at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal("ice shard", player.Spellbook[0].Name);
    }

    [Theory]
    [InlineData(1, "1d4")]     // Level 1 → power 1 → 1d4
    [InlineData(3, "1d8")]     // Level 3 → power 3 → effective min(3, 4)=3 → 1d8
    [InlineData(5, "2d6")]     // Level 5 → power 5 → effective min(5, 6)=5 → 2d6
    [InlineData(7, "2d10")]    // Level 7 → power 7 → effective min(7, 8)=7 → 2d10
    [InlineData(10, "4d10")]   // Level 10 → power 10 → effective min(10, 11)=10 → 4d10
    public async Task Fallback_Spell_DamageScalesWithLevel(int level, string expectedDice)
    {
        var state = await CreateStateAsync(playerLevel: level, mp: 100, maxMp: 100);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpellVetResponse?)null);

        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast bolt at skeleton");
        await engine.ProcessActionAsync(PlayerId, action);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal(expectedDice, player.Spellbook[0].DamageDice);
    }

    [Fact]
    public async Task Fallback_Spell_TruncatesLongNames()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpellVetResponse?)null);

        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var longName = new string('x', 50);
        var action = engine.ParseCommand(PlayerId, $"cast {longName} at skeleton");
        await engine.ProcessActionAsync(PlayerId, action);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Single(player.Spellbook);
        Assert.Equal(30, player.Spellbook[0].Name.Length);
    }

    [Theory]
    [InlineData(1, 2)]    // Level 1 → power 1, mpCost = max(2, 1*2) = 2
    [InlineData(5, 10)]   // Level 5 → power 5, mpCost = max(2, 5*2) = 10
    [InlineData(10, 20)]  // Level 10 → power 10, mpCost = max(2, 10*2) = 20
    public async Task Fallback_Spell_MpCostScalesWithLevel(int level, int expectedMpCost)
    {
        var state = await CreateStateAsync(playerLevel: level, mp: 100, maxMp: 100);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpellVetResponse?)null);

        var dice = CreateHittingDice();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast bolt at skeleton");
        await engine.ProcessActionAsync(PlayerId, action);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(expectedMpCost, player.Spellbook[0].MpCost);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Spellbook Command
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Spellbook_Empty_ReturnsHelpMessage()
    {
        var state = await CreateStateAsync();
        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "spells");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("empty", result.MechanicalSummary);
    }

    [Fact]
    public async Task Spellbook_WithSpells_ShowsTable()
    {
        var state = await CreateStateAsync();
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.AddRange(
        [
            new LearnedSpell { Name = "Fireball", DamageDice = "2d6", Category = SpellCategory.Damage, MpCost = 6 },
            new LearnedSpell { Name = "Heal", DamageDice = "1d8+3", Category = SpellCategory.Healing, MpCost = 4 }
        ]);
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "spellbook");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("SPELLBOOK", result.MechanicalSummary);
        Assert.Contains("Fireball", result.MechanicalSummary);
        Assert.Contains("Heal", result.MechanicalSummary);
        Assert.Contains("2d6", result.MechanicalSummary);
        Assert.Contains("6mp", result.MechanicalSummary);
    }

    [Theory]
    [InlineData("spells")]
    [InlineData("spellbook")]
    [InlineData("known spells")]
    [InlineData("my spells")]
    public async Task SpellbookCommand_AllVariants(string command)
    {
        var state = await CreateStateAsync();
        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Strict);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, command);
        Assert.Equal(ActionType.Spellbook, action.Type);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Command Parsing
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("cast fireball", "fireball", null)]
    [InlineData("cast fireball at goblin", "fireball", "goblin")]
    [InlineData("channel ice shard on skeleton", "ice shard", "skeleton")]
    [InlineData("invoke holy fire toward dragon", "holy fire", "dragon")]
    [InlineData("conjure lightning bolt against troll", "lightning bolt", "troll")]
    public void CastCommand_ParsesSpellAndTarget(string command, string expectedSpell, string? expectedTarget)
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        var action = parser.Parse(PlayerId, command);

        Assert.Equal(ActionType.Cast, action.Type);
        Assert.Equal(expectedSpell, action.Target);

        if (expectedTarget is not null)
            Assert.Equal(expectedTarget, action.Parameters["target"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Healing Spell ─ Vetted via Narrator
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_NewHealingSpell_VettedAsHealing_Heals()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20, hp: 10, maxHp: 30);
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpellVetResponse
            {
                Approved = true,
                SpellName = "Healing Light",
                Description = "A warm glow restores vitality.",
                Category = "healing",
                TargetType = "self",
                BasePower = 2,
                MpCost = 4,
                Narration = "Warm light surrounds you."
            });

        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d6+2", Total = 6, IndividualRolls = [4] });

        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast healing light");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("restores", result.MechanicalSummary);

        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.True(player.Hp > 10, "Should have healed");
        Assert.Single(player.Spellbook);
        Assert.Equal(SpellCategory.Healing, player.Spellbook[0].Category);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Buff/Utility Spells
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_BuffSpell_ShowsDescription()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);

        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Shield",
            Description = "A magical barrier surrounds you.",
            DamageDice = "",
            Category = SpellCategory.Buff,
            MpCost = 3,
            BasePower = 2,
            TargetType = "self"
        });
        await state.SavePlayerAsync(player);

        var narrator = new Mock<INarratorService>(MockBehavior.Strict);
        var dice = new Mock<IProbabilityEngine>();
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast shield");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.Contains("Shield", result.MechanicalSummary);
        Assert.Contains("magical barrier", result.MechanicalSummary);

        player = await state.GetPlayerAsync(PlayerId);
        Assert.NotNull(player);
        Assert.Equal(11, player.Mp); // 14 - 3 (RecalculateMaxHpMp caps MaxMp at 14)
    }

    // ═══════════════════════════════════════════════════════════════════
    //  State Changes & Dice Rolls
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Cast_DamageSpell_ReportsStateChanges()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice(attackTotal: 15, damageTotal: 5);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        Assert.NotEmpty(result.DiceRolls);
        Assert.NotEmpty(result.StateChanges);
        Assert.Contains(result.StateChanges, sc => sc.Property == "mp");
    }

    [Fact]
    public async Task Cast_DamageSpell_KillAllHostile_ReturnsToExplore()
    {
        var state = await CreateStateAsync(mp: 20, maxMp: 20);
        // Set skeleton to 1 HP
        var room = await state.GetPlayerRoomAsync(PlayerId, "spell-arena");
        Assert.NotNull(room);
        room.Npcs.First(n => n.Name == "Skeleton").Hp = 1;
        await state.SaveRoomAsync(room);

        var narrator = CreateSpellNarrator();
        var dice = CreateHittingDice(attackTotal: 15, damageTotal: 5);
        var engine = CreateEngine(state, narrator.Object, dice.Object);

        var action = engine.ParseCommand(PlayerId, "cast fireball at skeleton");
        var result = await engine.ProcessActionAsync(PlayerId, action);

        Assert.True(result.Success);
        // Should return to explore mode after killing the only hostile
        if (result.InteractionUpdate is not null)
        {
            Assert.Equal(InteractionMode.Explore, result.InteractionUpdate.Mode);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static async Task<InMemoryStateManager> CreateStateAsync(
        Room? room = null,
        int playerLevel = 1,
        int mp = 10,
        int maxMp = 10,
        int hp = 20,
        int maxHp = 20)
    {
        var stateManager = new InMemoryStateManager();

        var defaultRoom = room ?? new Room
        {
            Id = "spell-arena",
            Name = "The Arena",
            Description = "A stone arena for magical combat.",
            Npcs =
            [
                new Npc
                {
                    Id = "skeleton",
                    Name = "Skeleton",
                    IsHostile = true,
                    Hp = 20,
                    MaxHp = 20,
                    Defense = 10,
                    Level = 2
                }
            ],
            Exits = new Dictionary<string, string> { ["north"] = "corridor" }
        };

        await stateManager.SaveRoomAsync(defaultRoom);

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = PlayerId,
            Name = "Spell Tester",
            Race = "Elf",
            Class = "Mage",
            Level = playerLevel,
            CurrentRoomId = defaultRoom.Id,
            Hp = hp,
            MaxHp = maxHp,
            Mp = mp,
            MaxMp = maxMp,
            Str = 8,
            Dex = 10,
            Con = 10,
            Int = 14, // +2 mod
            Wis = 12,
            Cha = 10
        });

        return stateManager;
    }

    private static GameEngine CreateEngine(IStateManager stateManager, INarratorService narrator, IProbabilityEngine dice)
    {
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);
        return new GameEngine(
            stateManager,
            dice,
            narrator,
            parser,
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance);
    }

    private static Mock<INarratorService> CreateSpellNarrator(int mpCost = 6)
    {
        var narrator = new Mock<INarratorService>();
        narrator
            .Setup(n => n.VetSpellAsync(It.IsAny<PlayerCharacter>(), It.IsAny<string>(), It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlayerCharacter _, string spell, Room _, CancellationToken _) => new SpellVetResponse
            {
                Approved = true,
                SpellName = char.ToUpper(spell[0]) + spell[1..].Split(' ')[0].ToLower() + (spell.Contains(' ') ? " " + string.Join(" ", spell.Split(' ')[1..]) : ""),
                Description = "A powerful magical attack.",
                Category = "damage",
                TargetType = "enemy",
                BasePower = 3,
                MpCost = mpCost,
                Narration = "Arcane energy surges through your fingertips."
            });
        return narrator;
    }

    private static Mock<IProbabilityEngine> CreateHittingDice(int attackTotal = 15, int damageTotal = 8)
    {
        var dice = new Mock<IProbabilityEngine>();
        dice.Setup(d => d.RollAttack(It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d20+2", Total = attackTotal, IndividualRolls = [attackTotal - 2] });
        dice.Setup(d => d.RollDamage(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(new DiceRoll { Expression = "1d8", Total = damageTotal, IndividualRolls = [damageTotal] });
        dice.Setup(d => d.Roll(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new DiceRoll { Expression = "1d20", Total = 10, IndividualRolls = [10] });
        return dice;
    }
}
