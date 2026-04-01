using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Engine.Configuration;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

public class GameEngine : IGameEngine
{
    private readonly IStateManager _stateManager;
    private readonly IProbabilityEngine _dice;
    private readonly INarratorService _narrator;
    private readonly CommandParser _parser;
    private readonly GameRulesConfig _rules;
    private readonly ILogger<GameEngine> _logger;

    public GameEngine(
        IStateManager stateManager,
        IProbabilityEngine dice,
        INarratorService narrator,
        CommandParser parser,
        GameRulesConfig rules,
        ILogger<GameEngine> logger)
    {
        _stateManager = stateManager;
        _dice = dice;
        _narrator = narrator;
        _parser = parser;
        _rules = rules;
        _logger = logger;
    }

    public GameAction ParseCommand(string playerId, string rawInput)
        => _parser.Parse(playerId, rawInput);

    public async Task<ActionResult> ProcessActionAsync(string playerId, GameAction action, CancellationToken ct = default)
    {
        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        if (player is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "Player not found." };

        var result = action.Type switch
        {
            ActionType.Move => await ProcessMoveAsync(player, action, ct),
            ActionType.Look => await ProcessLookAsync(player, action, ct),
            ActionType.Attack => await ProcessAttackAsync(player, action, ct),
            ActionType.Talk => await ProcessTalkAsync(player, action, ct),
            ActionType.Take => ProcessTake(player, action),
            ActionType.Drop => ProcessDrop(player, action),
            ActionType.Equip => ProcessEquip(player, action),
            ActionType.Unequip => ProcessUnequip(player, action),
            ActionType.Rest or ActionType.ShortRest => ProcessShortRest(player, action),
            ActionType.LongRest => ProcessLongRest(player, action),
            ActionType.Inventory => ProcessInventory(player, action),
            ActionType.Stats => ProcessStats(player, action),
            ActionType.Help => ProcessHelp(action),
            _ => new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "I don't understand that command." }
        };

        // Save player state after any mutation
        if (result.Success && result.StateChanges.Count > 0)
        {
            player.LastActiveAt = DateTimeOffset.UtcNow;
            await _stateManager.SavePlayerAsync(player, ct);
        }

        // Narrate the result
        if (result.Success && action.Type != ActionType.Help && action.Type != ActionType.Stats && action.Type != ActionType.Inventory)
        {
            try
            {
                var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
                var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, 5, ct);
                var context = new NarratorContext
                {
                    Player = player,
                    CurrentRoom = room ?? new Room { Id = player.CurrentRoomId, Name = "Unknown" },
                    Action = action,
                    MechanicalResult = result,
                    RecentStory = [.. recentStory]
                };
                result.Narration = await _narrator.NarrateActionAsync(context, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Narration failed, using mechanical summary as fallback");
                result.Narration = result.MechanicalSummary;
            }

            // Record story entry
            await _stateManager.AddStoryEntryAsync(new StoryEntry
            {
                ActionId = action.Id,
                PlayerId = playerId,
                RoomId = player.CurrentRoomId,
                MechanicalSummary = result.MechanicalSummary,
                Narration = result.Narration ?? result.MechanicalSummary
            }, ct);
        }

        return result;
    }

    public async Task<PlayerCharacter> CreateCharacterFromConceptAsync(CharacterConcept concept, CancellationToken ct = default)
    {
        var stats = concept.StatMethod switch
        {
            StatAllocationMethod.Roll4d6DropLowest => _dice.RollStatArray(),
            StatAllocationMethod.StandardArray => [.. _rules.CharacterCreation.StandardArray],
            StatAllocationMethod.FlatValue => Enumerable.Repeat(_rules.CharacterCreation.FlatValue, 6).ToArray(),
            StatAllocationMethod.Manual when concept.ManualStats is not null =>
                [concept.ManualStats.GetValueOrDefault("str", 10),
                 concept.ManualStats.GetValueOrDefault("dex", 10),
                 concept.ManualStats.GetValueOrDefault("con", 10),
                 concept.ManualStats.GetValueOrDefault("int", 10),
                 concept.ManualStats.GetValueOrDefault("wis", 10),
                 concept.ManualStats.GetValueOrDefault("cha", 10)],
            _ => [.. _rules.CharacterCreation.StandardArray]
        };

        var player = new PlayerCharacter
        {
            Id = concept.PlayerDiscordId,
            Name = concept.Name,
            Race = concept.Race,
            Class = concept.Class,
            Backstory = concept.Backstory,
            Str = stats[0],
            Dex = stats[1],
            Con = stats[2],
            Int = stats[3],
            Wis = stats[4],
            Cha = stats.Length > 5 ? stats[5] : 10,
            Gold = _rules.CharacterCreation.StartingGold,
            CurrentRoomId = "spawn"
        };

        // Calculate HP/MP from rules
        int hpBase = _rules.Stats.GetValueOrDefault("hp")?.Base ?? 20;
        int mpBase = _rules.Stats.GetValueOrDefault("mp")?.Base ?? 10;
        player.MaxHp = hpBase + player.GetStatModifier(player.Con);
        player.Hp = player.MaxHp;
        player.MaxMp = mpBase + player.GetStatModifier(player.Int);
        player.Mp = player.MaxMp;

        // Generate backstory if narrator is available
        try
        {
            if (string.IsNullOrWhiteSpace(player.Backstory))
                player.Backstory = await _narrator.GenerateBackstoryAsync(concept, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backstory generation failed, using concept backstory");
        }

        await _stateManager.SavePlayerAsync(player, ct);
        _logger.LogInformation("Created character {Name} ({Race} {Class}) for {Id}", player.Name, player.Race, player.Class, player.Id);
        return player;
    }

    public async Task<CombatState?> GetActiveCombatAsync(string roomId, CancellationToken ct = default)
        => await _stateManager.GetCombatStateAsync(roomId, ct);

    // --- Action processors ---

    private async Task<ActionResult> ProcessMoveAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You are in an unknown location." };

        var direction = NormalizeDirection(action.Direction ?? "");
        if (!room.Exits.TryGetValue(direction, out var targetRoomId))
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"There is no exit to the {direction}." };

        var targetRoom = await _stateManager.GetRoomAsync(targetRoomId, ct);
        if (targetRoom is null)
        {
            // Generate new room via narrator
            try
            {
                targetRoom = await _narrator.GenerateRoomAsync(targetRoomId, direction, room, ct);
                targetRoom.IsDiscovered = true;
                targetRoom.DiscoveredAt = DateTimeOffset.UtcNow;
                await _stateManager.SaveRoomAsync(targetRoom, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room generation failed for {RoomId}", targetRoomId);
                targetRoom = new Room
                {
                    Id = targetRoomId,
                    Name = $"Unexplored Area ({targetRoomId})",
                    Description = "A dimly lit area that stretches before you.",
                    IsDiscovered = true,
                    DiscoveredAt = DateTimeOffset.UtcNow,
                    Exits = new Dictionary<string, string> { [OppositeDirection(direction)] = room.Id }
                };
                await _stateManager.SaveRoomAsync(targetRoom, ct);
            }
        }

        var oldRoomId = player.CurrentRoomId;
        player.CurrentRoomId = targetRoomId;

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You move {direction} to {targetRoom.Name}.",
            NewRoom = targetRoom,
            StateChanges = [new StateChange
            {
                EntityType = "Player", EntityId = player.Id,
                Property = "CurrentRoomId", OldValue = oldRoomId, NewValue = targetRoomId
            }]
        };
    }

    private async Task<ActionResult> ProcessLookAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You can't see anything." };

        var exits = string.Join(", ", room.Exits.Keys);
        var npcs = room.Npcs.Count > 0 ? string.Join(", ", room.Npcs.Select(n => n.Name)) : "nobody";
        var items = room.Items.Count > 0 ? string.Join(", ", room.Items.Select(i => i.Name)) : "nothing of interest";

        var summary = $"**{room.Name}**\n{room.Description}\nExits: {exits}\nYou see: {npcs}\nItems: {items}";

        return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = summary };
    }

    private async Task<ActionResult> ProcessAttackAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You can't attack here." };

        var target = room.Npcs.FirstOrDefault(n =>
            n.Name.Contains(action.Target ?? "", StringComparison.OrdinalIgnoreCase));

        if (target is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"There is no '{action.Target}' here to attack." };

        // Determine attack stat
        var weapon = player.Equipment.Weapon;
        string attackStat = weapon?.DamageStat ?? _rules.Combat.MeleeStat;
        int attackMod = player.GetModifier(attackStat);

        var attackRoll = _dice.RollAttack(attackMod);
        int targetDefense = target.Defense ?? _rules.Combat.BaseDefense;

        var result = new ActionResult
        {
            ActionId = action.Id,
            DiceRolls = [attackRoll]
        };

        if (attackRoll.IsFumble)
        {
            result.Success = true;
            result.MechanicalSummary = $"You fumble your attack against {target.Name}! Critical miss!";
            return result;
        }

        if (attackRoll.Total < targetDefense && !attackRoll.IsCritical)
        {
            result.Success = true;
            result.MechanicalSummary = $"You attack {target.Name} (rolled {attackRoll.Total} vs defense {targetDefense}) and miss!";
            return result;
        }

        // Hit — roll damage
        string damageDice = weapon?.DamageDice ?? "1d4";
        int damageMod = player.GetModifier(attackStat);
        var damageRoll = _dice.RollDamage(damageDice, damageMod);

        int totalDamage = attackRoll.IsCritical ? damageRoll.Total * _rules.Combat.CriticalMultiplier : damageRoll.Total;
        totalDamage = Math.Max(1, totalDamage);

        result.DiceRolls.Add(damageRoll);

        if (target.Hp.HasValue)
        {
            target.Hp = Math.Max(0, target.Hp.Value - totalDamage);
            result.StateChanges.Add(new StateChange
            {
                EntityType = "Npc", EntityId = target.Id,
                Property = "Hp", OldValue = (target.Hp.Value + totalDamage).ToString(), NewValue = target.Hp.Value.ToString()
            });
        }

        string critText = attackRoll.IsCritical ? " **CRITICAL HIT!**" : "";
        result.Success = true;
        result.MechanicalSummary = $"You attack {target.Name} (rolled {attackRoll.Total} vs defense {targetDefense}) and deal {totalDamage} damage!{critText}";

        // Check if NPC is dead
        if (target.Hp.HasValue && target.Hp.Value <= 0)
        {
            result.MechanicalSummary += $"\n{target.Name} has been defeated!";

            // Loot drop
            if (_dice.Roll("1d100", "Loot check").Total <= (int)(_rules.Loot.EnemyDropChance * 100))
            {
                var goldDrop = _dice.Roll("1d20", "Gold drop");
                int goldAmount = goldDrop.Total + (target.Level * 2);
                player.Gold += goldAmount;
                result.GoldChange = goldAmount;
                result.MechanicalSummary += $"\nYou find {goldAmount} gold!";
            }

            room.Npcs.Remove(target);
            await _stateManager.SaveRoomAsync(room, ct);
        }

        return result;
    }

    private Task<ActionResult> ProcessTalkAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        // Talk is primarily narration-driven; the engine just validates the target exists
        return Task.FromResult(new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You attempt to speak with {action.Target}."
        });
    }

    private ActionResult ProcessTake(PlayerCharacter player, GameAction action)
    {
        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You reach for {action.Target}."
        };
    }

    private ActionResult ProcessDrop(PlayerCharacter player, GameAction action)
    {
        var item = player.Inventory.FirstOrDefault(i =>
            i.Name.Contains(action.Target ?? "", StringComparison.OrdinalIgnoreCase));

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You don't have '{action.Target}'." };

        player.Inventory.Remove(item);
        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You drop {item.Name}.",
            ItemsLost = [item],
            StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Inventory", NewValue = "removed " + item.Name }]
        };
    }

    private ActionResult ProcessEquip(PlayerCharacter player, GameAction action)
    {
        var item = player.Inventory.FirstOrDefault(i =>
            i.Name.Contains(action.Target ?? "", StringComparison.OrdinalIgnoreCase) && i.IsEquippable);

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You can't equip '{action.Target}'." };

        switch (item.Type)
        {
            case ItemType.Weapon:
                player.Equipment.Weapon = item;
                break;
            case ItemType.Armor:
                player.Equipment.Armor = item;
                break;
            case ItemType.Shield:
                player.Equipment.Shield = item;
                break;
            case ItemType.Helmet:
                player.Equipment.Helmet = item;
                break;
            default:
                return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"{item.Name} is not equippable." };
        }

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You equip {item.Name}.",
            StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Equipment", NewValue = item.Name }]
        };
    }

    private ActionResult ProcessUnequip(PlayerCharacter player, GameAction action)
    {
        var target = action.Target?.ToLowerInvariant() ?? "";
        InventoryItem? item = null;

        if (player.Equipment.Weapon?.Name.Contains(target, StringComparison.OrdinalIgnoreCase) == true)
        {
            item = player.Equipment.Weapon;
            player.Equipment.Weapon = null;
        }
        else if (player.Equipment.Armor?.Name.Contains(target, StringComparison.OrdinalIgnoreCase) == true)
        {
            item = player.Equipment.Armor;
            player.Equipment.Armor = null;
        }
        else if (player.Equipment.Shield?.Name.Contains(target, StringComparison.OrdinalIgnoreCase) == true)
        {
            item = player.Equipment.Shield;
            player.Equipment.Shield = null;
        }
        else if (player.Equipment.Helmet?.Name.Contains(target, StringComparison.OrdinalIgnoreCase) == true)
        {
            item = player.Equipment.Helmet;
            player.Equipment.Helmet = null;
        }

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You don't have '{action.Target}' equipped." };

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You unequip {item.Name}.",
            StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Equipment", NewValue = "removed " + item.Name }]
        };
    }

    private ActionResult ProcessShortRest(PlayerCharacter player, GameAction action)
    {
        var hpRoll = _dice.Roll("1d8", "Short rest HP recovery");
        int hpRecovery = Math.Max(1, hpRoll.Total + player.GetStatModifier(player.Con));
        int oldHp = player.Hp;
        player.Hp = Math.Min(player.MaxHp, player.Hp + hpRecovery);

        var mpRoll = _dice.Roll("1d4", "Short rest MP recovery");
        int mpRecovery = Math.Max(1, mpRoll.Total + player.GetStatModifier(player.Int));
        int oldMp = player.Mp;
        player.Mp = Math.Min(player.MaxMp, player.Mp + mpRecovery);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You take a short rest. Recovered {player.Hp - oldHp} HP and {player.Mp - oldMp} MP.",
            DiceRolls = [hpRoll, mpRoll],
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() },
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString() }
            ]
        };
    }

    private ActionResult ProcessLongRest(PlayerCharacter player, GameAction action)
    {
        int oldHp = player.Hp;
        int oldMp = player.Mp;
        player.Hp = player.MaxHp;
        player.Mp = player.MaxMp;

        // Clear expired status effects
        player.StatusEffects.Clear();

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You take a long rest. Fully restored to {player.MaxHp} HP and {player.MaxMp} MP.",
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() },
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString() }
            ]
        };
    }

    private static ActionResult ProcessInventory(PlayerCharacter player, GameAction action)
    {
        var items = player.Inventory.Count > 0
            ? string.Join("\n", player.Inventory.Select(i => $"  - {i.Name} (x{i.Quantity}) — {i.Description}"))
            : "  Your inventory is empty.";

        var equipped = new List<string>();
        if (player.Equipment.Weapon is not null) equipped.Add($"Weapon: {player.Equipment.Weapon.Name}");
        if (player.Equipment.Armor is not null) equipped.Add($"Armor: {player.Equipment.Armor.Name}");
        if (player.Equipment.Shield is not null) equipped.Add($"Shield: {player.Equipment.Shield.Name}");
        if (player.Equipment.Helmet is not null) equipped.Add($"Helmet: {player.Equipment.Helmet.Name}");
        var equippedStr = equipped.Count > 0 ? string.Join(", ", equipped) : "Nothing equipped";

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"**Inventory** ({player.Gold} gold)\n{items}\n**Equipped:** {equippedStr}"
        };
    }

    private static ActionResult ProcessStats(PlayerCharacter player, GameAction action)
    {
        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"""
                **{player.Name}** — Level {player.Level} {player.Race} {player.Class}
                HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp} | Gold: {player.Gold} | XP: {player.Xp}
                STR: {player.Str} ({player.GetModifier("str"):+0;-0}) | DEX: {player.Dex} ({player.GetModifier("dex"):+0;-0}) | CON: {player.Con} ({player.GetModifier("con"):+0;-0})
                INT: {player.Int} ({player.GetModifier("int"):+0;-0}) | WIS: {player.Wis} ({player.GetModifier("wis"):+0;-0}) | CHA: {player.Cha} ({player.GetModifier("cha"):+0;-0})
                LCK: {player.Luck} ({player.GetModifier("luck"):+0;-0}) | Defense: {player.Defense}
                """
        };
    }

    private static ActionResult ProcessHelp(GameAction action)
    {
        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = """
                **Available Commands:**
                `go <direction>` — Move north/south/east/west/up/down
                `look` / `look at <target>` — Examine surroundings or a specific thing
                `attack <target>` — Attack an NPC
                `talk to <target>` — Talk to an NPC
                `take <item>` — Pick up an item
                `drop <item>` — Drop an item from inventory
                `use <item>` — Use a consumable item
                `equip <item>` — Equip an item
                `unequip <item>` — Unequip an item
                `rest` / `short rest` / `long rest` — Rest and recover
                `inventory` — View your inventory
                `stats` — View your character stats
                `help` — Show this help message
                """
        };
    }

    private static string NormalizeDirection(string dir) => dir.ToLowerInvariant() switch
    {
        "n" => "north",
        "s" => "south",
        "e" => "east",
        "w" => "west",
        "u" => "up",
        "d" => "down",
        _ => dir.ToLowerInvariant()
    };

    private static string OppositeDirection(string dir) => dir switch
    {
        "north" => "south",
        "south" => "north",
        "east" => "west",
        "west" => "east",
        "up" => "down",
        "down" => "up",
        _ => "back"
    };
}
