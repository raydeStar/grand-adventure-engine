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
    [InlineData("go n", ActionType.Move, "north")]
    [InlineData("south", ActionType.Move, "south")]
    [InlineData("n", ActionType.Move, "north")]
    [InlineData("e", ActionType.Move, "east")]
    [InlineData("w", ActionType.Move, "west")]
    public void Parse_MoveCommands_ReturnsCorrectDirection(string input, ActionType expectedType, string expectedDir)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(expectedType, action.Type);
        Assert.Equal(expectedDir, action.Direction);
    }

    [Theory]
    [InlineData("travel to world default-world", "default-world")]
    [InlineData("travel world shadow_market", "shadow_market")]
    [InlineData("realm jump ironhold", "ironhold")]
    public void Parse_TravelWorldCommands_ReturnsTravelWorld(string input, string expectedWorld)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.TravelWorld, action.Type);
        Assert.Equal(expectedWorld, action.Target);
    }

    [Theory]
    [InlineData("enter portal")]
    [InlineData("use the portal")]
    [InlineData("step through gate")]
    public void Parse_PortalTravelCommands_ReturnsTravelWorldWithPortalMode(string input)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.TravelWorld, action.Type);
        Assert.True(action.Parameters.TryGetValue("travelMode", out var mode));
        Assert.Equal("portal", mode);
        Assert.Null(action.Target);
    }

    [Fact]
    public void Parse_PortalTravelWithDestination_SetsTarget()
    {
        var action = _parser.Parse("player1", "enter portal to shadow");
        Assert.Equal(ActionType.TravelWorld, action.Type);
        Assert.Equal("shadow", action.Target);
        Assert.Equal("portal", action.Parameters.GetValueOrDefault("travelMode"));
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
    [InlineData("look around")]
    [InlineData("look at room")]
    [InlineData("search")]
    [InlineData("search room")]
    [InlineData("examine room")]
    public void Parse_RoomLookAliases_ReturnLookWithoutTarget(string input)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.Look, action.Type);
        Assert.Null(action.Target);
    }

    [Fact]
    public void Parse_SearchTarget_SetsTarget()
    {
        var action = _parser.Parse("player1", "search for sentinel");
        Assert.Equal(ActionType.Look, action.Type);
        Assert.Equal("sentinel", action.Target);
    }

    [Fact]
    public void Parse_ThrowOnGround_ReturnsDropWithTarget()
    {
        var action = _parser.Parse("player1", "throw 1 gold on the ground");
        Assert.Equal(ActionType.Drop, action.Type);
        Assert.Equal("1 gold", action.Target);
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

    [Theory]
    [InlineData("journal")]
    [InlineData("quests")]
    [InlineData("quest log")]
    [InlineData("my quests")]
    [InlineData("quest journal")]
    [InlineData("log")]
    public void Parse_JournalCommands_ReturnsJournal(string input)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.Journal, action.Type);
    }

    [Theory]
    [InlineData("quest rat problem", "rat problem")]
    [InlineData("quest info the lost sword", "the lost sword")]
    [InlineData("check quest brams patrol", "brams patrol")]
    public void Parse_QuestInfoCommands_ReturnsQuestInfoWithTarget(string input, string expectedTarget)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.QuestInfo, action.Type);
        Assert.Equal(expectedTarget, action.Target);
    }

    [Theory]
    [InlineData("accept rat problem", "rat problem")]
    [InlineData("take quest the lost sword", "the lost sword")]
    [InlineData("accept quest brams patrol", "brams patrol")]
    public void Parse_AcceptQuestCommands_ReturnsAcceptQuestWithTarget(string input, string expectedTarget)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.AcceptQuest, action.Type);
        Assert.Equal(expectedTarget, action.Target);
    }

    [Theory]
    [InlineData("abandon rat problem", "rat problem")]
    [InlineData("drop quest the lost sword", "the lost sword")]
    [InlineData("cancel quest brams patrol", "brams patrol")]
    public void Parse_AbandonQuestCommands_ReturnsAbandonQuestWithTarget(string input, string expectedTarget)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.AbandonQuest, action.Type);
        Assert.Equal(expectedTarget, action.Target);
    }

    [Theory]
    [InlineData("adventure start haunted-manor", "haunted-manor")]
    [InlineData("blind start dark_forest", "dark_forest")]
    [InlineData("ADVENTURE START my-story-01", "my-story-01")]
    public void Parse_AdventureStart_ReturnsAdventureStartWithId(string input, string expectedId)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.AdventureStart, action.Type);
        Assert.Equal(expectedId, action.Target);
    }

    [Theory]
    [InlineData("adventure end")]
    [InlineData("blind end")]
    [InlineData("adventure stop")]
    [InlineData("adventure quit")]
    [InlineData("blind finish")]
    public void Parse_AdventureEnd_ReturnsAdventureEnd(string input)
    {
        var action = _parser.Parse("player1", input);
        Assert.Equal(ActionType.AdventureEnd, action.Type);
    }
}
