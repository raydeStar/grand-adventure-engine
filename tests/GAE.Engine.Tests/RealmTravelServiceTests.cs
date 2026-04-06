using GAE.Core.Models;
using GAE.Engine.State;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Logging.Abstractions;

namespace GAE.Engine.Tests;

public class RealmTravelServiceTests
{
    [Fact]
    public async Task TransferPlayerAsync_MovesPlayerAndCreatesSnapshotAndTranslation()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance);

        await worlds.SaveWorldAsync(new World
        {
            Id = WorldDefaults.DefaultWorldId,
            Name = "Default",
            SpawnRoomId = "spawn"
        });
        await worlds.SaveWorldAsync(new World
        {
            Id = "shadow",
            Name = "Shadow Realm",
            SpawnRoomId = "shadow_spawn"
        });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "hero",
            Name = "Hero",
            ActiveWorldId = WorldDefaults.DefaultWorldId,
            HomeWorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "tavern",
            Str = 14,
            Dex = 12,
            Con = 13,
            Int = 11,
            Wis = 10,
            Cha = 9,
            Luck = 8,
            Hp = 20,
            MaxHp = 20,
            Mp = 8,
            MaxMp = 8
        });

        var result = await service.TransferPlayerAsync("hero", "shadow", "unit-test");

        Assert.True(result.Success);
        var player = await state.GetPlayerAsync("hero");
        Assert.NotNull(player);
        Assert.Equal("shadow", player!.ActiveWorldId);
        Assert.Equal("shadow_spawn", player.CurrentRoomId);

        var snapshot = await worlds.GetStatSnapshotAsync("hero", WorldDefaults.DefaultWorldId);
        Assert.NotNull(snapshot);
        Assert.Equal(14, snapshot!.Stats["str"]);

        var history = await worlds.GetTranslationHistoryAsync("hero", WorldDefaults.DefaultWorldId, "shadow");
        Assert.NotNull(history);
    }

    [Fact]
    public async Task TransferPlayerAsync_ReturnToHomeWorld_RestoresHomeSnapshotStats()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance);

        await worlds.SaveWorldAsync(new World
        {
            Id = WorldDefaults.DefaultWorldId,
            Name = "Default",
            SpawnRoomId = "spawn"
        });
        await worlds.SaveWorldAsync(new World
        {
            Id = "arcane",
            Name = "Arcane Grid",
            SpawnRoomId = "arcane_spawn"
        });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "mage",
            Name = "Mage",
            ActiveWorldId = WorldDefaults.DefaultWorldId,
            HomeWorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "library",
            Str = 9,
            Dex = 10,
            Con = 11,
            Int = 17,
            Wis = 15,
            Cha = 12,
            Luck = 8,
            Hp = 16,
            MaxHp = 16,
            Mp = 20,
            MaxMp = 20
        });

        var toArcane = await service.TransferPlayerAsync("mage", "arcane", "unit-test");
        Assert.True(toArcane.Success);

        var away = await state.GetPlayerAsync("mage");
        Assert.NotNull(away);
        away!.Int = 6;
        away.Wis = 6;
        await state.SavePlayerAsync(away);

        var home = await service.TransferPlayerAsync("mage", WorldDefaults.DefaultWorldId, "unit-test");
        Assert.True(home.Success);

        var restored = await state.GetPlayerAsync("mage");
        Assert.NotNull(restored);
        Assert.Equal(WorldDefaults.DefaultWorldId, restored!.ActiveWorldId);
        Assert.Equal(17, restored.Int);
        Assert.Equal(15, restored.Wis);
    }
}
