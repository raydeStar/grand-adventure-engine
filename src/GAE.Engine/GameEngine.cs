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
    private readonly IWikiService? _wiki;

    public GameEngine(
        IStateManager stateManager,
        IProbabilityEngine dice,
        INarratorService narrator,
        CommandParser parser,
        GameRulesConfig rules,
        ILogger<GameEngine> logger,
        IWikiService? wiki = null)
    {
        _stateManager = stateManager;
        _dice = dice;
        _narrator = narrator;
        _parser = parser;
        _rules = rules;
        _logger = logger;
        _wiki = wiki;
    }

    public GameAction ParseCommand(string playerId, string rawInput)
        => _parser.Parse(playerId, rawInput);

    public async Task<ActionResult> ProcessActionAsync(string playerId, GameAction action, CancellationToken ct = default)
    {
        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        if (player is null)
            return new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = false, MechanicalSummary = "Player not found." };

        // --- Interaction mode routing ---
        // When the player is in a non-explore mode, route through the interaction handler first.
        if (player.Interaction.Mode != InteractionMode.Explore)
        {
            var interactionResult = await ProcessInteractionTurnAsync(player, action, ct);
            if (interactionResult is not null)
                return interactionResult;
            // If null, the interaction handler decided to fall through to normal processing
            // (e.g., the interaction ended and the action should be handled normally)
        }

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
            ActionType.Drop => await ProcessDropAsync(player, action, ct),
            ActionType.Equip => ProcessEquip(player, action),
            ActionType.Unequip => ProcessUnequip(player, action),
            ActionType.Rest or ActionType.ShortRest => ProcessShortRest(player, action),
            ActionType.LongRest => ProcessLongRest(player, action),
            ActionType.Inventory => ProcessInventory(player, action),
            ActionType.Stats => ProcessStats(player, action),
            ActionType.Help => ProcessHelp(action),
            _ => await ProcessFreeFormActionAsync(player, action, ct)
        };

        result.RawInput = action.RawInput;

        // Save player state after any mutation
        if (result.Success && result.StateChanges.Count > 0)
        {
            player.LastActiveAt = DateTimeOffset.UtcNow;
            await _stateManager.SavePlayerAsync(player, ct);
        }

        // Narrate the result (free-form actions handle their own narration and story)
        var shouldNarrate = action.Type != ActionType.Help
            && action.Type != ActionType.Stats
            && action.Type != ActionType.Inventory
            && action.Type != ActionType.Unknown;

        if (shouldNarrate)
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
                result.Narration = result.Narration ?? result.MechanicalSummary;
            }

        }

        if (action.Type != ActionType.Unknown)
        {
            await _stateManager.AddStoryEntryAsync(new StoryEntry
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                PlayerId = playerId,
                RoomId = player.CurrentRoomId,
                MechanicalSummary = result.MechanicalSummary,
                Narration = result.Narration ?? string.Empty
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
                PublishToWikiInBackground(targetRoom);
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

        if (string.IsNullOrWhiteSpace(action.Target) || TargetReferencesRoom(room, action.Target))
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = string.Empty };

        var npc = FindNamedEntity(room.Npcs, candidate => candidate.Name, action.Target);
        if (npc is not null)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = string.Empty };

        var item = FindNamedEntity(room.Items, candidate => candidate.Name, action.Target);
        if (item is not null)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = string.Empty };

        return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Look target '{action.Target}' was not found in the current room." };
    }

    private async Task<ActionResult> ProcessAttackAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You can't attack here." };

        var target = FindNamedEntity(room.Npcs, npc => npc.Name, action.Target);

        if (target is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Attack target '{action.Target}' was not found in the current room." };

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
            player.Interaction.Reset();
            result.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "victory" };
            await _stateManager.SaveRoomAsync(room, ct);
        }
        else if (target.Hp.HasValue && target.Hp.Value > 0)
        {
            // NPC survived — enter combat mode
            EnterCombat(player, target);
            result.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Combat, CombatStatus = "ongoing" };
            await _stateManager.SavePlayerAsync(player, ct);
        }

        return result;
    }

    private Task<ActionResult> ProcessTalkAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        return ProcessTalkInternalAsync(player, action, ct);
    }

    private async Task<ActionResult> ProcessTalkInternalAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You find no one here to answer you." };

        var target = FindNamedEntity(room.Npcs, npc => npc.Name, action.Target);
        if (target is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Conversation target '{action.Target}' was not found in the current room." };

        // Enter conversation mode
        player.Interaction = new InteractionState
        {
            Mode = InteractionMode.Conversation,
            Target = target.Name,
            NpcDisposition = target.Disposition,
            CanLeave = true,
            LeaveConsequence = "normal"
        };
        player.Interaction.AppendContext($"Player initiated conversation with {target.Name}.");

        // Get AI narration for the greeting
        var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, target, player.Interaction, action.RawInput, ct);

        // Apply interaction update from AI
        ApplyInteractionUpdate(player, room, target, freeForm);

        await _stateManager.SavePlayerAsync(player, ct);

        var result = new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You begin speaking with {target.Name}.",
            Narration = freeForm.Narration,
            InteractionUpdate = freeForm.InteractionUpdate
        };

        await PersistInteractionStoryEntry(player, action, result, ct);
        return result;
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

    private async Task<ActionResult> ProcessDropAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You have nowhere to drop that." };

        var (requestedQuantity, rawTarget) = ParseQuantityTarget(action.Target);
        if (TryResolveGoldDrop(player, room, requestedQuantity, rawTarget, out var goldDropResult))
        {
            goldDropResult.ActionId = action.Id;
            await _stateManager.SaveRoomAsync(room, ct);
            return goldDropResult;
        }

        var item = FindNamedEntity(player.Inventory, inventoryItem => inventoryItem.Name, rawTarget);

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Drop target '{action.Target}' was not found in your inventory." };

        var quantityToDrop = requestedQuantity ?? Math.Max(1, item.Quantity);
        if (quantityToDrop <= 0)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You need to drop at least one item." };

        if (item.Quantity < quantityToDrop)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You only have {item.Quantity} {item.Name} to drop." };

        var droppedItem = CloneInventoryItem(item, quantityToDrop);
        if (item.Quantity == quantityToDrop)
        {
            player.Inventory.Remove(item);
        }
        else
        {
            item.Quantity -= quantityToDrop;
        }

        AddRoomItem(room, droppedItem);
        await _stateManager.SaveRoomAsync(room, ct);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"You drop {FormatDroppedItemLabel(droppedItem.Name, droppedItem.Quantity)}.",
            ItemsLost = [droppedItem],
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Inventory", NewValue = $"removed {FormatDroppedItemLabel(droppedItem.Name, droppedItem.Quantity)}" },
                new StateChange { EntityType = "Room", EntityId = room.Id, Property = "Items", NewValue = $"added {FormatDroppedItemLabel(droppedItem.Name, droppedItem.Quantity)}" }
            ]
        };
    }

    private ActionResult ProcessEquip(PlayerCharacter player, GameAction action)
    {
        var item = FindNamedEntity(player.Inventory.Where(i => i.IsEquippable), inventoryItem => inventoryItem.Name, action.Target);

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Equip target '{action.Target}' was not found among your equippable gear." };

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
        var equippedItems = new[]
        {
            player.Equipment.Weapon,
            player.Equipment.Armor,
            player.Equipment.Shield,
            player.Equipment.Helmet
        }.Where(item => item is not null).Cast<InventoryItem>();

        var item = FindNamedEntity(equippedItems, inventoryItem => inventoryItem.Name, action.Target);

        if (ReferenceEquals(item, player.Equipment.Weapon))
            player.Equipment.Weapon = null;
        else if (ReferenceEquals(item, player.Equipment.Armor))
            player.Equipment.Armor = null;
        else if (ReferenceEquals(item, player.Equipment.Shield))
            player.Equipment.Shield = null;
        else if (ReferenceEquals(item, player.Equipment.Helmet))
            player.Equipment.Helmet = null;

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Unequip target '{action.Target}' was not found among your equipped items." };

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
            RawInput = action.RawInput,
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

    private static (int? Quantity, string Target) ParseQuantityTarget(string? rawTarget)
    {
        var target = rawTarget?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
            return (null, string.Empty);

        var parts = target.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var quantity) && quantity > 0)
            return (quantity, parts[1].Trim());

        return (null, target);
    }

    private static bool TryResolveGoldDrop(PlayerCharacter player, Room room, int? requestedQuantity, string rawTarget, out ActionResult result)
    {
        result = null!;
        var normalizedTarget = NormalizeLookupText(rawTarget);
        if (normalizedTarget is not "gold" and not "coin" and not "coins" and not "gold coin" and not "gold coins")
            return false;

        var quantityToDrop = requestedQuantity ?? 1;
        if (quantityToDrop <= 0)
        {
            result = new ActionResult { Success = false, MechanicalSummary = "You need to drop at least one gold coin." };
            return true;
        }

        if (player.Gold < quantityToDrop)
        {
            result = new ActionResult { Success = false, MechanicalSummary = $"You only have {player.Gold} gold to drop." };
            return true;
        }

        var oldGold = player.Gold;
        player.Gold -= quantityToDrop;

        var droppedGold = new InventoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Gold Coin",
            Description = "A stamped coin of common gold.",
            Type = ItemType.Misc,
            Quantity = quantityToDrop,
            Value = 1
        };

        AddRoomItem(room, droppedGold);
        result = new ActionResult
        {
            Success = true,
            MechanicalSummary = $"You drop {FormatDroppedItemLabel(droppedGold.Name, droppedGold.Quantity)}.",
            GoldChange = -quantityToDrop,
            ItemsLost = [CloneInventoryItem(droppedGold, droppedGold.Quantity)],
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() },
                new StateChange { EntityType = "Room", EntityId = room.Id, Property = "Items", NewValue = $"added {FormatDroppedItemLabel(droppedGold.Name, droppedGold.Quantity)}" }
            ]
        };
        return true;
    }

    private static InventoryItem CloneInventoryItem(InventoryItem item, int quantity)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = item.Name,
            Description = item.Description,
            Type = item.Type,
            Quantity = quantity,
            Value = item.Value,
            DamageDice = item.DamageDice,
            DamageStat = item.DamageStat,
            ArmorValue = item.ArmorValue,
            IsEquippable = item.IsEquippable,
            IsConsumable = item.IsConsumable,
            Effect = item.Effect
        };

    private static void AddRoomItem(Room room, InventoryItem item)
    {
        var existing = room.Items.FirstOrDefault(candidate => string.Equals(candidate.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            room.Items.Add(item);
            return;
        }

        existing.Quantity += Math.Max(1, item.Quantity);
    }

    private static string FormatDroppedItemLabel(string itemName, int quantity)
    {
        var normalizedName = itemName.Trim();
        if (quantity <= 1)
            return quantity == 1 && NormalizeLookupText(normalizedName) == "gold coin"
                ? "1 gold coin"
                : normalizedName;

        if (NormalizeLookupText(normalizedName) == "gold coin")
            return $"{quantity} gold coins";

        return $"{normalizedName} (x{quantity})";
    }

    private static T? FindNamedEntity<T>(IEnumerable<T> entities, Func<T, string?> getName, string? rawQuery) where T : class
    {
        var query = NormalizeLookupText(rawQuery);
        if (string.IsNullOrWhiteSpace(query))
            return null;

        return entities
            .Select(entity => new { Entity = entity, Candidate = NormalizeLookupText(getName(entity)) })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Candidate))
            .Select(entry => new { entry.Entity, Score = GetNameMatchScore(query, entry.Candidate) })
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .Select(entry => entry.Entity)
            .FirstOrDefault();
    }

    private static bool TargetReferencesRoom(Room room, string? rawTarget)
    {
        var target = NormalizeLookupText(rawTarget);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (target is "room" or "around" or "here" or "surroundings")
            return true;

        var roomName = NormalizeLookupText(room.Name);
        return target == roomName || Singularize(target) == Singularize(roomName);
    }

    private static int GetNameMatchScore(string query, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return 0;

        var normalizedQuery = Singularize(query);
        var normalizedCandidate = Singularize(candidate);

        if (candidate == query || normalizedCandidate == normalizedQuery)
            return 100;

        if (candidate.StartsWith(query, StringComparison.Ordinal) || candidate.StartsWith(normalizedQuery, StringComparison.Ordinal))
            return 90;

        if (candidate.Contains(query, StringComparison.Ordinal) || candidate.Contains(normalizedQuery, StringComparison.Ordinal))
            return 75;

        if (query.StartsWith(candidate, StringComparison.Ordinal) || normalizedQuery.StartsWith(normalizedCandidate, StringComparison.Ordinal))
            return 65;

        if (query.Contains(candidate, StringComparison.Ordinal) || normalizedQuery.Contains(normalizedCandidate, StringComparison.Ordinal))
            return 55;

        return 0;
    }

    private static string NormalizeLookupText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalizedCharacters = value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character)
                ? character
                : character is '-' or '_' || char.IsWhiteSpace(character)
                    ? ' '
                    : '\0')
            .Where(character => character != '\0')
            .ToArray();

        var normalized = new string(normalizedCharacters);
        return string.Join(' ', normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not "the" and not "a" and not "an"));
    }

    private static string Singularize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= 3)
            return value;

        if (value.EndsWith("ies", StringComparison.Ordinal))
            return value[..^3] + "y";

        if (value.EndsWith('s') && !value.EndsWith("ss", StringComparison.Ordinal))
            return value[..^1];

        return value;
    }

    // --- Interaction state machine ---

    private static readonly HashSet<string> ConversationExitPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "goodbye", "bye", "farewell", "leave", "walk away", "end conversation", "nevermind", "never mind"
    };

    /// <summary>
    /// Routes a player turn through the active interaction mode (conversation, combat, etc.).
    /// Returns null if the interaction ended and normal processing should continue.
    /// </summary>
    private async Task<ActionResult?> ProcessInteractionTurnAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        return player.Interaction.Mode switch
        {
            InteractionMode.Conversation => await ProcessConversationTurnAsync(player, action, ct),
            InteractionMode.Combat => await ProcessCombatTurnAsync(player, action, ct),
            _ => null
        };
    }

    private async Task<ActionResult?> ProcessConversationTurnAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
        {
            player.Interaction.Reset();
            return null;
        }

        var npc = FindNamedEntity(room.Npcs, n => n.Name, player.Interaction.Target);
        if (npc is null)
        {
            var missingTarget = player.Interaction.Target;
            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);
            return new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = $"{missingTarget} is no longer here.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            };
        }

        // Movement mid-conversation: exit with disposition penalty
        if (action.Type == ActionType.Move)
        {
            var dispositionPenalty = ShiftDisposition(npc.Disposition, -1);
            npc.Disposition = dispositionPenalty;
            player.Interaction.AppendContext($"Player walked away mid-conversation. {npc.Name}'s disposition shifted to {dispositionPenalty}.");

            var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, npc, player.Interaction, "[Player walks away mid-sentence]", ct);
            ApplyInteractionUpdate(player, room, npc, freeForm);

            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);
            await _stateManager.SaveRoomAsync(room, ct);

            var exitResult = new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = $"You walk away from {npc.Name}. Their disposition shifts to {dispositionPenalty}.",
                Narration = freeForm.Narration,
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, NpcDisposition = dispositionPenalty }
            };

            await PersistInteractionStoryEntry(player, action, exitResult, ct);
            return null; // Let the movement continue through normal processing
        }

        // Attack mid-conversation: transition to combat
        if (action.Type == ActionType.Attack)
        {
            player.Interaction.AppendContext($"Player attacked {npc.Name} mid-conversation!");
            player.Interaction.Reset();

            if (npc.Hp.HasValue && npc.Hp > 0)
            {
                EnterCombat(player, npc);
                await _stateManager.SavePlayerAsync(player, ct);
                return await ProcessCombatTurnAsync(player, action, ct);
            }

            return null; // Fall through to normal attack processing
        }

        // Explicit goodbye / leave
        if (ConversationExitPhrases.Contains(action.RawInput.Trim()))
        {
            var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, npc, player.Interaction, action.RawInput, ct);
            ApplyInteractionUpdate(player, room, npc, freeForm);

            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);
            await _stateManager.SaveRoomAsync(room, ct);

            var result = new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = $"You end your conversation with {npc.Name}.",
                Narration = freeForm.Narration,
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, NpcDisposition = npc.Disposition }
            };

            await PersistInteractionStoryEntry(player, action, result, ct);
            return result;
        }

        // Normal conversation turn — everything the player says goes to the AI as dialogue
        {
            var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, npc, player.Interaction, action.RawInput, ct);
            ApplyInteractionUpdate(player, room, npc, freeForm);

            if (freeForm.InteractionUpdate?.Mode == InteractionMode.Explore)
                player.Interaction.Reset();

            if (freeForm.CombatInitiated && npc.Hp.HasValue && npc.Hp > 0)
            {
                player.Interaction.Reset();
                EnterCombat(player, npc);
            }

            await _stateManager.SavePlayerAsync(player, ct);
            await _stateManager.SaveRoomAsync(room, ct);

            var result = new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = freeForm.Success,
                MechanicalSummary = $"[Conversation with {npc.Name}, turn {player.Interaction.TurnCount}]",
                Narration = freeForm.Narration,
                InteractionUpdate = freeForm.InteractionUpdate ?? new InteractionUpdate
                {
                    Mode = player.Interaction.Mode,
                    NpcDisposition = npc.Disposition
                }
            };

            ApplyFreeFormStatChanges(player, freeForm, result);
            await PersistInteractionStoryEntry(player, action, result, ct);
            return result;
        }
    }

    private async Task<ActionResult?> ProcessCombatTurnAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetRoomAsync(player.CurrentRoomId, ct);
        if (room is null)
        {
            player.Interaction.Reset();
            return null;
        }

        var enemy = FindNamedEntity(room.Npcs, n => n.Name, player.Interaction.Target);
        if (enemy is null)
        {
            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);
            return new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = "Your opponent is no longer here. Combat ends.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            };
        }

        // Flee attempt
        var rawLower = action.RawInput.Trim().ToLowerInvariant();
        if (rawLower is "flee" or "run" or "escape" or "run away")
        {
            int oppDamage = 0;
            var diceRolls = new List<DiceRoll>();
            if (enemy.DamageDice is not null)
            {
                var oppRoll = _dice.Roll(enemy.DamageDice, $"{enemy.Name} opportunity attack");
                oppDamage = Math.Max(1, oppRoll.Total);
                diceRolls.Add(oppRoll);
                player.Hp = Math.Max(0, player.Hp - oppDamage);
            }

            player.Interaction.Reset();

            string? escapeDirection = room.Exits.Keys.FirstOrDefault();
            if (escapeDirection is not null)
                player.CurrentRoomId = room.Exits[escapeDirection];

            await _stateManager.SavePlayerAsync(player, ct);

            var fleeResult = new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = oppDamage > 0
                    ? $"You flee from {enemy.Name}! They strike you for {oppDamage} damage as you escape."
                    : $"You flee from {enemy.Name}!",
                DiceRolls = diceRolls,
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "fled" },
                StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", NewValue = player.Hp.ToString() }]
            };

            await PersistInteractionStoryEntry(player, action, fleeResult, ct);
            return fleeResult;
        }

        // Normal combat turn — route through AI
        var freeForm = await _narrator.ProcessCombatTurnAsync(player, room, enemy, player.Interaction, action.RawInput, ct);
        ApplyFreeFormStatChanges(player, freeForm, null);

        if (freeForm.InteractionUpdate?.EnemyUpdate.TryGetValue("hp", out var enemyHpDelta) == true && enemy.Hp.HasValue)
            enemy.Hp = Math.Max(0, enemy.Hp.Value + enemyHpDelta);

        ApplyInteractionUpdate(player, room, enemy, freeForm);

        var combatStatus = freeForm.InteractionUpdate?.CombatStatus ?? "ongoing";

        var combatResult = new ActionResult
        {
            ActionId = action.Id,
            RawInput = action.RawInput,
            Success = freeForm.Success,
            MechanicalSummary = $"[Combat with {enemy.Name}: {combatStatus}]",
            Narration = freeForm.Narration,
            InteractionUpdate = freeForm.InteractionUpdate ?? new InteractionUpdate
            {
                Mode = InteractionMode.Combat,
                CombatStatus = "ongoing"
            }
        };

        if (combatStatus == "victory" || (enemy.Hp.HasValue && enemy.Hp.Value <= 0))
        {
            int xpGain = enemy.Level * 10;
            player.Xp += xpGain;
            combatResult.XpGained = xpGain;
            combatResult.MechanicalSummary = $"{enemy.Name} has been defeated! You gain {xpGain} XP.";

            if (_dice.Roll("1d100", "Loot check").Total <= (int)(_rules.Loot.EnemyDropChance * 100))
            {
                var goldRoll = _dice.Roll("1d20", "Gold drop");
                int goldAmount = goldRoll.Total + (enemy.Level * 2);
                player.Gold += goldAmount;
                combatResult.GoldChange = goldAmount;
                combatResult.MechanicalSummary += $" You find {goldAmount} gold!";
            }

            foreach (var lootItem in freeForm.InteractionUpdate?.Loot ?? [])
            {
                player.Inventory.Add(lootItem);
                combatResult.ItemsGained.Add(lootItem);
            }

            room.Npcs.Remove(enemy);
            player.Interaction.Reset();
            combatResult.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "victory" };
        }
        else if (combatStatus == "defeat" || player.Hp <= 0)
        {
            player.Hp = 0;
            player.Interaction.Reset();
            combatResult.MechanicalSummary = $"You have been defeated by {enemy.Name}!";
            combatResult.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "defeat" };
        }

        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);
        await PersistInteractionStoryEntry(player, action, combatResult, ct);
        return combatResult;
    }

    private void EnterCombat(PlayerCharacter player, Npc enemy)
    {
        player.Interaction = new InteractionState
        {
            Mode = InteractionMode.Combat,
            Target = enemy.Name,
            CanLeave = false,
            LeaveConsequence = "flee_penalty"
        };
        player.Interaction.AppendContext($"Combat initiated with {enemy.Name}.");
    }

    private static void ApplyInteractionUpdate(PlayerCharacter player, Room room, Npc npc, FreeFormResponse freeForm)
    {
        var update = freeForm.InteractionUpdate;
        if (update is null) return;

        if (update.NpcDisposition is not null)
            npc.Disposition = update.NpcDisposition;

        foreach (var contextEntry in update.Context)
            player.Interaction.AppendContext(contextEntry);
    }

    private static void ApplyFreeFormStatChanges(PlayerCharacter player, FreeFormResponse freeForm, ActionResult? result)
    {
        foreach (var (stat, delta) in freeForm.StatChanges)
        {
            switch (stat.ToLowerInvariant())
            {
                case "hp":
                    player.Hp = Math.Clamp(player.Hp + delta, 0, player.MaxHp);
                    break;
                case "mp":
                    player.Mp = Math.Clamp(player.Mp + delta, 0, player.MaxMp);
                    break;
                case "gold":
                    player.Gold = Math.Max(0, player.Gold + delta);
                    if (result is not null) result.GoldChange += delta;
                    break;
                case "xp":
                    player.Xp = Math.Max(0, player.Xp + delta);
                    if (result is not null) result.XpGained += Math.Max(0, delta);
                    break;
            }
        }
    }

    private async Task PersistInteractionStoryEntry(PlayerCharacter player, GameAction action, ActionResult result, CancellationToken ct)
    {
        await _stateManager.AddStoryEntryAsync(new StoryEntry
        {
            ActionId = action.Id,
            RawInput = action.RawInput,
            PlayerId = player.Id,
            RoomId = player.CurrentRoomId,
            MechanicalSummary = result.MechanicalSummary,
            Narration = result.Narration ?? result.MechanicalSummary
        }, ct);
    }

    private static string ShiftDisposition(string current, int direction)
    {
        string[] scale = ["hostile", "angry", "annoyed", "suspicious", "neutral", "amused", "friendly", "flirtatious"];
        var index = Array.FindIndex(scale, d => d.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 4; // default to neutral
        var newIndex = Math.Clamp(index + direction, 0, scale.Length - 1);
        return scale[newIndex];
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

    private void PublishToWikiInBackground(Room room)
    {
        if (_wiki is null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _wiki.SyncRoomPageAsync(room);
                foreach (var npc in room.Npcs)
                    await _wiki.SyncNpcPageAsync(npc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background wiki publish failed for room {RoomId}", room.Id);
            }
        });
    }

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
