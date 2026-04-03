using GAE.Core.Models;
using GAE.Discord.CharacterCreation;

namespace GAE.Engine.Tests;

public class CharacterCreationTests
{
    // ==================== SheetOverrides.Apply ====================

    [Fact]
    public void Override_ChangeRace_ForcesEvenIfAiIgnored()
    {
        var previous = new CharacterCreationAiResponse { Name = "Ignignok", Race = "Human", Class = "Fighter" };
        var current = new CharacterCreationAiResponse { Name = "Ignignok", Race = "Human", Class = "Fighter" };

        SheetOverrides.Apply("Make the race Mooninite", current, previous);

        Assert.Equal("Mooninite", current.Race);
        Assert.Equal("Fighter", current.Class);
        Assert.Equal("Ignignok", current.Name);
    }

    [Fact]
    public void Override_ChangeRaceTo_Works()
    {
        var previous = new CharacterCreationAiResponse { Race = "Human" };
        var current = new CharacterCreationAiResponse { Race = "Human" };

        SheetOverrides.Apply("change race to Duck", current, previous);
        Assert.Equal("Duck", current.Race);
    }

    [Fact]
    public void Override_SetRaceTo_Works()
    {
        var previous = new CharacterCreationAiResponse { Race = "Elf" };
        var current = new CharacterCreationAiResponse { Race = "Elf" };

        SheetOverrides.Apply("set race to Sentient Mushroom", current, previous);
        Assert.Equal("Sentient Mushroom", current.Race);
    }

    [Fact]
    public void Override_RaceColon_Works()
    {
        var previous = new CharacterCreationAiResponse { Race = "Human" };
        var current = new CharacterCreationAiResponse { Race = "Human" };

        SheetOverrides.Apply("race: mooninite", current, previous);
        Assert.Equal("Mooninite", current.Race);
    }

    [Fact]
    public void Override_ChangeClass_Works()
    {
        var previous = new CharacterCreationAiResponse { Race = "Duck", Class = "Rogue" };
        var current = new CharacterCreationAiResponse { Race = "Duck", Class = "Rogue" };

        SheetOverrides.Apply("make the class Hitman", current, previous);
        Assert.Equal("Hitman", current.Class);
        Assert.Equal("Duck", current.Race);
    }

    [Fact]
    public void Override_ChangeName_Works()
    {
        var previous = new CharacterCreationAiResponse { Name = "Bob" };
        var current = new CharacterCreationAiResponse { Name = "Bob" };

        SheetOverrides.Apply("change name to Sir Quacksalot", current, previous);
        Assert.Equal("Sir Quacksalot", current.Name);
    }

    [Fact]
    public void Override_PreventsAiRevertingCustomRace()
    {
        var previous = new CharacterCreationAiResponse { Race = "Mooninite", Class = "Fighter" };
        var current = new CharacterCreationAiResponse { Race = "Human", Class = "Fighter" };

        SheetOverrides.Apply("make the backstory more dramatic", current, previous);
        Assert.Equal("Mooninite", current.Race);
    }

    [Fact]
    public void Override_PreventsAiRevertingCustomClass()
    {
        var previous = new CharacterCreationAiResponse { Race = "Human", Class = "Hitman" };
        var current = new CharacterCreationAiResponse { Race = "Human", Class = "Fighter" };

        SheetOverrides.Apply("add more to the backstory", current, previous);
        Assert.Equal("Hitman", current.Class);
    }

    [Fact]
    public void Override_PreservesNameWhenAiDropsIt()
    {
        var previous = new CharacterCreationAiResponse { Name = "Ignignok" };
        var current = new CharacterCreationAiResponse { Name = null };

        SheetOverrides.Apply("bump my strength", current, previous);
        Assert.Equal("Ignignok", current.Name);
    }

    [Fact]
    public void Override_NormalConversation_NoChanges()
    {
        var previous = new CharacterCreationAiResponse { Name = "Bob", Race = "Elf", Class = "Mage" };
        var current = new CharacterCreationAiResponse { Name = "Bob", Race = "Elf", Class = "Mage" };

        SheetOverrides.Apply("I like the backstory but make me stronger", current, previous);

        Assert.Equal("Elf", current.Race);
        Assert.Equal("Mage", current.Class);
        Assert.Equal("Bob", current.Name);
    }

    // ==================== Fallback Wizard — Single Block Parsing ====================

    [Fact]
    public void Fallback_SingleBlock_FullDescription_CompletesImmediately()
    {
        var session = new CharacterCreationSession("test-1");
        var result = session.ProcessInput("My name is Yuric. I am a duck hitman who likes cats.");

        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal("Yuric", concept.Name);
        Assert.Equal("Duck", concept.Race);
        Assert.Equal("Hitman", concept.Class);
    }

    [Fact]
    public void Fallback_SingleBlock_NameAndRace_AsksForClass()
    {
        var session = new CharacterCreationSession("test-2");
        var result = session.ProcessInput("My name is Bob and I'm a mooninite");

        Assert.False(session.IsComplete);
        Assert.Contains("What do you do", result); // asking for class

        var result2 = session.ProcessInput("Moon Warrior");
        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal("Bob", concept.Name);
        Assert.Equal("Mooninite", concept.Race);
        Assert.Equal("Moon Warrior", concept.Class);
    }

    [Fact]
    public void Fallback_SingleBlock_JustName_AsksForRace()
    {
        var session = new CharacterCreationSession("test-3");
        var result = session.ProcessInput("My name is Ignignok");

        Assert.False(session.IsComplete);
        Assert.Contains("What are you", result); // asking for race
    }

    [Fact]
    public void Fallback_SingleBlock_ClassAndRace_AsksForName()
    {
        var session = new CharacterCreationSession("test-4");
        var result = session.ProcessInput("I am an elf ranger");

        Assert.False(session.IsComplete);
        Assert.Contains("name", result.ToLowerInvariant());

        var result2 = session.ProcessInput("Legolas");
        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal("Legolas", concept.Name);
        Assert.Equal("Elf", concept.Race);
        Assert.Equal("Ranger", concept.Class);
    }

    [Fact]
    public void Fallback_SingleBlock_ExplicitFields_Work()
    {
        var session = new CharacterCreationSession("test-5");
        var result = session.ProcessInput("name: Quackers, race: Duck, class: Pirate");

        // Should parse all three from explicit field syntax
        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal("Quackers", concept.Name);
        Assert.Equal("Duck", concept.Race);
        Assert.Equal("Pirate", concept.Class);
    }

    [Fact]
    public void Fallback_SingleBlock_KnownRaceAndClass_ExtractsCorrectly()
    {
        var session = new CharacterCreationSession("test-6");
        var result = session.ProcessInput("My name is Grimjaw. I am a dwarf barbarian from the frozen wastes.");

        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal("Grimjaw", concept.Name);
        Assert.Equal("Dwarf", concept.Race);
        Assert.Equal("Barbarian", concept.Class);
    }

    [Fact]
    public void Fallback_SingleBlock_CustomRaceKnownClass_Works()
    {
        var session = new CharacterCreationSession("test-7");
        var result = session.ProcessInput("My name is Ignignok. I am a mooninite warrior from the moon!");

        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal("Ignignok", concept.Name);
        Assert.Equal("Mooninite", concept.Race);
        Assert.Equal("Warrior", concept.Class);
    }

    [Fact]
    public void Fallback_StepByStep_StillWorks()
    {
        var session = new CharacterCreationSession("test-8");

        // Very short input with no parseable info
        var r1 = session.ProcessInput("hi");
        Assert.False(session.IsComplete);

        // Should ask for name first
        session.ProcessInput("Yuric");
        Assert.False(session.IsComplete);
    }

    [Fact]
    public void Fallback_ToConcept_UsesStandardArray()
    {
        var session = new CharacterCreationSession("test-9");
        session.ProcessInput("My name is Bob. I am an elf mage who studies ancient tomes.");

        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal(StatAllocationMethod.StandardArray, concept.StatMethod);
    }

    [Fact]
    public void Fallback_SingleBlock_BackstoryPreserved()
    {
        var session = new CharacterCreationSession("test-10");
        var input = "My name is Yuric. I am a duck hitman who likes cats and running from oncoming cars.";
        session.ProcessInput(input);

        Assert.True(session.IsComplete);
        var concept = session.ToConcept();
        Assert.Equal(input, concept.Backstory);
    }
}
