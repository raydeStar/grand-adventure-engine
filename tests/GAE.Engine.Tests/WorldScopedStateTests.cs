using GAE.Core.Models;
using GAE.Engine.State;

namespace GAE.Engine.Tests;

public class WorldScopedStateTests
{
    [Fact]
    public async Task CombatState_IsolatedByWorld()
    {
        var state = new InMemoryStateManager();
        var roomId = "arena";

        var combat1 = new CombatState { RoomId = roomId, WorldId = "world-a", RoundNumber = 1, Phase = CombatPhase.PlayerTurn };
        var combat2 = new CombatState { RoomId = roomId, WorldId = "world-b", RoundNumber = 5, Phase = CombatPhase.EnemyTurn };

        await state.SaveCombatStateAsync(combat1);
        await state.SaveCombatStateAsync(combat2);

        var fromA = await state.GetCombatStateAsync(roomId, "world-a");
        var fromB = await state.GetCombatStateAsync(roomId, "world-b");
        var fromC = await state.GetCombatStateAsync(roomId, "world-c");

        Assert.NotNull(fromA);
        Assert.Equal(1, fromA!.RoundNumber);
        Assert.NotNull(fromB);
        Assert.Equal(5, fromB!.RoundNumber);
        Assert.Null(fromC);

        // Remove only world-a combat
        await state.RemoveCombatStateAsync(roomId, "world-a");
        Assert.Null(await state.GetCombatStateAsync(roomId, "world-a"));
        Assert.NotNull(await state.GetCombatStateAsync(roomId, "world-b"));
    }

    [Fact]
    public async Task StoryEntries_FilteredByWorld()
    {
        var state = new InMemoryStateManager();
        var roomId = "tavern";

        await state.AddStoryEntryAsync(new StoryEntry
        {
            PlayerId = "hero", RoomId = roomId, WorldId = "world-a",
            Narration = "Ale flows freely.", MechanicalSummary = "look"
        });
        await state.AddStoryEntryAsync(new StoryEntry
        {
            PlayerId = "hero", RoomId = roomId, WorldId = "world-b",
            Narration = "The tavern is eerily silent.", MechanicalSummary = "look"
        });
        await state.AddStoryEntryAsync(new StoryEntry
        {
            PlayerId = "hero", RoomId = roomId, WorldId = "world-a",
            Narration = "A bard begins to play.", MechanicalSummary = "listen"
        });

        var worldA = await state.GetRecentStoryForRoomAsync(roomId, "world-a");
        var worldB = await state.GetRecentStoryForRoomAsync(roomId, "world-b");

        Assert.Equal(2, worldA.Count);
        Assert.Single(worldB);
        Assert.Contains(worldA, e => e.Narration == "A bard begins to play.");
        Assert.Contains(worldB, e => e.Narration == "The tavern is eerily silent.");
    }
}
