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

        // NLP fallback: when regex parsing fails, ask the narrator to interpret natural language
        if (action.Type == ActionType.Unknown)
        {
            _logger.LogInformation("Unrecognized command for {PlayerId}: {RawInput}. Attempting intent translation before free-form handling.", playerId, action.RawInput);
            var translated = await _narrator.ParseIntentAsync(action.RawInput, ct);
            if (translated is not null)
            {
                var reparsed = _parser.Parse(playerId, translated);
                if (reparsed.Type != ActionType.Unknown)
                {
                    _logger.LogInformation("Intent translation mapped \"{RawInput}\" to canonical command \"{Translated}\".", action.RawInput, translated);
                    action.Type = reparsed.Type;
                    action.Target = reparsed.Target;
                    action.Direction = reparsed.Direction;
                }
            }
        }

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
            _ => await ProcessFreeFormActionAsync(player, action, ct)
        };

        // Save player state after any mutation
        if (result.Success && result.StateChanges.Count > 0)
        {
            player.LastActiveAt = DateTimeOffset.UtcNow;
            await _stateManager.SavePlayerAsync(player, ct);
        }

        // Narrate the result (free-form actions handle their own narration and story)
        if (result.Success && action.Type != ActionType.Help && action.Type != ActionType.Stats && action.Type != ActionType.Inventory && action.Type != ActionType.Unknown)
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

        return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = string.Empty };
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
                if (player.Equipment.Weapon is not null)
                    player.Inventory.Add(player.Equipment.Weapon);
                player.Equipment.Weapon = item;
                break;
            case ItemType.Armor:
                if (player.Equipment.Armor is not null)
                    player.Inventory.Add(player.Equipment.Armor);
                player.Equipment.Armor = item;
                break;
            case ItemType.Shield:
                if (player.Equipment.Shield is not null)
                    player.Inventory.Add(player.Equipment.Shield);
                player.Equipment.Shield = item;
                break;
            case ItemType.Helmet:
                if (player.Equipment.Helmet is not null)
                    player.Inventory.Add(player.Equipment.Helmet);
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

    private async Task<ActionResult> ProcessFreeFormActionAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        _logger.LogInformation("Routing free-form action for {PlayerId} in room {RoomId}: {RawInput}", player.Id, player.CurrentRoomId, action.RawInput);

        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        room ??= new Room { Id = player.CurrentRoomId, Name = "Unknown" };

        var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, 5, ct);
        var freeForm = await _narrator.ProcessFreeFormAsync(player, room, action.RawInput, recentStory, ct);

        var result = new ActionResult
        {
            ActionId = action.Id,
            Success = freeForm.Success,
            MechanicalSummary = freeForm.Success ? $"[Free action: {action.RawInput}]" : $"[Failed: {action.RawInput}]",
            Narration = freeForm.Narration
        };

        // Apply stat changes
        foreach (var (stat, delta) in freeForm.StatChanges)
        {
            switch (stat.ToLowerInvariant())
            {
                case "hp":
                    var oldHp = player.Hp;
                    player.Hp = Math.Clamp(player.Hp + delta, 0, player.MaxHp);
                    result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() });
                    break;
                case "mp":
                    var oldMp = player.Mp;
                    player.Mp = Math.Clamp(player.Mp + delta, 0, player.MaxMp);
                    result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString() });
                    break;
                case "gold":
                    var oldGold = player.Gold;
                    player.Gold = Math.Max(0, player.Gold + delta);
                    result.GoldChange += delta;
                    result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() });
                    break;
                case "xp":
                    var oldXp = player.Xp;
                    player.Xp = Math.Max(0, player.Xp + delta);
                    result.XpGained += Math.Max(0, delta);
                    result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "xp", OldValue = oldXp.ToString(), NewValue = player.Xp.ToString() });
                    break;
            }
        }

        // Apply inventory changes
        foreach (var change in freeForm.InventoryChanges)
        {
            if (string.Equals(change.Action, "add", StringComparison.OrdinalIgnoreCase))
            {
                var item = new InventoryItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = change.ItemName,
                    Quantity = Math.Max(1, change.Quantity),
                    Type = ItemType.Misc
                };
                player.Inventory.Add(item);
                result.ItemsGained.Add(item);
            }
            else if (string.Equals(change.Action, "remove", StringComparison.OrdinalIgnoreCase))
            {
                var existing = player.Inventory.FirstOrDefault(i => i.Name.Equals(change.ItemName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    player.Inventory.Remove(existing);
                    result.ItemsLost.Add(existing);
                }
            }
        }

        // Apply entity changes to room
        foreach (var change in freeForm.EntityChanges)
        {
            if (string.Equals(change.EntityType, "npc", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(change.Action, "remove", StringComparison.OrdinalIgnoreCase))
                {
                    room.Npcs.RemoveAll(n => n.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
                }
                else if (string.Equals(change.Action, "add", StringComparison.OrdinalIgnoreCase))
                {
                    room.Npcs.Add(new Npc { Id = Guid.NewGuid().ToString("N"), Name = change.Name });
                }
            }
            else if (string.Equals(change.EntityType, "item", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(change.Action, "remove", StringComparison.OrdinalIgnoreCase))
                {
                    room.Items.RemoveAll(i => i.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
                }
                else if (string.Equals(change.Action, "add", StringComparison.OrdinalIgnoreCase))
                {
                    room.Items.Add(new InventoryItem { Id = Guid.NewGuid().ToString("N"), Name = change.Name, Type = ItemType.Misc });
                }
            }
        }

        // Apply room description/exit changes
        if (freeForm.RoomChanges is not null)
        {
            if (freeForm.RoomChanges.NewDescription is not null)
                room.Description = freeForm.RoomChanges.NewDescription;

            if (freeForm.RoomChanges.NewExits is not null)
            {
                foreach (var (dir, target) in freeForm.RoomChanges.NewExits)
                    room.Exits[dir] = target;
            }
        }

        // Persist mutations
        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);

        // Record story entry
        await _stateManager.AddStoryEntryAsync(new StoryEntry
        {
            ActionId = action.Id,
            PlayerId = player.Id,
            RoomId = player.CurrentRoomId,
            MechanicalSummary = result.MechanicalSummary,
            Narration = result.Narration ?? result.MechanicalSummary
        }, ct);

        return result;
    }

    private static string SummarizeEntities<T>(IEnumerable<T> entities, Func<T, string?> getName, Func<T, int>? getQuantity = null)
    {
        var counts = new Dictionary<string, (string DisplayName, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in entities)
        {
            var name = getName(entity)?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var quantity = Math.Max(1, getQuantity?.Invoke(entity) ?? 1);
            if (counts.TryGetValue(name, out var existing))
            {
                counts[name] = (existing.DisplayName, existing.Count + quantity);
                continue;
            }

            counts[name] = (name, quantity);
        }

        return string.Join(", ", counts.Values.Select(entry => entry.Count > 1 ? $"{entry.DisplayName} (x{entry.Count})" : entry.DisplayName));
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
