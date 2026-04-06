using GAE.Core.Models;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Engine.Tests;

public class WorldBootstrapServiceTests
{
    [Fact]
    public async Task EnsureDefaultWorldAsync_CreatesDefaultWorldAndBackfillsPlayers()
    {
        var worldRepository = new InMemoryWorldRepository();
        var stateManager = new InMemoryStateManager();
        var createdAt = new DateTimeOffset(2026, 4, 6, 1, 0, 0, TimeSpan.Zero);
        var lastActiveAt = createdAt.AddHours(3);

        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = "hero",
            Name = "Hero",
            Race = "Human",
            Class = "Warrior",
            ActiveWorldId = string.Empty,
            HomeWorldId = string.Empty,
            CurrentRoomId = "tavern",
            CreatedAt = createdAt,
            LastActiveAt = lastActiveAt,
            QuestLog =
            [
                new QuestProgress
                {
                    QuestId = "rat_problem",
                    WorldId = string.Empty,
                    Status = QuestStatus.Active
                }
            ]
        });

        var service = new WorldBootstrapService(worldRepository, stateManager, NullLogger<WorldBootstrapService>.Instance);

        await service.EnsureDefaultWorldAsync(new GameRulesConfig());

        var world = await worldRepository.GetWorldAsync(WorldDefaults.DefaultWorldId);
        Assert.NotNull(world);
        Assert.Equal(WorldDefaults.DefaultWorldName, world!.Name);
        Assert.Equal(WorldDefaults.DefaultSpawnRoomId, world.SpawnRoomId);
        Assert.Contains("default", world.Tags);

        var updatedPlayer = await stateManager.GetPlayerAsync("hero");
        Assert.NotNull(updatedPlayer);
        Assert.Equal(WorldDefaults.DefaultWorldId, updatedPlayer!.ActiveWorldId);
        Assert.Equal(WorldDefaults.DefaultWorldId, updatedPlayer.HomeWorldId);
        Assert.Equal(WorldDefaults.DefaultWorldId, Assert.Single(updatedPlayer.QuestLog).WorldId);

        var playerWorldState = await worldRepository.GetPlayerWorldStateAsync("hero", WorldDefaults.DefaultWorldId);
        Assert.NotNull(playerWorldState);
        Assert.Equal("tavern", playerWorldState!.CurrentRoomId);
        Assert.True(playerWorldState.HasVisited);
        Assert.Equal(createdAt, playerWorldState.FirstVisitedAt);
        Assert.Equal(lastActiveAt, playerWorldState.LastVisitedAt);
    }

    [Fact]
    public async Task EnsureDefaultWorldAsync_PreservesExistingPlayerWorldState()
    {
        var worldRepository = new InMemoryWorldRepository();
        var stateManager = new InMemoryStateManager();
        await stateManager.SavePlayerAsync(new PlayerCharacter
        {
            Id = "hero",
            Name = "Hero",
            Race = "Human",
            Class = "Warrior",
            ActiveWorldId = WorldDefaults.DefaultWorldId,
            HomeWorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "spawn"
        });

        var existingState = new PlayerWorldState
        {
            PlayerId = "hero",
            WorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "deep-keep",
            HasVisited = true,
            FirstVisitedAt = new DateTimeOffset(2026, 4, 5, 0, 0, 0, TimeSpan.Zero),
            LastVisitedAt = new DateTimeOffset(2026, 4, 6, 0, 0, 0, TimeSpan.Zero)
        };
        await worldRepository.SavePlayerWorldStateAsync(existingState);

        var service = new WorldBootstrapService(worldRepository, stateManager, NullLogger<WorldBootstrapService>.Instance);

        await service.EnsureDefaultWorldAsync(new GameRulesConfig());

        var worldState = await worldRepository.GetPlayerWorldStateAsync("hero", WorldDefaults.DefaultWorldId);
        Assert.NotNull(worldState);
        Assert.Equal(existingState.CurrentRoomId, worldState!.CurrentRoomId);
        Assert.Equal(existingState.FirstVisitedAt, worldState.FirstVisitedAt);
        Assert.Equal(existingState.LastVisitedAt, worldState.LastVisitedAt);
    }
}