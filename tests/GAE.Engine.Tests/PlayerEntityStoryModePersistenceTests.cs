using GAE.Core.Models;
using GAE.Engine.Data;

namespace GAE.Engine.Tests;

public class PlayerEntityStoryModePersistenceTests
{
    [Fact]
    public void PlayerEntity_RoundTripsStoryModeState()
    {
        var player = new PlayerCharacter
        {
            Id = "story-mode-player",
            Name = "Story Mode Player",
            Race = "Human",
            Class = "Rogue",
            CurrentRoomId = "spawn",
            GameMode = GameMode.ChooseYourOwnAdventure,
            Interaction = new InteractionState { Mode = InteractionMode.Cyoa },
            CyoaState = new CyoaState
            {
                Health = CyoaHealthLevel.Hurt,
                CurrentNode = "crossroads",
                PreviousRoomId = "spawn",
                CurrentNarration = "The road waits.",
                CurrentChoices =
                [
                    new CyoaChoice { Id = "left", Text = "Take the left path" },
                    new CyoaChoice { Id = "right", Text = "Take the right path" }
                ],
                ChoiceHistory =
                [
                    new CyoaChoiceRecord { Node = "start", ChoiceText = "Enter the wood" }
                ]
            },
            BlindAdventure = new BlindAdventureSession
            {
                Storyline = new StorylineContext { Id = "haunted-manor", Name = "Haunted Manor", MaxRooms = 3 },
                PreviousRoomId = "spawn",
                RoomsGenerated = 1,
                VisitedRoomIds = ["blind_haunted-manor_start"]
            }
        };

        var roundTripped = PlayerEntity.FromDomain(player).ToDomain();

        Assert.Equal(GameMode.ChooseYourOwnAdventure, roundTripped.GameMode);
        Assert.Equal(InteractionMode.Cyoa, roundTripped.Interaction.Mode);
        Assert.NotNull(roundTripped.CyoaState);
        Assert.Equal("crossroads", roundTripped.CyoaState.CurrentNode);
        Assert.Equal(CyoaHealthLevel.Hurt, roundTripped.CyoaState.Health);
        Assert.Equal("Take the left path", roundTripped.CyoaState.CurrentChoices[0].Text);
        Assert.Single(roundTripped.CyoaState.ChoiceHistory);
        Assert.NotNull(roundTripped.BlindAdventure);
        Assert.Equal("haunted-manor", roundTripped.BlindAdventure.Storyline.Id);
        Assert.Equal("spawn", roundTripped.BlindAdventure.PreviousRoomId);
    }
}
