using GAE.Core.Models;
using GAE.Discord.CharacterCreation;

namespace GAE.Engine.Tests;

public class CharacterCreationTests
{
    // ==================== SheetOverrides.Apply ====================

    [Fact]
    public void Apply_ChangeRace_OverridesEvenIfAiIgnored()
    {
        var previous = new CharacterCreationAiResponse { Name = "Ignignok", Race = "Human", Class = "Fighter" };
        var current = new CharacterCreationAiResponse { Name = "Ignignok", Race = "Human", Class = "Fighter" };

        SheetOverrides.Apply("Make the race Mooninite", current, previous);

        Assert.Equal("Mooninite", current.Race);
        Assert.Equal("Fighter", current.Class); // unchanged
        Assert.Equal("Ignignok", current.Name); // unchanged
    }

    [Fact]
    public void Apply_ChangeRaceTo_OverridesCorrectly()
    {
        var previous = new CharacterCreationAiResponse { Race = "Human", Class = "Rogue" };
        var current = new CharacterCreationAiResponse { Race = "Human", Class = "Rogue" };

        SheetOverrides.Apply("change race to Duck", current, previous);

        Assert.Equal("Duck", current.Race);
    }

    [Fact]
    public void Apply_SetRace_OverridesCorrectly()
    {
        var previous = new CharacterCreationAiResponse { Race = "Elf" };
        var current = new CharacterCreationAiResponse { Race = "Elf" };

        SheetOverrides.Apply("set race to Sentient Mushroom", current, previous);

        Assert.Equal("Sentient Mushroom", current.Race);
    }

    [Fact]
    public void Apply_RaceColon_OverridesCorrectly()
    {
        var previous = new CharacterCreationAiResponse { Race = "Human" };
        var current = new CharacterCreationAiResponse { Race = "Human" };

        SheetOverrides.Apply("race: mooninite", current, previous);

        Assert.Equal("Mooninite", current.Race);
    }

    [Fact]
    public void Apply_ChangeClass_OverridesEvenIfAiIgnored()
    {
        var previous = new CharacterCreationAiResponse { Race = "Duck", Class = "Rogue" };
        var current = new CharacterCreationAiResponse { Race = "Duck", Class = "Rogue" };

        SheetOverrides.Apply("make the class Hitman", current, previous);

        Assert.Equal("Hitman", current.Class);
        Assert.Equal("Duck", current.Race); // unchanged
    }

    [Fact]
    public void Apply_ChangeClassTo_OverridesCorrectly()
    {
        var previous = new CharacterCreationAiResponse { Class = "Fighter" };
        var current = new CharacterCreationAiResponse { Class = "Fighter" };

        SheetOverrides.Apply("change class to Cheese Wizard", current, previous);

        Assert.Equal("Cheese Wizard", current.Class);
    }

    [Fact]
    public void Apply_ChangeName_OverridesCorrectly()
    {
        var previous = new CharacterCreationAiResponse { Name = "Bob" };
        var current = new CharacterCreationAiResponse { Name = "Bob" };

        SheetOverrides.Apply("change name to Sir Quacksalot", current, previous);

        Assert.Equal("Sir Quacksalot", current.Name);
    }

    [Fact]
    public void Apply_SetNameTo_OverridesCorrectly()
    {
        var previous = new CharacterCreationAiResponse { Name = "Old Name" };
        var current = new CharacterCreationAiResponse { Name = "Old Name" };

        SheetOverrides.Apply("set the name to Ignignok the Destroyer", current, previous);

        Assert.Equal("Ignignok The Destroyer", current.Name);
    }

    [Fact]
    public void Apply_PreventAiRevertingCustomRace()
    {
        // Player previously set race to "Mooninite", AI reverted to "Human" on next exchange
        var previous = new CharacterCreationAiResponse { Race = "Mooninite", Class = "Fighter" };
        var current = new CharacterCreationAiResponse { Race = "Human", Class = "Fighter" };

        SheetOverrides.Apply("make the backstory more dramatic", current, previous);

        Assert.Equal("Mooninite", current.Race); // preserved, not reverted
    }

    [Fact]
    public void Apply_PreventAiRevertingCustomClass()
    {
        var previous = new CharacterCreationAiResponse { Race = "Human", Class = "Hitman" };
        var current = new CharacterCreationAiResponse { Race = "Human", Class = "Fighter" };

        SheetOverrides.Apply("add more to the backstory", current, previous);

        Assert.Equal("Hitman", current.Class); // preserved, not reverted
    }

    [Fact]
    public void Apply_AllowLegitimateRaceChange()
    {
        // If the player explicitly asks for a different race, allow it
        var previous = new CharacterCreationAiResponse { Race = "Mooninite" };
        var current = new CharacterCreationAiResponse { Race = "Human" }; // AI changed it

        SheetOverrides.Apply("change race to Elf", current, previous);

        Assert.Equal("Elf", current.Race); // the new explicit request wins
    }

    [Fact]
    public void Apply_MultipleChangesAtOnce_OnlyMatchesFirst()
    {
        // If input has both race and class patterns, both should apply
        var previous = new CharacterCreationAiResponse { Race = "Human", Class = "Fighter" };
        var current = new CharacterCreationAiResponse { Race = "Human", Class = "Fighter" };

        // These are separate patterns so should both match
        SheetOverrides.Apply("change race to Duck", current, previous);
        SheetOverrides.Apply("change class to Hitman", current, previous);

        Assert.Equal("Duck", current.Race);
        Assert.Equal("Hitman", current.Class);
    }

    [Fact]
    public void Apply_NormalConversation_DoesNotTriggerOverrides()
    {
        var previous = new CharacterCreationAiResponse { Name = "Bob", Race = "Elf", Class = "Mage" };
        var current = new CharacterCreationAiResponse { Name = "Bob", Race = "Elf", Class = "Mage" };

        SheetOverrides.Apply("I like the backstory but make me stronger", current, previous);

        Assert.Equal("Elf", current.Race);
        Assert.Equal("Mage", current.Class);
        Assert.Equal("Bob", current.Name);
    }

    [Fact]
    public void Apply_PreservesNameWhenAiDropsIt()
    {
        var previous = new CharacterCreationAiResponse { Name = "Ignignok" };
        var current = new CharacterCreationAiResponse { Name = null };

        SheetOverrides.Apply("bump my strength", current, previous);

        Assert.Equal("Ignignok", current.Name);
    }

    // ==================== CharacterCreationSession (Fallback Wizard) ====================

    [Fact]
    public void FallbackWizard_FullFlow_CompletesIn4Steps()
    {
        var session = new CharacterCreationSession("test-123");

        // Step 1: Name
        var response1 = session.ProcessInput("Yuric");
        Assert.False(session.IsComplete);
        Assert.Contains("What are you", response1);

        // Step 2: Race
        var response2 = session.ProcessInput("Duck");
        Assert.False(session.IsComplete);
        Assert.Contains("What do you do", response2);

        // Step 3: Class
        var response3 = session.ProcessInput("Hitman");
        Assert.False(session.IsComplete);
        Assert.Contains("backstory", response3);

        // Step 4: Backstory
        var response4 = session.ProcessInput("A professional duck hitman who likes cats");
        Assert.True(session.IsComplete);
    }

    [Fact]
    public void FallbackWizard_ToConcept_PreservesCustomRaceAndClass()
    {
        var session = new CharacterCreationSession("test-456");
        session.ProcessInput("Ignignok");
        session.ProcessInput("Mooninite");
        session.ProcessInput("Moon Warrior");
        session.ProcessInput("skip");

        var concept = session.ToConcept();

        Assert.Equal("Ignignok", concept.Name);
        Assert.Equal("Mooninite", concept.Race);
        Assert.Equal("Moon Warrior", concept.Class);
        Assert.Equal(StatAllocationMethod.StandardArray, concept.StatMethod);
    }

    [Fact]
    public void FallbackWizard_SkipBackstory_SetsEmpty()
    {
        var session = new CharacterCreationSession("test-789");
        session.ProcessInput("Bob");
        session.ProcessInput("Human");
        session.ProcessInput("Fighter");
        session.ProcessInput("skip");

        var concept = session.ToConcept();

        Assert.Equal(string.Empty, concept.Backstory);
    }

    [Fact]
    public void FallbackWizard_NoStatStep_Exists()
    {
        var session = new CharacterCreationSession("test-000");

        session.ProcessInput("Name");
        var r2 = session.ProcessInput("Race");
        var r3 = session.ProcessInput("Class");

        // After class, should go straight to backstory — no "stat" step
        Assert.Contains("backstory", r3);
        Assert.DoesNotContain("stat", r3.ToLowerInvariant());
    }
}
