using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GAE.Integration.Tests;

/// <summary>
/// Tests the state management pipeline: save → retrieve, journal writes,
/// and checkpoint/replay cycle through the real DI container.
/// </summary>
public class StatePipelineTests : IClassFixture<GaeWebApplicationFactory>
{
    private readonly GaeWebApplicationFactory _factory;

    public StatePipelineTests(GaeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SaveAndRetrievePlayer_RoundTrips()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();

        var player = new PlayerCharacter
        {
            Id = "state-test-1",
            Name = "State Tester",
            Race = "Elf",
            Class = "Mage",
            Str = 8, Dex = 14, Con = 10, Int = 16, Wis = 12, Cha = 10,
            MaxHp = 18, Hp = 18, MaxMp = 15, Mp = 15,
            CurrentRoomId = "spawn"
        };

        await state.SavePlayerAsync(player);
        var retrieved = await state.GetPlayerAsync("state-test-1");

        Assert.NotNull(retrieved);
        Assert.Equal("State Tester", retrieved.Name);
        Assert.Equal("Elf", retrieved.Race);
        Assert.Equal(16, retrieved.Int);
        Assert.Equal("spawn", retrieved.CurrentRoomId);
    }

    [Fact]
    public async Task SaveRoom_ThenGetAllRooms_IncludesIt()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();

        var room = new Room
        {
            Id = "state-test-room",
            Name = "Test Chamber",
            Description = "A room created for testing.",
            IsDiscovered = true
        };

        await state.SaveRoomAsync(room);
        var allRooms = await state.GetAllRoomsAsync();

        Assert.Contains(allRooms, r => r.Id == "state-test-room");
    }

    [Fact]
    public async Task AddStoryEntry_ThenRetrieve()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();

        var entry = new StoryEntry
        {
            PlayerId = "state-story-player",
            RoomId = "spawn",
            MechanicalSummary = "Test mechanical output",
            Narration = "The test narrator speaks."
        };

        await state.AddStoryEntryAsync(entry);
        var entries = await state.GetStoryEntriesAsync("state-story-player");

        Assert.Contains(entries, e => e.MechanicalSummary == "Test mechanical output");
    }

    [Fact]
    public async Task CombatState_SaveAndRetrieve()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();

        var combat = new CombatState
        {
            RoomId = "combat-test-room",
            Phase = CombatPhase.PlayerTurn,
            RoundNumber = 1
        };

        await state.SaveCombatStateAsync(combat);
        var retrieved = await state.GetCombatStateAsync("combat-test-room", WorldDefaults.DefaultWorldId);

        Assert.NotNull(retrieved);
        Assert.Equal(CombatPhase.PlayerTurn, retrieved.Phase);
        Assert.Equal(1, retrieved.RoundNumber);

        // Remove and verify
        await state.RemoveCombatStateAsync("combat-test-room", WorldDefaults.DefaultWorldId);
        var removed = await state.GetCombatStateAsync("combat-test-room", WorldDefaults.DefaultWorldId);
        Assert.Null(removed);
    }

    [Fact]
    public async Task PostgreSQL_PersistsState_AcrossScopes()
    {
        var roomId = $"pg-persist-{Guid.NewGuid():N}";

        // Save in one scope
        using (var scope = _factory.Services.CreateScope())
        {
            var state = scope.ServiceProvider.GetRequiredService<IStateManager>();
            await state.SaveRoomAsync(new Room
            {
                Id = roomId,
                Name = "Persistence Test Room",
                Description = "Verifies state survives across scopes via PostgreSQL."
            });
        }

        // Retrieve in a fresh scope — proves data is in the database, not just in memory
        using (var scope = _factory.Services.CreateScope())
        {
            var state = scope.ServiceProvider.GetRequiredService<IStateManager>();
            var retrieved = await state.GetRoomAsync(roomId);

            Assert.NotNull(retrieved);
            Assert.Equal("Persistence Test Room", retrieved.Name);
        }

        // Also verify at the DB level
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<GaeDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var exists = await db.Rooms.AnyAsync(r => r.Id == roomId);
            Assert.True(exists, "Room should exist in PostgreSQL");
        }
    }

    [Fact]
    public async Task SpawnRoom_IsSeeded_OnStartup()
    {
        using var scope = _factory.Services.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IStateManager>();

        var spawn = await state.GetRoomAsync("spawn");

        Assert.NotNull(spawn);
        Assert.False(string.IsNullOrWhiteSpace(spawn.Name));
        Assert.True(spawn.Exits.ContainsKey("east"), "Spawn should have east exit");
        Assert.True(spawn.Exits.ContainsKey("south"), "Spawn should have south exit");
        Assert.True(spawn.Npcs.Count > 0, "Spawn should have at least one NPC");
        Assert.Contains(spawn.Npcs, n => n.Id == "innkeeper_mara");
    }
}
