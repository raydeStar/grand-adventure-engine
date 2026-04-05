using GAE.Core.Interfaces;
using GAE.Core.Models;

namespace GAE.Engine;

/// <summary>
/// Procedural dungeon generator that creates complete multi-floor dungeons
/// without requiring an LLM. Scales difficulty based on player level.
/// </summary>
public static class DungeonGenerator
{
    private static readonly Random Rng = new();

    // ── Difficulty Tiers ──────────────────────────────────────────────

    private record DifficultyProfile(
        string Tier, int MinLevel, int MaxLevel,
        int MinHp, int MaxHp, int MinAtk, int MaxAtk, int MinDef, int MaxDef,
        string[] DamageDice, int GoldMin, int GoldMax);

    private static readonly DifficultyProfile[] Profiles =
    [
        new("easy",   1, 3,  10, 25, 2, 5,  8, 11, ["1d4","1d6"],           5,  25),
        new("medium", 3, 6,  25, 45, 5, 9, 11, 14, ["1d6","1d8","2d4"],    15,  50),
        new("hard",   6, 9,  45, 75, 9, 14, 14, 17, ["1d10","2d6","2d8"],  40, 150),
        new("deadly", 9, 12, 75,120,14, 20, 17, 22, ["2d8","2d10","3d6"], 100, 500),
    ];

    private static DifficultyProfile GetProfile(int playerLevel) => playerLevel switch
    {
        <= 3  => Profiles[0],
        <= 6  => Profiles[1],
        <= 9  => Profiles[2],
        _     => Profiles[3],
    };

    // ── Enemy Templates ──────────────────────────────────────────────

    private static readonly string[][] EnemyNames =
    [
        // Easy
        ["Giant Rat", "Goblin Scout", "Skeleton Warrior", "Cave Spider", "Zombie Shambler", "Kobold Sneak"],
        // Medium
        ["Dire Wolf", "Bandit Captain", "Animated Armor", "Ghoul", "Orc Berserker", "Cursed Spirit"],
        // Hard
        ["Troll", "Wraith", "Dark Knight", "Stone Golem", "Basilisk", "Shadow Fiend"],
        // Deadly
        ["Young Dragon", "Lich Apprentice", "Demon Captain", "Ancient Guardian", "Hydra", "Death Knight"],
    ];

    private static readonly string[][] BossNames =
    [
        ["Ratking Gnash", "Goblin Warchief Skrag", "The Bone Collector"],
        ["Warden of the Deep", "Ironhide the Cursed", "The Whispering Shade"],
        ["Varkoth the Undying", "Siege Golem Malachar", "The Eyeless Watcher"],
        ["Scourgelord Drakthar", "The Abyssal Maw", "Archlich Veranthos"],
    ];

    // ── Room Name Templates ──────────────────────────────────────────

    private static readonly string[] CombatRoomNames =
    [
        "Crumbling Guard Post", "Bone-Littered Chamber", "The Reeking Hall",
        "Collapsed Barracks", "The Crimson Gallery", "Fungal Cavern",
        "Rusted Gate Room", "The Killing Floor", "Spider's Nest",
        "Damp Tunnel Junction", "The Wailing Pit", "Moss-Covered Vault",
    ];

    private static readonly string[] TreasureRoomNames =
    [
        "Hidden Alcove", "The Glittering Cache", "Forgotten Treasury",
        "Dusty Reliquary", "Lockbox Chamber", "The Hoarder's Nook",
    ];

    private static readonly string[] AtmosphereRoomNames =
    [
        "Echoing Corridor", "The Dripping Passage", "Hall of Whispers",
        "Cracked Mosaic Hall", "Ancient Library Remnant", "The Silent Gallery",
        "Overgrown Tunnel", "Torch-Lit Crossing", "The Breathing Walls",
    ];

    private static readonly string[] RestRoomNames =
    [
        "Abandoned Campsite", "Underground Spring", "Quiet Alcove",
        "The Warm Draft", "Sheltered Nook", "Mossy Grotto",
    ];

    private static readonly string[] BossRoomNames =
    [
        "The Inner Sanctum", "Throne of Bones", "The Final Chamber",
        "Heart of the Depths", "The Crucible", "Lair of the Guardian",
    ];

    // ── Room Description Templates ───────────────────────────────────

    private static readonly string[] CombatDescriptions =
    [
        "The stench of decay fills the air. Claw marks score the walls, and something moves in the shadows ahead.",
        "Broken weapons litter the ground. The sound of heavy breathing echoes from deeper within the chamber.",
        "Dried blood paints the stone floor in sweeping arcs. Whatever did this is still here.",
        "Bones crunch underfoot. The darkness seems to press inward, thick and watchful.",
        "A foul wind pushes through cracks in the wall. Eyes gleam in the far corner.",
    ];

    private static readonly string[] TreasureDescriptions =
    [
        "A gleam catches your eye — something valuable was left behind, or perhaps placed here deliberately.",
        "Dust motes swirl in a thin beam of light. Beneath it, objects glint with promise.",
        "The room is smaller than the others, almost cozy. Shelves line the walls, some still holding their contents.",
    ];

    private static readonly string[] AtmosphereDescriptions =
    [
        "The passage narrows here, forcing you to turn sideways. Strange carvings line the walls, their meaning lost to time.",
        "Water drips from the ceiling in a steady rhythm. The air tastes of copper and old stone.",
        "Faded murals cover every surface — scenes of battles, feasts, and rituals you don't recognize.",
        "The corridor opens into a wider space. Something about the acoustics makes every footstep sound twice.",
    ];

    private static readonly string[] RestDescriptions =
    [
        "A natural spring bubbles up through a crack in the floor. The water is cold but clean, and the air feels lighter here.",
        "The remains of a campfire sit in a ring of stones. Someone rested here before — their charcoal drawings still mark the wall.",
        "A sheltered corner offers a moment of peace. The dungeon's usual menace feels distant, held at bay by thick walls.",
    ];

    private static readonly string[] BossDescriptions =
    [
        "The chamber opens into a vast space. Pillars of carved stone reach toward a ceiling lost in shadow. Something immense waits at the far end.",
        "The air changes — heavier, electric, wrong. The room ahead is clearly the heart of this place, and its guardian knows you're here.",
        "Every surface is scorched or scarred. This room has seen battle before. The thing that won those battles watches you enter.",
    ];

    // ── Loot Tables ─────────────────────────────────────────────────

    private record LootDef(string Name, string Desc, ItemType Type, int Value, string? DamageDice = null, int ArmorValue = 0, bool Consumable = false, string? Effect = null);

    private static readonly LootDef[][] TierWeapons =
    [
        [new("Rusty Shortsword", "Pitted and dull, but functional.", ItemType.Weapon, 10, "1d6"),
         new("Cracked Mace", "Splintered handle, heavy head.", ItemType.Weapon, 12, "1d6")],
        [new("Steel Longsword", "Well-balanced and sharp.", ItemType.Weapon, 35, "1d8+1"),
         new("War Hammer", "Dwarven make, dented but deadly.", ItemType.Weapon, 40, "1d10")],
        [new("Enchanted Blade", "Faint runes pulse along the edge.", ItemType.Weapon, 120, "2d6+2"),
         new("Flamberge of Ash", "The blade smolders with residual heat.", ItemType.Weapon, 150, "2d8")],
        [new("Demonslayer Greatsword", "Forged in hellfire, quenched in holy water.", ItemType.Weapon, 400, "3d8+4"),
         new("Soulreaver", "The blade hums with stolen life force.", ItemType.Weapon, 500, "4d6+3")],
    ];

    private static readonly LootDef[][] TierArmor =
    [
        [new("Leather Cap", "Scuffed but protective.", ItemType.Helmet, 8, ArmorValue: 1),
         new("Padded Vest", "Quilted linen over leather.", ItemType.Armor, 15, ArmorValue: 1)],
        [new("Chainmail Shirt", "Interlocking iron rings.", ItemType.Armor, 50, ArmorValue: 3),
         new("Iron Shield", "Battered but solid.", ItemType.Shield, 30, ArmorValue: 2)],
        [new("Plate Greaves", "Forged steel leg protection.", ItemType.Armor, 100, ArmorValue: 5),
         new("Tower Shield", "Nearly as tall as you.", ItemType.Shield, 90, ArmorValue: 4)],
        [new("Dragonscale Armor", "Scales shimmer with inner fire.", ItemType.Armor, 350, ArmorValue: 7),
         new("Aegis of the Fallen", "A shield that whispers warnings.", ItemType.Shield, 300, ArmorValue: 6)],
    ];

    private static readonly LootDef[] Potions =
    [
        new("Minor Healing Potion", "Restores a small amount of health.", ItemType.Potion, 10, Consumable: true, Effect: "heal:1d4+2"),
        new("Healing Potion", "Warm red liquid that mends wounds.", ItemType.Potion, 25, Consumable: true, Effect: "heal:2d4+4"),
        new("Mana Potion", "Shimmering blue tonic restores magical energy.", ItemType.Potion, 20, Consumable: true, Effect: "mp:1d6+3"),
    ];

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Generates a complete multi-floor dungeon and saves all rooms.
    /// Returns the entrance room (already saved).
    /// </summary>
    public static async Task<Room> GenerateFullDungeonAsync(
        string dungeonId, int playerLevel, Room sourceRoom,
        IStateManager stateManager, CancellationToken ct = default)
    {
        var profile = GetProfile(playerLevel);
        var (floorCount, roomsPerFloor) = GetDungeonSize(playerLevel);
        var allRooms = new List<Room>();

        Room? entrance = null;
        string previousFloorBossId = dungeonId; // entrance connects back here initially

        for (int floor = 1; floor <= floorCount; floor++)
        {
            var floorRooms = GenerateFloor(dungeonId, floor, floorCount, roomsPerFloor, profile, playerLevel, sourceRoom.Id);

            // Wire floor entrance to previous floor's boss room (or dungeon entrance)
            if (floor == 1)
            {
                // First room of first floor IS the dungeon entrance
                entrance = floorRooms[0];
                entrance.Id = dungeonId;
                entrance.Exits["back"] = sourceRoom.Id;
            }
            else
            {
                // Connect previous floor's boss to this floor's first room
                var prevBoss = allRooms.Last(r => r.Id == previousFloorBossId);
                var firstRoom = floorRooms[0];
                prevBoss.Exits["down"] = firstRoom.Id;
                firstRoom.Exits["up"] = prevBoss.Id;
            }

            // Track boss room for next floor connection
            previousFloorBossId = floorRooms[^1].Id;
            allRooms.AddRange(floorRooms);
        }

        // Save all rooms
        foreach (var room in allRooms)
            await stateManager.SaveRoomAsync(room, ct);

        return entrance!;
    }

    // ── Floor Generation ────────────────────────────────────────────

    private static (int floors, int roomsPerFloor) GetDungeonSize(int level) => level switch
    {
        <= 3  => (1, Rng.Next(5, 8)),
        <= 6  => (2, Rng.Next(4, 7)),
        <= 9  => (3, Rng.Next(4, 7)),
        _     => (4, Rng.Next(4, 7)),
    };

    private static List<Room> GenerateFloor(
        string dungeonId, int floor, int totalFloors, int mainRooms,
        DifficultyProfile profile, int playerLevel, string sourceRoomId)
    {
        var rooms = new List<Room>();
        var tierIdx = Array.IndexOf(Profiles, profile);

        // Generate main path rooms
        var roomTypes = AssignRoomTypes(mainRooms, floor == totalFloors);

        for (int i = 0; i < mainRooms; i++)
        {
            var roomId = $"{dungeonId}_f{floor}_r{i + 1}";
            var roomType = roomTypes[i];
            var room = CreateRoom(roomId, roomType, profile, tierIdx, playerLevel, floor);
            rooms.Add(room);
        }

        // Wire main path linearly
        for (int i = 0; i < rooms.Count - 1; i++)
        {
            var dir = PickDirection(i);
            rooms[i].Exits[dir] = rooms[i + 1].Id;
            rooms[i + 1].Exits[OppositeDirection(dir)] = rooms[i].Id;
        }

        // Add 1-2 branch rooms off the main path
        int branchCount = Rng.Next(1, 3);
        for (int b = 0; b < branchCount && rooms.Count > 2; b++)
        {
            int attachIdx = Rng.Next(1, rooms.Count - 1); // not first or last
            var branchId = $"{dungeonId}_f{floor}_b{b + 1}";
            var branchType = Rng.NextDouble() < 0.6 ? RoomType.Treasure : RoomType.Combat;
            var branch = CreateRoom(branchId, branchType, profile, tierIdx, playerLevel, floor);

            var branchDir = PickBranchDirection(rooms[attachIdx]);
            rooms[attachIdx].Exits[branchDir] = branch.Id;
            branch.Exits[OppositeDirection(branchDir)] = rooms[attachIdx].Id;
            rooms.Add(branch);
        }

        return rooms;
    }

    private static RoomType[] AssignRoomTypes(int count, bool isLastFloor)
    {
        var types = new RoomType[count];
        // Last room is always boss
        types[^1] = RoomType.Boss;
        // First room is atmosphere (entrance flavor)
        types[0] = RoomType.Atmosphere;

        for (int i = 1; i < count - 1; i++)
        {
            var roll = Rng.NextDouble();
            types[i] = roll switch
            {
                < 0.45 => RoomType.Combat,
                < 0.60 => RoomType.Treasure,
                < 0.80 => RoomType.Atmosphere,
                _      => RoomType.Rest,
            };
        }
        // Ensure at least one combat room
        if (!types[1..^1].Contains(RoomType.Combat) && count > 3)
            types[1] = RoomType.Combat;

        return types;
    }

    // ── Room Creation ───────────────────────────────────────────────

    private enum RoomType { Combat, Treasure, Atmosphere, Rest, Boss }

    private static Room CreateRoom(string id, RoomType type, DifficultyProfile profile, int tierIdx, int playerLevel, int floor)
    {
        var (name, desc) = type switch
        {
            RoomType.Combat     => (Pick(CombatRoomNames), Pick(CombatDescriptions)),
            RoomType.Treasure   => (Pick(TreasureRoomNames), Pick(TreasureDescriptions)),
            RoomType.Atmosphere => (Pick(AtmosphereRoomNames), Pick(AtmosphereDescriptions)),
            RoomType.Rest       => (Pick(RestRoomNames), Pick(RestDescriptions)),
            RoomType.Boss       => (Pick(BossRoomNames), Pick(BossDescriptions)),
            _                   => ("Dungeon Room", "A dark chamber stretches before you."),
        };

        var room = new Room
        {
            Id = id,
            Name = $"{name} (F{floor})",
            Description = desc,
            EnvironmentTags = ["dungeon", "generated_dungeon", $"difficulty_{profile.Tier}", $"floor_{floor}", type.ToString().ToLowerInvariant()],
            IsDiscovered = true,
            DiscoveredAt = DateTimeOffset.UtcNow,
            Npcs = [],
            Items = [],
            Exits = new Dictionary<string, string>(),
        };

        switch (type)
        {
            case RoomType.Combat:
                AddCombatNpcs(room, profile, tierIdx, playerLevel, isBoss: false);
                AddScatteredLoot(room, profile, tierIdx);
                break;
            case RoomType.Treasure:
                AddTreasureLoot(room, profile, tierIdx);
                break;
            case RoomType.Rest:
                // Add a potion or two
                room.Items.Add(CreatePotion());
                if (Rng.NextDouble() < 0.4) room.Items.Add(CreatePotion());
                break;
            case RoomType.Boss:
                AddCombatNpcs(room, profile, tierIdx, playerLevel, isBoss: true);
                AddTreasureLoot(room, profile, tierIdx); // boss rooms have good loot too
                break;
            case RoomType.Atmosphere:
                // Chance of a minor item
                if (Rng.NextDouble() < 0.3)
                    room.Items.Add(CreatePotion());
                break;
        }

        return room;
    }

    // ── NPC Generation ──────────────────────────────────────────────

    private static void AddCombatNpcs(Room room, DifficultyProfile profile, int tierIdx, int playerLevel, bool isBoss)
    {
        if (isBoss)
        {
            var bossName = Pick(BossNames[tierIdx]);
            room.Npcs.Add(CreateEnemy(bossName, profile, playerLevel, isBoss: true));
            // Boss might have a minion
            if (Rng.NextDouble() < 0.5)
            {
                var minionName = Pick(EnemyNames[tierIdx]);
                room.Npcs.Add(CreateEnemy(minionName, profile, playerLevel, isBoss: false));
            }
        }
        else
        {
            int enemyCount = Rng.Next(1, 4); // 1-3
            for (int i = 0; i < enemyCount; i++)
            {
                var name = Pick(EnemyNames[tierIdx]);
                room.Npcs.Add(CreateEnemy(name, profile, playerLevel, isBoss: false));
            }
        }
    }

    private static Npc CreateEnemy(string name, DifficultyProfile profile, int playerLevel, bool isBoss)
    {
        var hpBase = Rng.Next(profile.MinHp, profile.MaxHp + 1);
        var atkBase = Rng.Next(profile.MinAtk, profile.MaxAtk + 1);
        var defBase = Rng.Next(profile.MinDef, profile.MaxDef + 1);
        var level = Rng.Next(profile.MinLevel, profile.MaxLevel + 1);
        var dice = Pick(profile.DamageDice);

        if (isBoss)
        {
            hpBase = (int)(hpBase * 1.8);
            atkBase = (int)(atkBase * 1.4);
            defBase += 2;
            level = Math.Min(level + 2, profile.MaxLevel + 2);
        }

        return new Npc
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Personality = isBoss
                ? "Territorial and merciless. Guards the deepest chambers with lethal intent."
                : "Aggressive and feral. Attacks anything that moves.",
            IsHostile = true,
            Level = level,
            Hp = hpBase,
            MaxHp = hpBase,
            AttackBonus = atkBase,
            Defense = defBase,
            DamageDice = dice,
            LootTable = GenerateEnemyLoot(profile),
        };
    }

    private static List<InventoryItem> GenerateEnemyLoot(DifficultyProfile profile)
    {
        var loot = new List<InventoryItem>();
        // Gold
        loot.Add(new InventoryItem
        {
            Name = "Gold",
            Type = ItemType.Misc,
            Quantity = Rng.Next(profile.GoldMin, profile.GoldMax + 1),
            Value = 1,
        });
        // Chance of potion drop
        if (Rng.NextDouble() < 0.3)
            loot.Add(CreatePotion());
        return loot;
    }

    // ── Item Generation ─────────────────────────────────────────────

    private static void AddScatteredLoot(Room room, DifficultyProfile profile, int tierIdx)
    {
        // Always a potion
        room.Items.Add(CreatePotion());
        // Chance of gold pile
        if (Rng.NextDouble() < 0.5)
            room.Items.Add(new InventoryItem
            {
                Name = "Gold Coins",
                Description = "A scattered pile of coins.",
                Type = ItemType.Misc,
                Quantity = Rng.Next(profile.GoldMin, profile.GoldMax / 2 + 1),
                Value = 1,
            });
    }

    private static void AddTreasureLoot(Room room, DifficultyProfile profile, int tierIdx)
    {
        // Weapon or armor
        if (Rng.NextDouble() < 0.5)
        {
            var wpn = Pick(TierWeapons[tierIdx]);
            room.Items.Add(new InventoryItem
            {
                Name = wpn.Name, Description = wpn.Desc, Type = wpn.Type,
                Value = wpn.Value, DamageDice = wpn.DamageDice, IsEquippable = true,
            });
        }
        else
        {
            var arm = Pick(TierArmor[tierIdx]);
            room.Items.Add(new InventoryItem
            {
                Name = arm.Name, Description = arm.Desc, Type = arm.Type,
                Value = arm.Value, ArmorValue = arm.ArmorValue, IsEquippable = true,
            });
        }

        // Gold
        room.Items.Add(new InventoryItem
        {
            Name = "Gold Coins",
            Description = "A generous pile of treasure.",
            Type = ItemType.Misc,
            Quantity = Rng.Next(profile.GoldMin * 2, profile.GoldMax + 1),
            Value = 1,
        });

        // Extra potion
        room.Items.Add(CreatePotion());
    }

    private static InventoryItem CreatePotion()
    {
        var p = Pick(Potions);
        return new InventoryItem
        {
            Name = p.Name, Description = p.Desc, Type = p.Type,
            Value = p.Value, IsConsumable = true, Effect = p.Effect,
        };
    }

    // ── Layout Helpers ──────────────────────────────────────────────

    private static readonly string[] MainDirections = ["north", "east", "north", "east", "north"];

    private static string PickDirection(int idx) => MainDirections[idx % MainDirections.Length];

    private static string PickBranchDirection(Room room)
    {
        string[] candidates = ["west", "east", "north", "south"];
        foreach (var d in candidates)
            if (!room.Exits.ContainsKey(d))
                return d;
        return $"passage_{Rng.Next(100)}";
    }

    private static string OppositeDirection(string dir) => dir.ToLowerInvariant() switch
    {
        "north" => "south",
        "south" => "north",
        "east" => "west",
        "west" => "east",
        "up" => "down",
        "down" => "up",
        _ => "back",
    };

    private static T Pick<T>(T[] arr) => arr[Rng.Next(arr.Length)];
}
