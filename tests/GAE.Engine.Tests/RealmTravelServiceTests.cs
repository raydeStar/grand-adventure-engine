using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Configuration;
using GAE.Engine.State;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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

    [Fact]
    public async Task TransferPlayerAsync_ReturnHome_PreservesHigherLevelAndXp()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var rules = new GameRulesConfig
        {
            Stats = new Dictionary<string, StatConfig>
            {
                ["str"] = new() { Max = 20 },
                ["hp"] = new() { Base = 20 },
                ["mp"] = new() { Base = 10 }
            }
        };
        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance, rules);

        await worlds.SaveWorldAsync(new World
        {
            Id = WorldDefaults.DefaultWorldId,
            Name = "Default",
            SpawnRoomId = "spawn"
        });
        await worlds.SaveWorldAsync(new World
        {
            Id = "void",
            Name = "Void",
            SpawnRoomId = "void_spawn"
        });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "veteran",
            Name = "Veteran",
            ActiveWorldId = WorldDefaults.DefaultWorldId,
            HomeWorldId = WorldDefaults.DefaultWorldId,
            CurrentRoomId = "camp",
            Level = 3,
            Xp = 220,
            Str = 14,
            Dex = 12,
            Con = 13,
            Int = 11,
            Wis = 10,
            Cha = 9,
            Luck = 8,
            Hp = 22,
            MaxHp = 22,
            Mp = 11,
            MaxMp = 11
        });

        var toVoid = await service.TransferPlayerAsync("veteran", "void", "unit-test");
        Assert.True(toVoid.Success);

        var inVoid = await state.GetPlayerAsync("veteran");
        Assert.NotNull(inVoid);
        inVoid!.Level = 5;
        inVoid.Xp = 500;
        inVoid.Int = 6;
        inVoid.Wis = 6;
        await state.SavePlayerAsync(inVoid);

        var backHome = await service.TransferPlayerAsync("veteran", WorldDefaults.DefaultWorldId, "unit-test");
        Assert.True(backHome.Success);

        var restored = await state.GetPlayerAsync("veteran");
        Assert.NotNull(restored);
        Assert.Equal(WorldDefaults.DefaultWorldId, restored!.ActiveWorldId);
        Assert.Equal(5, restored.Level);
        Assert.Equal(500, restored.Xp);
        Assert.Equal(11, restored.Int); // home snapshot restored
        Assert.Equal(10, restored.Wis); // home snapshot restored
    }

    [Fact]
    public async Task TransferPlayerAsync_UsesAiTranslation_WhenNarratorAvailable()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var narrator = new Mock<INarratorService>();

        narrator.Setup(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StatTranslationResponse
            {
                TranslatedStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["str"] = 16, ["dex"] = 14, ["con"] = 15,
                    ["int"] = 8,  ["wis"] = 7,  ["cha"] = 6, ["luck"] = 10
                },
                TranslationNotes = "Strength-focused translation for combat world.",
                Narrative = "Your muscles swell as the shadow realm reshapes you."
            });

        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance,
            narrator: narrator.Object);

        await worlds.SaveWorldAsync(new World { Id = "home", Name = "Home", SpawnRoomId = "spawn" });
        await worlds.SaveWorldAsync(new World { Id = "combat", Name = "Combat Realm", SpawnRoomId = "arena" });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Warrior", ActiveWorldId = "home", HomeWorldId = "home",
            CurrentRoomId = "tavern",
            Str = 14, Dex = 12, Con = 13, Int = 11, Wis = 10, Cha = 9, Luck = 8,
            Hp = 20, MaxHp = 20, Mp = 8, MaxMp = 8
        });

        var result = await service.TransferPlayerAsync("p1", "combat", "test");

        Assert.True(result.Success);
        Assert.Contains("Strength-focused", result.MechanicalSummary);
        Assert.Equal("Your muscles swell as the shadow realm reshapes you.", result.Narration);

        var player = await state.GetPlayerAsync("p1");
        Assert.Equal(16, player!.Str);
        Assert.Equal(14, player.Dex);
        Assert.Equal(8, player.Int);

        narrator.Verify(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransferPlayerAsync_FallsToDeterministicTranslation_WhenNarratorReturnsNull()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var narrator = new Mock<INarratorService>();

        narrator.Setup(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StatTranslationResponse?)null);
        narrator.Setup(n => n.NarrateRealmTransitionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("A portal shimmers open.");

        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance,
            narrator: narrator.Object);

        await worlds.SaveWorldAsync(new World { Id = "home", Name = "Home", SpawnRoomId = "spawn" });
        await worlds.SaveWorldAsync(new World { Id = "other", Name = "Other", SpawnRoomId = "other_spawn" });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Mage", ActiveWorldId = "home", HomeWorldId = "home",
            CurrentRoomId = "library",
            Str = 9, Dex = 10, Con = 11, Int = 17, Wis = 15, Cha = 12, Luck = 8,
            Hp = 16, MaxHp = 16, Mp = 20, MaxMp = 20
        });

        var result = await service.TransferPlayerAsync("p1", "other", "test");

        Assert.True(result.Success);
        Assert.Contains("Deterministic", result.MechanicalSummary);
        // Fallback narration should be generated via NarrateRealmTransitionAsync
        Assert.Equal("A portal shimmers open.", result.Narration);
    }

    [Fact]
    public async Task CacheInvalidation_LevelChange_TriggersReTranslation()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var narrator = new Mock<INarratorService>();

        int callCount = 0;
        narrator.Setup(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new StatTranslationResponse
                {
                    TranslatedStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["str"] = 14 + callCount, ["dex"] = 12, ["con"] = 13,
                        ["int"] = 11, ["wis"] = 10, ["cha"] = 9, ["luck"] = 8
                    },
                    TranslationNotes = $"Translation #{callCount}.",
                    Narrative = $"Transition #{callCount}."
                };
            });

        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance,
            narrator: narrator.Object);

        await worlds.SaveWorldAsync(new World { Id = "home", Name = "Home", SpawnRoomId = "spawn" });
        await worlds.SaveWorldAsync(new World { Id = "shadow", Name = "Shadow", SpawnRoomId = "s_spawn" });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Hero", ActiveWorldId = "home", HomeWorldId = "home",
            CurrentRoomId = "tavern", Level = 3,
            Str = 14, Dex = 12, Con = 13, Int = 11, Wis = 10, Cha = 9, Luck = 8,
            Hp = 20, MaxHp = 20, Mp = 8, MaxMp = 8
        });

        // First transfer: should call AI
        var r1 = await service.TransferPlayerAsync("p1", "shadow", "test");
        Assert.True(r1.Success);
        Assert.Equal(1, callCount);

        // Return home
        var home = await service.TransferPlayerAsync("p1", "home", "test");
        Assert.True(home.Success);

        // Level up the player at home
        var player = await state.GetPlayerAsync("p1");
        player!.Level = 5;
        player.Str = 16; // Also changed stats
        await state.SavePlayerAsync(player);

        // Transfer again: cache should be invalid due to level change, triggers re-translation
        var r2 = await service.TransferPlayerAsync("p1", "shadow", "test");
        Assert.True(r2.Success);
        Assert.Equal(2, callCount); // AI called again
        Assert.Contains("Translation #2", r2.MechanicalSummary);
    }

    [Fact]
    public async Task CacheValid_SameLevelAndStats_UsedCachedTranslation()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var narrator = new Mock<INarratorService>();

        int callCount = 0;
        narrator.Setup(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return new StatTranslationResponse
                {
                    TranslatedStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["str"] = 14, ["dex"] = 12, ["con"] = 13,
                        ["int"] = 11, ["wis"] = 10, ["cha"] = 9, ["luck"] = 8
                    },
                    TranslationNotes = "Cached translation.",
                    Narrative = "The realm shifts."
                };
            });

        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance,
            narrator: narrator.Object);

        await worlds.SaveWorldAsync(new World { Id = "home", Name = "Home", SpawnRoomId = "spawn" });
        await worlds.SaveWorldAsync(new World { Id = "shadow", Name = "Shadow", SpawnRoomId = "s_spawn" });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Hero", ActiveWorldId = "home", HomeWorldId = "home",
            CurrentRoomId = "tavern", Level = 3,
            Str = 14, Dex = 12, Con = 13, Int = 11, Wis = 10, Cha = 9, Luck = 8,
            Hp = 20, MaxHp = 20, Mp = 8, MaxMp = 8
        });

        // First transfer
        await service.TransferPlayerAsync("p1", "shadow", "test");
        Assert.Equal(1, callCount);

        // Return home without changing anything
        await service.TransferPlayerAsync("p1", "home", "test");

        // Transfer again with same stats/level — should use cache
        var result = await service.TransferPlayerAsync("p1", "shadow", "test");
        Assert.True(result.Success);
        Assert.Equal(1, callCount); // AI NOT called again
        Assert.Contains("cached", result.MechanicalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnHome_AfterLevelUp_ReTranslatesViaAi()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var narrator = new Mock<INarratorService>();

        narrator.Setup(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StatTranslationResponse
            {
                TranslatedStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["str"] = 15, ["dex"] = 13, ["con"] = 14,
                    ["int"] = 12, ["wis"] = 11, ["cha"] = 10, ["luck"] = 9
                },
                TranslationNotes = "Re-translated for growth.",
                Narrative = "You return home changed."
            });

        var rules = new GameRulesConfig
        {
            Stats = new Dictionary<string, StatConfig>
            {
                ["str"] = new() { Max = 20 },
                ["hp"] = new() { Base = 20 },
                ["mp"] = new() { Base = 10 }
            }
        };
        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance,
            rules: rules, narrator: narrator.Object);

        await worlds.SaveWorldAsync(new World { Id = "home", Name = "Home", SpawnRoomId = "spawn" });
        await worlds.SaveWorldAsync(new World { Id = "void", Name = "Void", SpawnRoomId = "v_spawn" });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Hero", ActiveWorldId = "home", HomeWorldId = "home",
            CurrentRoomId = "camp", Level = 3,
            Str = 14, Dex = 12, Con = 13, Int = 11, Wis = 10, Cha = 9, Luck = 8,
            Hp = 22, MaxHp = 22, Mp = 11, MaxMp = 11
        });

        // Go to void
        await service.TransferPlayerAsync("p1", "void", "test");

        // Level up while in void
        var inVoid = await state.GetPlayerAsync("p1");
        inVoid!.Level = 5;
        inVoid.Xp = 500;
        await state.SavePlayerAsync(inVoid);

        // Return home: should trigger re-translation since level > snapshot level
        var result = await service.TransferPlayerAsync("p1", "home", "test");
        Assert.True(result.Success);
        Assert.Contains("grown stronger", result.MechanicalSummary);

        var restored = await state.GetPlayerAsync("p1");
        Assert.Equal(5, restored!.Level); // Higher level preserved
        Assert.Equal(500, restored.Xp);   // XP preserved
    }

    [Fact]
    public async Task DeterministicTranslation_SemanticTagMapping_ScalesCorrectly()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();

        // Source world: standard fantasy stats
        var sourceRules = new GameRulesConfig
        {
            Stats = new Dictionary<string, StatConfig>
            {
                ["str"] = new() { Display = "Strength", Min = 1, Max = 20, SemanticTags = ["physical_power", "melee"] },
                ["int"] = new() { Display = "Intelligence", Min = 1, Max = 20, SemanticTags = ["mental_power", "magic"] },
                ["hp"] = new() { Base = 20, Category = "resource" },
                ["mp"] = new() { Base = 10, Category = "resource" }
            }
        };

        // Destination world: sci-fi stats with different ranges but overlapping tags
        var destRules = new GameRulesConfig
        {
            Stats = new Dictionary<string, StatConfig>
            {
                ["might"] = new() { Display = "Might", Min = 1, Max = 100, SemanticTags = ["physical_power", "combat"] },
                ["tech"] = new() { Display = "Tech Skill", Min = 1, Max = 100, SemanticTags = ["mental_power", "hacking"] },
                ["hp"] = new() { Base = 50, Category = "resource" },
                ["mp"] = new() { Base = 25, Category = "resource" }
            }
        };

        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance, rules: sourceRules);

        await worlds.SaveWorldAsync(new World { Id = "fantasy", Name = "Fantasy", SpawnRoomId = "spawn", Rules = sourceRules });
        await worlds.SaveWorldAsync(new World { Id = "scifi", Name = "Sci-Fi", SpawnRoomId = "s_spawn", Rules = destRules });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Hero", ActiveWorldId = "fantasy", HomeWorldId = "fantasy",
            CurrentRoomId = "tavern",
            Str = 15, Dex = 12, Con = 13, Int = 18, Wis = 10, Cha = 9, Luck = 8,
            Hp = 20, MaxHp = 20, Mp = 8, MaxMp = 8
        });

        var result = await service.TransferPlayerAsync("p1", "scifi", "test");
        Assert.True(result.Success);

        var player = await state.GetPlayerAsync("p1");
        Assert.NotNull(player);

        // Destination uses non-standard stat IDs ("might", "tech") — ApplyStats only applies
        // standard stat names, so the player's core stats won't change. But the translation
        // history should contain the mapped values with proper scaling.
        var history = await worlds.GetTranslationHistoryAsync("p1", "fantasy", "scifi");
        Assert.NotNull(history);
        Assert.Contains("Deterministic", result.MechanicalSummary);

        // Str 15/20 → 73.7% of [1,100] → ~74 for "might" (via "physical_power" tag overlap)
        Assert.True(history!.TranslatedStats.ContainsKey("might"));
        Assert.InRange(history.TranslatedStats["might"], 70, 80);

        // Int 18/20 → 89.5% of [1,100] → ~90 for "tech" (via "mental_power" tag overlap)
        Assert.True(history.TranslatedStats.ContainsKey("tech"));
        Assert.InRange(history.TranslatedStats["tech"], 85, 95);
    }

    [Fact]
    public async Task TransitionNarration_GeneratedAsFallback_WhenAiTranslationHasNone()
    {
        var state = new InMemoryStateManager();
        var worlds = new InMemoryWorldRepository();
        var narrator = new Mock<INarratorService>();

        // AI translation succeeds but with empty narrative
        narrator.Setup(n => n.TranslateStatsAsync(It.IsAny<StatTranslationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StatTranslationResponse
            {
                TranslatedStats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["str"] = 14, ["dex"] = 12, ["con"] = 13,
                    ["int"] = 11, ["wis"] = 10, ["cha"] = 9, ["luck"] = 8
                },
                TranslationNotes = "Identity mapping.",
                Narrative = "" // Empty!
            });

        // Fallback narration call should be made
        narrator.Setup(n => n.NarrateRealmTransitionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("The world dissolves around you.");

        var service = new RealmTravelService(state, worlds, NullLogger<RealmTravelService>.Instance,
            narrator: narrator.Object);

        await worlds.SaveWorldAsync(new World { Id = "home", Name = "Home", SpawnRoomId = "spawn" });
        await worlds.SaveWorldAsync(new World { Id = "shadow", Name = "Shadow", SpawnRoomId = "s_spawn" });

        await state.SavePlayerAsync(new PlayerCharacter
        {
            Id = "p1", Name = "Hero", ActiveWorldId = "home", HomeWorldId = "home",
            CurrentRoomId = "tavern",
            Str = 14, Dex = 12, Con = 13, Int = 11, Wis = 10, Cha = 9, Luck = 8,
            Hp = 20, MaxHp = 20, Mp = 8, MaxMp = 8
        });

        var result = await service.TransferPlayerAsync("p1", "shadow", "test");

        Assert.True(result.Success);
        Assert.Equal("The world dissolves around you.", result.Narration);
        narrator.Verify(n => n.NarrateRealmTransitionAsync(
            "Hero", "Home", "Shadow", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
