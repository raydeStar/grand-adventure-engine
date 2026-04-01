using GAE.Core.Models;
using GAE.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Engine.Tests;

public class CommandParserTests
{
    private readonly CommandParser _parser;

    public CommandParserTests()
    {
        _parser = new CommandParser(NullLogger<CommandParser>.Instance);
    }

    [Theory]
    [InlineData("go north", ActionType.Move, "north")]
    [InlineData("move south", ActionType.Move, "south")]
    [InlineData("walk east", ActionType.Move, "east")]
    [InlineData("go n", ActionType.Move, "n")]
    public void Parse_MoveCommands_ReturnsCorrectDirection(string input, ActionType expectedType, string expectedDir)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
        Assert.Equal(expectedDir, action.Direction);
    }

    [Theory]
    [InlineData("look", ActionType.Look)]
    [InlineData("examine", ActionType.Look)]
    [InlineData("l", ActionType.Look)]
    public void Parse_LookCommands_ReturnsLook(string input, ActionType expectedType)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
    }

    [Fact]
    public void Parse_LookAtTarget_SetsTarget()
    {
        var action = _parser.Parse("player1", "look at sword");
        Assert.Equal(ActionType.Look, action.Type);
        Assert.Equal("sword", action.Target);
    }

    [Theory]
    [InlineData("attack goblin", ActionType.Attack, "goblin")]
    [InlineData("hit dragon", ActionType.Attack, "dragon")]
    [InlineData("strike orc", ActionType.Attack, "orc")]
    public void Parse_AttackCommands_ReturnsAttackWithTarget(string input, ActionType expectedType, string expectedTarget)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
        Assert.Equal(expectedTarget, action.Target);
    }

    [Theory]
    [InlineData("talk to innkeeper", ActionType.Talk, "innkeeper")]
    [InlineData("speak with mara", ActionType.Talk, "mara")]
    public void Parse_TalkCommands_ReturnsTalkWithTarget(string input, ActionType expectedType, string expectedTarget)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
        Assert.Equal(expectedTarget, action.Target);
    }

    [Theory]
    [InlineData("rest", ActionType.Rest)]
    [InlineData("short rest", ActionType.ShortRest)]
    [InlineData("long rest", ActionType.LongRest)]
    [InlineData("sleep", ActionType.LongRest)]
    public void Parse_RestCommands_ReturnsCorrectType(string input, ActionType expectedType)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
    }

    [Theory]
    [InlineData("inventory", ActionType.Inventory)]
    [InlineData("inv", ActionType.Inventory)]
    [InlineData("i", ActionType.Inventory)]
    public void Parse_InventoryCommands_ReturnsInventory(string input, ActionType expectedType)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
    }

    [Theory]
    [InlineData("stats", ActionType.Stats)]
    [InlineData("me", ActionType.Stats)]
    [InlineData("character", ActionType.Stats)]
    public void Parse_StatsCommands_ReturnsStats(string input, ActionType expectedType)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
    }

    [Fact]
    public void Parse_UnknownCommand_ReturnsUnknown()
    {
        var action = _parser.Parse("player1", "dance wildly");
        Assert.Equal(ActionType.Unknown, action.Type);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsUnknown()
    {
        var action = _parser.Parse("player1", "");
        Assert.Equal(ActionType.Unknown, action.Type);
    }

    [Fact]
    public void Parse_SetsPlayerIdAndRawInput()
    {
        var action = _parser.Parse("player1", "go north");
        Assert.Equal("player1", action.PlayerId);
        Assert.Equal("go north", action.RawInput);
    }
}
