using System.Text.Json;
using GAE.Narrator.Prompts;

namespace GAE.Narrator.Tests;

public class BlindAdventurePromptsTests
{
    [Fact]
    public void RoomGenerationPrompts_PreserveHorrorToneWithoutLeakingEngineInternals()
    {
        var systemPrompt = BlindAdventurePrompts.BuildRoomGenerationSystemPrompt();
        var userPrompt = BlindAdventurePrompts.BuildRoomGenerationUserPrompt(
            currentRoomName: "Ashwood Foyer",
            currentRoomDescription: "Dusty chandeliers sway above a checkerboard floor.",
            visibleExits: ["south"],
            environmentTags: ["indoors", "manor", "haunted"],
            direction: "north",
            storylineName: "The Haunting of Ashwood Manor",
            setting: "A crumbling Victorian manor on a fog-shrouded hilltop",
            tone: "Gothic horror with dark humor",
            theme: "Uncovering family secrets",
            plotBeats:
            [
                "The front door locks behind you",
                "The ghost of Lady Ashwood appears and offers a deal"
            ],
            visitedRoomSummaries:
            [
                "Grand foyer with funeral portraits",
                "Narrow servant corridor with clawed wallpaper"
            ],
            nextPlotBeat: "The ghost of Lady Ashwood appears and offers a deal",
            roomsRemaining: 5);

        Assert.Contains("valid JSON", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not include stat blocks", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Gothic horror with dark humor", userPrompt);
        Assert.Contains("The ghost of Lady Ashwood appears and offers a deal", userPrompt);
        Assert.DoesNotContain("room ID", userPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foyer_01", userPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attack values", userPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hit points", userPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoomGenerationFallback_ReturnsParseableJsonForFantasyHeistTone()
    {
        var fallback = BlindAdventurePrompts.BuildRoomGenerationFallback(
            direction: "down",
            storylineName: "The Sunken Crown Heist",
            setting: "A drowned palace vault beneath a bustling canal city",
            tone: "Swashbuckling fantasy caper under constant pressure",
            roomsRemaining: 3);

        using var document = JsonDocument.Parse(fallback);
        var root = document.RootElement;

        Assert.Equal("The Next Uncertain Chamber", root.GetProperty("name").GetString());
        Assert.Contains("Sunken Crown Heist", root.GetProperty("description").GetString());
        Assert.Contains(root.GetProperty("exits").EnumerateArray().Select(element => element.GetString()), exit => exit == "up");
        Assert.Contains(root.GetProperty("exits").EnumerateArray().Select(element => element.GetString()), exit => exit == "forward");
    }

    [Fact]
    public void PlotBeatPrompt_UsesStorylineToneAndFallbackStaysNarrative()
    {
        var systemPrompt = BlindAdventurePrompts.BuildPlotBeatIntegrationSystemPrompt();
        var userPrompt = BlindAdventurePrompts.BuildPlotBeatIntegrationUserPrompt(
            currentRoomName: "Flooded Archive",
            currentSceneSummary: "Canal water laps at collapsed shelves while distant bells echo through stone.",
            storylineName: "The Sunken Crown Heist",
            tone: "Swashbuckling fantasy caper under constant pressure",
            theme: "Trust is harder to steal than treasure",
            plotBeat: "A royal revenant demands the crown be returned, not sold");
        var fallback = BlindAdventurePrompts.BuildPlotBeatFallbackNarration(
            storylineName: "The Sunken Crown Heist",
            tone: "Swashbuckling fantasy caper under constant pressure",
            plotBeat: "A royal revenant demands the crown be returned, not sold");

        Assert.Contains("2-3 sentences", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trust is harder to steal than treasure", userPrompt);
        Assert.Contains("A royal revenant demands the crown be returned, not sold", userPrompt);
        Assert.Contains("Sunken Crown Heist", fallback);
        Assert.DoesNotContain("plot beat", fallback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("system", fallback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdventureConclusionPrompt_ProducesParseableFallbackJson()
    {
        var systemPrompt = BlindAdventurePrompts.BuildAdventureConclusionSystemPrompt();
        var userPrompt = BlindAdventurePrompts.BuildAdventureConclusionUserPrompt(
            storylineName: "The Haunting of Ashwood Manor",
            setting: "A crumbling Victorian manor on a fog-shrouded hilltop",
            tone: "Gothic horror with dark humor",
            theme: "Uncovering family secrets",
            visitedRooms:
            [
                "Grand Foyer",
                "Basement Chapel",
                "Ashwood Attic"
            ],
            keyEvents:
            [
                "Lady Ashwood offered a terrible bargain",
                "The hidden journal exposed the family's crime"
            ]);
        var fallback = BlindAdventurePrompts.BuildAdventureConclusionFallback(
            storylineName: "The Haunting of Ashwood Manor",
            theme: "Uncovering family secrets",
            keyEvents:
            [
                "Lady Ashwood offered a terrible bargain",
                "The hidden journal exposed the family's crime"
            ]);

        Assert.Contains("valid JSON", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Grand Foyer", userPrompt);
        Assert.DoesNotContain("room count", userPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not mention systems", systemPrompt, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(fallback);
        var root = document.RootElement;
        Assert.Contains("Haunting of Ashwood Manor", root.GetProperty("narration").GetString());
        Assert.Contains("uncovering family secrets", root.GetProperty("summary").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}