using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Engine.Tests;

public class PortalTravelTests
{
    [Fact]
    public async Task EnterPortal_TransfersPlayerToDestinationWorld()
    {
        var state = new InMemoryStateManager();
        var worldRepo = new InMemoryWorldRepository();
        var travel = new RealmTravelService(state, worldRepo, NullLogger<RealmTravelService>.Instance);

        await worldRepo.SaveWorldAsync(new World
        {
            Id = WorldDefaults.DefaultWorldId,
            Name = "Default",
            SpawnRoomId = "spawn",
            Portals =
            [
                new WorldPortal
                {
                    Id = "spawn-to-shadow",
                    SourceWorldId = WorldDefaults.DefaultWorldId,
                    SourceRoomId = "spawn",
                    DestinationWorldId = "shadow"
                }
            ]
        });

        await worldRepo.SaveWorldAsync(new World
        {
            Id = "shadow",
            Name = "Shadow",
            SpawnRoomId = "shadow_spawn"
        });

        await state.SaveRoomAsync(new Room { Id = "spawn", Name = "Spawn", WorldIds = [WorldDefaults.DefaultWorldId] });
        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "hero",
            Name = "Hero",
            ActiveWorldId = WorldDefaults.DefaultWorldId,
            HomeWorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "spawn",
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10
        });

        var narrator = CreateNarratorMock();
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Loose);
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);

        var engine = new GameEngine(
            state,
            dice.Object,
            narrator.Object,
            parser,
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance,
            realmTravelService: travel,
            worldRepository: worldRepo);

        var action = engine.ParseCommand("hero", "enter portal");
        Assert.Equal(ActionType.TravelWorld, action.Type);
        Assert.Equal("portal", action.Parameters.GetValueOrDefault("travelMode"));
        Assert.Null(action.Target);
        var result = await engine.ProcessActionAsync("hero", action);

        Assert.True(result.Success);
        var updated = await state.GetPlayerAsync("hero");
        Assert.NotNull(updated);
        Assert.Equal("shadow", updated!.ActiveWorldId);
        Assert.Equal("shadow_spawn", updated.CurrentRoomId);
    }

    [Fact]
    public async Task EnterPortal_WithMultipleEligiblePortals_ReturnsChoicePrompt()
    {
        var state = new InMemoryStateManager();
        var worldRepo = new InMemoryWorldRepository();
        var travel = new RealmTravelService(state, worldRepo, NullLogger<RealmTravelService>.Instance);

        await worldRepo.SaveWorldAsync(new World
        {
            Id = WorldDefaults.DefaultWorldId,
            Name = "Default",
            SpawnRoomId = "spawn",
            Portals =
            [
                new WorldPortal
                {
                    Id = "p1",
                    SourceWorldId = WorldDefaults.DefaultWorldId,
                    SourceRoomId = "spawn",
                    DestinationWorldId = "shadow"
                },
                new WorldPortal
                {
                    Id = "p2",
                    SourceWorldId = WorldDefaults.DefaultWorldId,
                    SourceRoomId = "spawn",
                    DestinationWorldId = "ironhold"
                }
            ]
        });

        await worldRepo.SaveWorldAsync(new World { Id = "shadow", Name = "Shadow", SpawnRoomId = "shadow_spawn" });
        await worldRepo.SaveWorldAsync(new World { Id = "ironhold", Name = "Ironhold", SpawnRoomId = "iron_spawn" });
        await state.SaveRoomAsync(new Room { Id = "spawn", Name = "Spawn", WorldIds = [WorldDefaults.DefaultWorldId] });
        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "hero",
            Name = "Hero",
            ActiveWorldId = WorldDefaults.DefaultWorldId,
            HomeWorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "spawn",
            Hp = 20,
            MaxHp = 20,
            Mp = 10,
            MaxMp = 10
        });

        var narrator = CreateNarratorMock();
        var dice = new Mock<IProbabilityEngine>(MockBehavior.Loose);
        var parser = new CommandParser(NullLogger<CommandParser>.Instance);

        var engine = new GameEngine(
            state,
            dice.Object,
            narrator.Object,
            parser,
            new GameRulesConfig(),
            NullLogger<GameEngine>.Instance,
            realmTravelService: travel,
            worldRepository: worldRepo);

        var action = engine.ParseCommand("hero", "enter portal");
        var result = await engine.ProcessActionAsync("hero", action);

        Assert.True(!result.Success, result.MechanicalSummary);
        Assert.Contains("Multiple portals", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    private static Mock<INarratorService> CreateNarratorMock()
    {
        var narrator = new Mock<INarratorService>(MockBehavior.Loose);
        narrator
            .Setup(n => n.NarrateActionAsync(It.IsAny<NarratorContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The world bends around you.");
        return narrator;
    }
}
