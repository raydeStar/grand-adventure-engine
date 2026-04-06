using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

/// <summary>
/// Procedural dungeon generator that creates complete multi-floor dungeons.
/// Pulls items and enemies from the content registry, scaling by player level.
/// When the registry doesn't have enough content for a tier, generates new
/// templates procedurally and registers them for future use.
/// </summary>
public class DungeonGenerator
{
    private static readonly Random Rng = new();

    private readonly IContentRegistryService _registry;
    private readonly ILogger _logger;

    public DungeonGenerator(IContentRegistryService registry, ILogger logger)
    {
        _registry = registry;
        _logger = logger;
    }

    // ── Difficulty Tiers ──────────────────────────────────────────────

    private record DifficultyProfile(
        string Tier, int MinLevel, int MaxLevel,
        string[] DamageDice, int GoldMin, int GoldMax);

    private static readonly DifficultyProfile[] Profiles =
    [
        new("easy",   1, 3,  ["1d4","1d6"],           5,  25),
        new("medium", 3, 6,  ["1d6","1d8","2d4"],    15,  50),
        new("hard",   6, 9,  ["1d10","2d6","2d8"],  40, 150),
        new("deadly", 9, 12, ["2d8","2d10","3d6"], 100, 500),
    ];

    private static DifficultyProfile GetProfile(int playerLevel) => playerLevel switch
    {
        <= 3  => Profiles[0],
        <= 6  => Profiles[1],
        <= 9  => Profiles[2],
        _     => Profiles[3],
    };

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

    // ── Procedural Name Parts (for generating new templates) ────────

    private static readonly string[] MonsterPrefixes =
        ["Shadow", "Cursed", "Feral", "Venomous", "Spectral", "Iron", "Blood", "Frost", "Blighted", "Ashen",
         "Hollow", "Pale", "Rotting", "Crystalline", "Ember", "Thorned", "Bile", "Storm", "Plague", "Shattered"];
    private static readonly string[] MonsterSuffixes =
        ["Stalker", "Lurker", "Mauler", "Revenant", "Brute", "Scourge", "Howler", "Sentinel", "Ravager", "Defiler",
         "Devourer", "Husk", "Wretch", "Reaper", "Abomination", "Shade", "Warden", "Crawler", "Horror", "Harvester"];
    private static readonly string[] BossFirstNames =
        ["Vulgrim", "Nethys", "Skarn", "Mordecai", "Thessaly", "Uldren", "Kragg", "Zanith", "Bael", "Corvax",
         "Drazul", "Ephrath", "Ghorthal", "Hekara", "Izrafil", "Kethis", "Malachar", "Nazgrel", "Ophira", "Rathbone"];
    private static readonly string[] BossTitles =
        ["the Unyielding", "the Dread", "the Hollow", "of the Abyss", "the Corrupted", "the Eternal",
         "Bonecrusher", "Soulbinder", "the Vile", "of Ruin", "the Undying", "the Forsaken",
         "Fleshweaver", "the Eyeless", "of the Black Pit", "Plaguebringer", "the Starved",
         "Oathbreaker", "the Twice-Dead", "of the Sunless Throne"];

    // ── Weapon Name Parts ───────────────────────────────────────────

    private static readonly string[][] TierWeaponNames =
    [
        // Easy — crude, improvised, scavenged
        ["Notched Cleaver", "Bone-Handled Knife", "Bent Iron Poker", "Chipped Stone Axe",
         "Rat-Gnawed Shortsword", "Rusty Butcher's Hook", "Gravedigger's Spade", "Splintered Club",
         "Scavenger's Shiv", "Bandit's Last Blade", "Pitted Bronze Mace", "Goblin-Forged Dagger"],
        // Medium — functional, military, stolen from better owners
        ["Captain's Sabre", "War-Notched Longsword", "Mercenary's Falchion", "Ironwood Quarterstaff",
         "Tomb-Raider's Pick", "Soldier's Warhammer", "Brigand Lord's Rapier", "Scorchmarked Flail",
         "Orc-Killer Broadaxe", "Sentinel's Glaive", "Duelist's Estoc", "Rune-Scratched Morningstar"],
        // Hard — magical, legendary, feared
        ["Whisperwind Katana", "Emberfang Greatsword", "Frostbitten Warblade", "Nightfall Scythe",
         "Thunderstrike Maul", "Serpent's Fang Spear", "Soulthirst Rapier", "Bonebreaker Greataxe",
         "Stormcaller's Staff", "Eclipse Dagger", "Dreadnought Halberd", "Wraithbane Mace"],
        // Deadly — artifacts, relics, things that have names
        ["Worldsplitter", "The Hungering Edge", "Dawnkiller", "Voidreaver Greatsword",
         "Ashen Crown Warhammer", "Godsplinter Lance", "Abyssal Fang", "The Last Argument",
         "Eternity's Thorn", "Hellscream Greataxe", "Oblivion's Kiss", "The Pale Arbiter"],
    ];

    private static readonly string[][] TierWeaponDescs =
    [
        // Easy
        ["The edge is more rust than steel, but it'll still draw blood.",
         "Dried something cakes the handle. Best not to think about it.",
         "Found half-buried in the muck. Someone died holding this.",
         "The balance is terrible, but desperation makes every weapon deadly."],
        // Medium
        ["Well-maintained despite hard use. Someone cared about this weapon.",
         "Runes of a forgotten regiment are stamped into the crossguard.",
         "The blade hums faintly when drawn — residual enchantment, or just the wind.",
         "Battle-scarred but sharp. This weapon has seen real war."],
        // Hard
        ["Pale light runs along the edge like foxfire. The metal is cold to the touch.",
         "The weapon seems to shift its weight to guide your strikes.",
         "Something about it feels alive — patient, hungry, waiting.",
         "Ancient maker's marks glow faintly. This was forged with purpose."],
        // Deadly
        ["Reality bends slightly around the blade. Looking at it too long makes your eyes water.",
         "It whispers. Not words exactly, but the echo of every life it's taken.",
         "The weapon predates the kingdom. Wars were fought just to possess it.",
         "You feel stronger just holding it. That's how it gets you."],
    ];

    // ── Armor Name Parts ────────────────────────────────────────────

    private static readonly string[][] TierArmorNames =
    [
        // Easy
        ["Moth-Eaten Leather Cap", "Dented Tin Buckler", "Stitched Hide Vest", "Scrap-Iron Bracers",
         "Rat-Leather Boots", "Bandit's Padded Jerkin", "Wooden Training Shield", "Salvaged Chainlinks",
         "Gravewatcher's Hood", "Makeshift Pauldron", "Boarskin Gloves", "Tarnished Copper Ring"],
        // Medium
        ["Soldier's Steel Helm", "Militia-Issue Chainmail", "Ironbound Kite Shield", "War-Worn Greaves",
         "Reinforced Leather Cuirass", "Sentinel's Tower Shield", "Tomb Warden's Circlet", "Bronzescale Gauntlets",
         "Ranger's Traveling Cloak", "Mercenary's Half-Plate", "Signet Ring of Rank", "Cavalier's Riding Boots"],
        // Hard
        ["Moonforged Helm", "Spellguard Plate", "Aegis of the Last Watch", "Shadowweave Cloak",
         "Flamewarded Bracers", "Crystalcore Shield", "Stormhide Boots", "Mithril-Laced Gauntlets",
         "Circlet of the Seer", "Dragonbone Cuirass", "Oathkeeper's Ring", "Mantle of Frozen Stars"],
        // Deadly
        ["Crown of the Deathless", "Worldshell Plate", "Bulwark of the Fallen God", "Voidmantle",
         "Gauntlets of the Unchained", "Boots of the Last March", "Aegis of Unmaking", "Ring of Eternal Dusk",
         "The Hollow King's Armor", "Soulward Greatshield", "Circlet of Broken Prophecy", "Cloak of Fading Stars"],
    ];

    private static readonly string[][] TierArmorDescs =
    [
        // Easy
        ["It smells bad and fits worse, but it might stop a blade. Once.",
         "More patches than original material at this point.",
         "You can see daylight through the links, but it's better than nothing.",
         "Someone scratched 'STILL ALIVE' on the inside."],
        // Medium
        ["Standard military issue, maintained by someone who took pride in their gear.",
         "The previous owner's name is etched inside. You try not to think about why they don't need it.",
         "Solid craftsmanship. The kind of armor that gets handed down through families.",
         "Reinforced at the stress points. This was made by someone who understood combat."],
        // Hard
        ["The metal seems to drink in light. Enchantment threads pulse through the seams.",
         "It adjusts to your body as you put it on, like it was made for you.",
         "Ancient protective wards are woven into every fiber. Some still glow.",
         "The craftsmanship is beyond anything modern smiths can replicate."],
        // Deadly
        ["Reality shivers where it meets your skin. You feel less like a person and more like a force of nature.",
         "It remembers every blow it's ever absorbed. Thousands upon thousands.",
         "The gods themselves wore armor like this. Or so the legends claim.",
         "Putting it on feels like a covenant. It will protect you, but it expects something in return."],
    ];

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Generates a complete multi-floor dungeon and saves all rooms.
    /// Returns the entrance room (already saved).
    /// </summary>
    public async Task<Room> GenerateFullDungeonAsync(
        string dungeonId, int playerLevel, Room sourceRoom,
        IStateManager stateManager, CancellationToken ct = default)
    {
        var profile = GetProfile(playerLevel);
        var (floorCount, roomsPerFloor) = GetDungeonSize(playerLevel);

        // Pre-fetch level-appropriate content from the registry
        var tierItems = GetOrGenerateItems(playerLevel, profile);
        var tierMonsters = GetOrGenerateMonsters(playerLevel, profile, isBoss: false);
        var tierBosses = GetOrGenerateMonsters(playerLevel, profile, isBoss: true);
        var tierPotions = GetOrGeneratePotions(playerLevel);

        _logger.LogInformation(
            "Generating dungeon {Id} for level {Level} ({Tier}): {Floors} floors, {Rooms} rooms/floor. " +
            "Registry: {Items} items, {Monsters} monsters, {Bosses} bosses, {Potions} potions available",
            dungeonId, playerLevel, profile.Tier, floorCount, roomsPerFloor,
            tierItems.Count, tierMonsters.Count, tierBosses.Count, tierPotions.Count);

        var allRooms = new List<Room>();
        Room? entrance = null;
        string previousFloorBossId = dungeonId;

        for (int floor = 1; floor <= floorCount; floor++)
        {
            var floorRooms = GenerateFloor(
                dungeonId, floor, floorCount, roomsPerFloor,
                profile, playerLevel, sourceRoom.Id,
                tierItems, tierMonsters, tierBosses, tierPotions);

            if (floor == 1)
            {
                entrance = floorRooms[0];
                entrance.Id = dungeonId;
                entrance.Exits["back"] = sourceRoom.Id;
            }
            else
            {
                var prevBoss = allRooms.Last(r => r.Id == previousFloorBossId);
                var firstRoom = floorRooms[0];
                prevBoss.Exits["down"] = firstRoom.Id;
                firstRoom.Exits["up"] = prevBoss.Id;
            }

            previousFloorBossId = floorRooms[^1].Id;
            allRooms.AddRange(floorRooms);
        }

        foreach (var room in allRooms)
            await stateManager.SaveRoomAsync(room, ct);

        return entrance!;
    }

    // ── Registry Queries with Auto-Generation ───────────────────────

    private List<ItemTemplate> GetOrGenerateItems(int playerLevel, DifficultyProfile profile)
    {
        var weapons = _registry.Items.GetAll()
            .Where(i => i.RequiredLevel >= profile.MinLevel && i.RequiredLevel <= profile.MaxLevel
                     && i.Type == ItemType.Weapon && i.Tags.Contains("dungeon_loot"))
            .ToList();

        var armor = _registry.Items.GetAll()
            .Where(i => i.RequiredLevel >= profile.MinLevel && i.RequiredLevel <= profile.MaxLevel
                     && IsArmorType(i.Type) && i.Tags.Contains("dungeon_loot"))
            .ToList();

        // Ensure at least 3 weapons and 3 armor pieces per tier
        while (weapons.Count < 3)
        {
            var generated = GenerateWeaponTemplate(profile);
            _registry.Items.Register(generated);
            weapons.Add(generated);
            _logger.LogInformation("Generated new weapon template: {Name} (level {Level})", generated.Name, generated.RequiredLevel);
        }
        while (armor.Count < 3)
        {
            var generated = GenerateArmorTemplate(profile);
            _registry.Items.Register(generated);
            armor.Add(generated);
            _logger.LogInformation("Generated new armor template: {Name} (level {Level})", generated.Name, generated.RequiredLevel);
        }

        return [.. weapons, .. armor];
    }

    private List<MonsterTemplate> GetOrGenerateMonsters(int playerLevel, DifficultyProfile profile, bool isBoss)
    {
        var monsters = _registry.Monsters.GetAll()
            .Where(m => m.MinLevel <= playerLevel && m.MaxLevel >= profile.MinLevel && m.IsBoss == isBoss)
            .ToList();

        int minCount = isBoss ? 2 : 4;
        while (monsters.Count < minCount)
        {
            var generated = GenerateMonsterTemplate(profile, isBoss);
            _registry.Monsters.Register(generated);
            monsters.Add(generated);
            _logger.LogInformation("Generated new {Type} template: {Name} (level {Min}-{Max})",
                isBoss ? "boss" : "monster", generated.Name, generated.MinLevel, generated.MaxLevel);
        }

        return monsters;
    }

    private List<ItemTemplate> GetOrGeneratePotions(int playerLevel)
    {
        var potions = _registry.Items.GetAll()
            .Where(i => i.Type == ItemType.Potion && i.RequiredLevel <= playerLevel && i.Tags.Contains("dungeon_loot"))
            .ToList();

        // Always need at least 2 potions available
        if (potions.Count < 2)
        {
            // Fall back to any potion in the registry
            potions = _registry.Items.GetAll()
                .Where(i => i.Type == ItemType.Potion && i.RequiredLevel <= playerLevel)
                .ToList();
        }

        // Still not enough? Generate a basic healing potion
        if (potions.Count == 0)
        {
            var healDice = playerLevel switch
            {
                <= 3 => "1d4+2",
                <= 6 => "2d4+4",
                <= 9 => "4d4+8",
                _ => "8d4+16",
            };
            var potion = new ItemTemplate
            {
                Id = $"gen_heal_potion_lv{playerLevel}_{Rng.Next(1000):000}",
                Name = "Healing Draught",
                Description = "A bubbling red liquid that restores vitality.",
                Type = ItemType.Potion,
                IsConsumable = true,
                Effect = $"heal:{healDice}",
                Value = 10 + playerLevel * 5,
                RequiredLevel = 1,
                Rarity = "common",
                Tags = ["dungeon_loot", "potion", "healing", "generated"],
            };
            _registry.Items.Register(potion);
            potions.Add(potion);
        }

        return potions;
    }

    // ── Template Generators ─────────────────────────────────────────

    private static ItemTemplate GenerateWeaponTemplate(DifficultyProfile profile)
    {
        var tierIndex = Array.IndexOf(Profiles, profile);
        var name = Pick(TierWeaponNames[tierIndex]);
        var desc = Pick(TierWeaponDescs[tierIndex]);
        var dice = Pick(profile.DamageDice);
        var level = Rng.Next(profile.MinLevel, profile.MaxLevel + 1);
        var value = profile.Tier switch
        {
            "easy" => Rng.Next(8, 18),
            "medium" => Rng.Next(30, 55),
            "hard" => Rng.Next(100, 180),
            _ => Rng.Next(350, 550),
        };
        var rarity = profile.Tier switch
        {
            "easy" => "common",
            "medium" => "uncommon",
            "hard" => "rare",
            _ => "epic",
        };

        return new ItemTemplate
        {
            Id = $"gen_wpn_{Guid.NewGuid().ToString("N")[..8]}",
            Name = name,
            Description = desc,
            Type = ItemType.Weapon,
            DamageDice = dice,
            IsEquippable = true,
            Value = value,
            RequiredLevel = level,
            Rarity = rarity,
            Tags = ["dungeon_loot", $"tier{Array.IndexOf(Profiles, profile) + 1}", "generated"],
        };
    }

    private static ItemTemplate GenerateArmorTemplate(DifficultyProfile profile)
    {
        var tierIndex = Array.IndexOf(Profiles, profile);
        var name = Pick(TierArmorNames[tierIndex]);
        var desc = Pick(TierArmorDescs[tierIndex]);
        var level = Rng.Next(profile.MinLevel, profile.MaxLevel + 1);

        var (armorValue, itemType) = profile.Tier switch
        {
            "easy" => (Rng.Next(1, 2), PickArmorType()),
            "medium" => (Rng.Next(2, 4), PickArmorType()),
            "hard" => (Rng.Next(3, 6), PickArmorType()),
            _ => (Rng.Next(5, 8), PickArmorType()),
        };
        var value = profile.Tier switch
        {
            "easy" => Rng.Next(5, 20),
            "medium" => Rng.Next(25, 55),
            "hard" => Rng.Next(80, 150),
            _ => Rng.Next(250, 450),
        };
        var rarity = profile.Tier switch
        {
            "easy" => "common",
            "medium" => "uncommon",
            "hard" => "rare",
            _ => "epic",
        };

        return new ItemTemplate
        {
            Id = $"gen_arm_{Guid.NewGuid().ToString("N")[..8]}",
            Name = name,
            Description = desc,
            Type = itemType,
            ArmorValue = armorValue,
            IsEquippable = true,
            Value = value,
            RequiredLevel = level,
            Rarity = rarity,
            Tags = ["dungeon_loot", $"tier{Array.IndexOf(Profiles, profile) + 1}", "generated"],
        };
    }

    private static MonsterTemplate GenerateMonsterTemplate(DifficultyProfile profile, bool isBoss)
    {
        string name;
        if (isBoss)
        {
            var bossPrefix = Pick(MonsterPrefixes);
            var bossTitle = Pick(BossTitles);
            name = $"{bossPrefix} {bossTitle}";
        }
        else
        {
            var prefix = Pick(MonsterPrefixes);
            var suffix = Pick(MonsterSuffixes);
            name = $"{prefix} {suffix}";
        }

        var tierIdx = Array.IndexOf(Profiles, profile);
        var (baseHp, baseAtk, baseDef) = tierIdx switch
        {
            0 => (Rng.Next(10, 22), Rng.Next(2, 6), Rng.Next(8, 12)),
            1 => (Rng.Next(25, 42), Rng.Next(5, 10), Rng.Next(11, 15)),
            2 => (Rng.Next(45, 70), Rng.Next(9, 15), Rng.Next(14, 18)),
            _ => (Rng.Next(75, 110), Rng.Next(14, 22), Rng.Next(17, 22)),
        };

        if (isBoss)
        {
            baseHp = (int)(baseHp * 1.8);
            baseAtk = (int)(baseAtk * 1.4);
            baseDef += 2;
        }

        var goldRange = tierIdx switch
        {
            0 => (isBoss ? 20 : 2, isBoss ? 80 : 20),
            1 => (isBoss ? 50 : 10, isBoss ? 140 : 50),
            2 => (isBoss ? 100 : 30, isBoss ? 300 : 150),
            _ => (isBoss ? 200 : 80, isBoss ? 700 : 400),
        };

        return new MonsterTemplate
        {
            Id = $"gen_mon_{Guid.NewGuid().ToString("N")[..8]}",
            Name = name,
            Description = isBoss
                ? $"A fearsome {profile.Tier}-tier boss that guards the deepest chambers."
                : $"A dangerous {profile.Tier}-tier creature lurking in the darkness.",
            Personality = isBoss
                ? "Territorial and merciless. Guards the deepest chambers with lethal intent."
                : "Aggressive and feral. Attacks anything that moves.",
            MinLevel = profile.MinLevel,
            MaxLevel = profile.MaxLevel,
            IsBoss = isBoss,
            BaseHp = baseHp,
            BaseAttack = baseAtk,
            BaseDefense = baseDef,
            DamageDice = Pick(profile.DamageDice),
            Rarity = isBoss ? (tierIdx >= 2 ? "epic" : "rare") : (tierIdx >= 2 ? "uncommon" : "common"),
            Tags = [isBoss ? "boss" : "enemy", "generated"],
            GoldMin = goldRange.Item1,
            GoldMax = goldRange.Item2,
        };
    }

    // ── Floor Generation ────────────────────────────────────────────

    private static (int floors, int roomsPerFloor) GetDungeonSize(int level) => level switch
    {
        <= 3  => (1, Rng.Next(5, 8)),
        <= 6  => (2, Rng.Next(4, 7)),
        <= 9  => (3, Rng.Next(4, 7)),
        _     => (4, Rng.Next(4, 7)),
    };

    private List<Room> GenerateFloor(
        string dungeonId, int floor, int totalFloors, int mainRooms,
        DifficultyProfile profile, int playerLevel, string sourceRoomId,
        List<ItemTemplate> tierItems, List<MonsterTemplate> tierMonsters,
        List<MonsterTemplate> tierBosses, List<ItemTemplate> tierPotions)
    {
        var rooms = new List<Room>();

        var roomTypes = AssignRoomTypes(mainRooms, floor == totalFloors);

        for (int i = 0; i < mainRooms; i++)
        {
            var roomId = $"{dungeonId}_f{floor}_r{i + 1}";
            var roomType = roomTypes[i];
            var room = CreateRoom(roomId, roomType, profile, playerLevel, floor,
                tierItems, tierMonsters, tierBosses, tierPotions);
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
            int attachIdx = Rng.Next(1, rooms.Count - 1);
            var branchId = $"{dungeonId}_f{floor}_b{b + 1}";
            var branchType = Rng.NextDouble() < 0.6 ? RoomType.Treasure : RoomType.Combat;
            var branch = CreateRoom(branchId, branchType, profile, playerLevel, floor,
                tierItems, tierMonsters, tierBosses, tierPotions);

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
        types[^1] = RoomType.Boss;
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
        if (!types[1..^1].Contains(RoomType.Combat) && count > 3)
            types[1] = RoomType.Combat;

        return types;
    }

    // ── Room Creation ───────────────────────────────────────────────

    private enum RoomType { Combat, Treasure, Atmosphere, Rest, Boss }

    private Room CreateRoom(string id, RoomType type, DifficultyProfile profile,
        int playerLevel, int floor,
        List<ItemTemplate> tierItems, List<MonsterTemplate> tierMonsters,
        List<MonsterTemplate> tierBosses, List<ItemTemplate> tierPotions)
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
                AddCombatNpcs(room, profile, playerLevel, tierMonsters, isBoss: false);
                AddScatteredLoot(room, profile, tierPotions);
                break;
            case RoomType.Treasure:
                AddTreasureLoot(room, profile, tierItems, tierPotions);
                break;
            case RoomType.Rest:
                room.Items.Add(CreatePotionItem(tierPotions));
                if (Rng.NextDouble() < 0.4) room.Items.Add(CreatePotionItem(tierPotions));
                break;
            case RoomType.Boss:
                AddCombatNpcs(room, profile, playerLevel, tierBosses, isBoss: true);
                // Boss might have a minion
                if (Rng.NextDouble() < 0.5 && tierMonsters.Count > 0)
                {
                    var minion = Pick(tierMonsters.ToArray());
                    var minionNpc = minion.ToNpc(Rng.Next(profile.MinLevel, profile.MaxLevel + 1));
                    minionNpc.LootTable = GenerateEnemyLoot(profile, tierPotions);
                    room.Npcs.Add(minionNpc);
                }
                AddTreasureLoot(room, profile, tierItems, tierPotions);
                break;
            case RoomType.Atmosphere:
                if (Rng.NextDouble() < 0.3)
                    room.Items.Add(CreatePotionItem(tierPotions));
                break;
        }

        return room;
    }

    // ── NPC Generation ──────────────────────────────────────────────

    private static void AddCombatNpcs(Room room, DifficultyProfile profile, int playerLevel,
        List<MonsterTemplate> templatePool, bool isBoss)
    {
        if (templatePool.Count == 0) return;

        if (isBoss)
        {
            var boss = Pick(templatePool.ToArray());
            var bossNpc = boss.ToNpc(Math.Min(playerLevel + 2, profile.MaxLevel + 2), bossScale: true);
            bossNpc.LootTable = []; // Boss loot is in the room itself
            room.Npcs.Add(bossNpc);
        }
        else
        {
            int enemyCount = Rng.Next(1, 4);
            for (int i = 0; i < enemyCount; i++)
            {
                var template = Pick(templatePool.ToArray());
                var level = Rng.Next(profile.MinLevel, profile.MaxLevel + 1);
                var npc = template.ToNpc(level);
                npc.LootTable = GenerateEnemyLoot(profile, []);
                room.Npcs.Add(npc);
            }
        }
    }

    private static List<InventoryItem> GenerateEnemyLoot(DifficultyProfile profile, List<ItemTemplate> potions)
    {
        var loot = new List<InventoryItem>
        {
            new()
            {
                Name = "Gold",
                Type = ItemType.Misc,
                Quantity = Rng.Next(profile.GoldMin, profile.GoldMax + 1),
                Value = 1,
            }
        };

        if (Rng.NextDouble() < 0.3 && potions.Count > 0)
            loot.Add(Pick(potions.ToArray()).ToInventoryItem());

        return loot;
    }

    // ── Item Generation ─────────────────────────────────────────────

    private static void AddScatteredLoot(Room room, DifficultyProfile profile, List<ItemTemplate> potions)
    {
        room.Items.Add(CreatePotionItem(potions));

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

    private static void AddTreasureLoot(Room room, DifficultyProfile profile,
        List<ItemTemplate> tierItems, List<ItemTemplate> potions)
    {
        if (tierItems.Count > 0)
        {
            // Pick a weapon or armor from the registry
            var item = Pick(tierItems.ToArray());
            room.Items.Add(item.ToInventoryItem());
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

        // Potion
        room.Items.Add(CreatePotionItem(potions));
    }

    private static InventoryItem CreatePotionItem(List<ItemTemplate> potions)
    {
        if (potions.Count > 0)
            return Pick(potions.ToArray()).ToInventoryItem();

        // Absolute fallback — should never happen since GetOrGeneratePotions ensures at least one
        return new InventoryItem
        {
            Name = "Minor Healing Potion",
            Description = "Restores a small amount of health.",
            Type = ItemType.Potion,
            Value = 10,
            IsConsumable = true,
            Effect = "heal:1d4+2",
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

    private static bool IsArmorType(ItemType type) => type is
        ItemType.Armor or ItemType.Shield or ItemType.Helmet or
        ItemType.Cloak or ItemType.Boots or ItemType.Gloves;

    private static ItemType PickArmorType()
    {
        ItemType[] types = [ItemType.Armor, ItemType.Shield, ItemType.Helmet, ItemType.Boots, ItemType.Cloak];
        return Pick(types);
    }
}
