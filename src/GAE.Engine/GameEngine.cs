using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
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
    private readonly IContentRegistryService? _registry;


    public GameEngine(
        IStateManager stateManager,
        IProbabilityEngine dice,
        INarratorService narrator,
        CommandParser parser,
        GameRulesConfig rules,
        ILogger<GameEngine> logger,
        IWikiService? wiki = null,
        IContentRegistryService? registry = null)
    {
        _stateManager = stateManager;
        _dice = dice;
        _narrator = narrator;
        _parser = parser;
        _rules = rules;
        _logger = logger;
        _wiki = wiki;
        _registry = registry;
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
            ActionType.Take => await ProcessTakeAsync(player, action, ct),
            ActionType.Drop => await ProcessDropAsync(player, action, ct),
            ActionType.Use => await ProcessUseAsync(player, action, ct),
            ActionType.Buy => await ProcessBuyAsync(player, action, ct),
            ActionType.Sell => await ProcessSellAsync(player, action, ct),
            ActionType.Shop => await ProcessShopAsync(player, action, ct),
            ActionType.Equip => await ProcessEquipAsync(player, action, ct),
            ActionType.Unequip => ProcessUnequip(player, action),
            ActionType.Rest or ActionType.ShortRest => ProcessShortRest(player, action),
            ActionType.LongRest => ProcessLongRest(player, action),
            ActionType.Inventory => ProcessInventory(player, action),
            ActionType.Stats => ProcessStats(player, action),
            ActionType.Cast => await ProcessCastAsync(player, action, ct),
            ActionType.Help => ProcessHelp(action),
            ActionType.Map => await ProcessMapAsync(player, action, ct),
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
            && action.Type != ActionType.Map
            && action.Type != ActionType.Cast
            && action.Type != ActionType.Shop
            && action.Type != ActionType.Unknown;

        if (shouldNarrate)
        {
            try
            {
                var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
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
        // Derive attribute stat keys from rules (preserving YAML order)
        var attributeKeys = _rules.Stats
            .Where(kv => kv.Value.Category == "attribute")
            .Select(kv => kv.Key)
            .ToList();

        var statValues = concept.StatMethod switch
        {
            StatAllocationMethod.Roll4d6DropLowest => _dice.RollStatArray(),
            StatAllocationMethod.StandardArray => OrderStandardArrayByClass(concept.Class, attributeKeys, _rules.CharacterCreation.StandardArray),
            StatAllocationMethod.FlatValue => Enumerable.Repeat(_rules.CharacterCreation.FlatValue, attributeKeys.Count).ToArray(),
            StatAllocationMethod.Manual when concept.ManualStats is not null =>
                attributeKeys.Select(k => concept.ManualStats.GetValueOrDefault(k, 10)).ToArray(),
            _ => OrderStandardArrayByClass(concept.Class, attributeKeys, _rules.CharacterCreation.StandardArray)
        };

        var player = new PlayerCharacter
        {
            Id = concept.PlayerDiscordId,
            Name = concept.Name,
            Race = concept.Race,
            Class = concept.Class,
            Backstory = concept.Backstory,
            Gold = _rules.CharacterCreation.StartingGold,
            CurrentRoomId = "spawn"
        };

        // Populate stats from rules-defined attributes
        var statMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < attributeKeys.Count && i < statValues.Length; i++)
            statMap[attributeKeys[i]] = statValues[i];

        player.Str = statMap.GetValueOrDefault("str", 10);
        player.Dex = statMap.GetValueOrDefault("dex", 10);
        player.Con = statMap.GetValueOrDefault("con", 10);
        player.Int = statMap.GetValueOrDefault("int", 10);
        player.Wis = statMap.GetValueOrDefault("wis", 10);
        player.Cha = statMap.GetValueOrDefault("cha", 10);
        player.Luck = statMap.GetValueOrDefault("luck", 10);

        // Set starting level
        player.Level = _rules.CharacterCreation.StartingLevel;

        // Calculate HP/MP from rules — scale with level
        int hpBase = _rules.Stats.GetValueOrDefault("hp")?.Base ?? 20;
        int mpBase = _rules.Stats.GetValueOrDefault("mp")?.Base ?? 10;
        int conMod = PlayerCharacter.GetStatModifier(player.Con);
        int intMod = PlayerCharacter.GetStatModifier(player.Int);
        // Each level above 1 adds (5 + CON mod) HP and (3 + INT mod) MP
        int bonusLevels = Math.Max(0, player.Level - 1);
        player.MaxHp = hpBase + conMod + bonusLevels * (5 + Math.Max(0, conMod));
        player.Hp = player.MaxHp;
        player.MaxMp = mpBase + intMod + bonusLevels * (3 + Math.Max(0, intMod));
        player.Mp = player.MaxMp;

        // Grant starting items from the registry / lore seed
        await GrantStartingItemsAsync(player, ct);

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

    /// <summary>
    /// Grants starting items to a new character by looking them up in the item registry
    /// or shop inventories from the starting room. Auto-equips weapons and armor.
    /// </summary>
    private async Task GrantStartingItemsAsync(PlayerCharacter player, CancellationToken ct)
    {
        if (_rules.CharacterCreation.StartingItems.Count == 0) return;

        // Try to resolve items from the registry first
        foreach (var itemName in _rules.CharacterCreation.StartingItems)
        {
            InventoryItem? item = null;

            // Check registry
            if (_registry is not null)
            {
                var template = _registry.Items.GetAll()
                    .FirstOrDefault(t => t.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase));
                if (template is not null)
                    item = template.ToInventoryItem();
            }

            // Fallback: check starting room's shop inventories
            if (item is null)
            {
                var spawnRoom = await _stateManager.GetPlayerRoomAsync(player.Id, "spawn", ct);
                if (spawnRoom is not null)
                    item = TryResolveKnownItem(itemName, spawnRoom);

                // Also check all connected rooms' shops (blacksmith, general store, etc.)
                if (item is null && spawnRoom is not null)
                {
                    foreach (var exitRoomId in spawnRoom.Exits.Values)
                    {
                        var exitRoom = await _stateManager.GetPlayerRoomAsync(player.Id, exitRoomId, ct);
                        if (exitRoom is not null)
                        {
                            item = TryResolveKnownItem(itemName, exitRoom);
                            if (item is not null) break;

                            // Check rooms one more level deep (e.g., town_square -> blacksmith)
                            foreach (var deepExitId in exitRoom.Exits.Values)
                            {
                                var deepRoom = await _stateManager.GetPlayerRoomAsync(player.Id, deepExitId, ct);
                                if (deepRoom is not null)
                                {
                                    item = TryResolveKnownItem(itemName, deepRoom);
                                    if (item is not null) break;
                                }
                            }
                            if (item is not null) break;
                        }
                    }
                }
            }

            if (item is not null)
            {
                // Auto-equip weapons and armor, put everything else in inventory
                if (item.IsEquippable)
                {
                    var slotName = player.Equipment.Equip(item, out _);
                    if (slotName is null)
                        player.Inventory.Add(item); // Slot taken, put in backpack
                }
                else
                {
                    // Stack consumables
                    var existing = player.Inventory.FirstOrDefault(i =>
                        string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                        existing.Quantity++;
                    else
                        player.Inventory.Add(item);
                }

                _logger.LogInformation("Granted starting item '{ItemName}' to {PlayerId}", itemName, player.Id);
            }
            else
            {
                _logger.LogWarning("Could not resolve starting item '{ItemName}' from registry or shops", itemName);
            }
        }
    }

    public async Task<CombatState?> GetActiveCombatAsync(string roomId, CancellationToken ct = default)
        => await _stateManager.GetCombatStateAsync(roomId, ct);

    // --- Action processors ---

    private async Task<ActionResult> ProcessMoveAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You are in an unknown location." };

        var direction = NormalizeDirection(action.Direction ?? "");
        if (!room.Exits.TryGetValue(direction, out var targetRoomId))
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"There is no exit to the {direction}." };

        // Use the simple room ID (strip player prefix if composite key leaked into exits)
        var simpleTargetId = StripPlayerPrefix(targetRoomId);
        var simpleSourceId = StripPlayerPrefix(player.CurrentRoomId);

        var targetRoom = await _stateManager.GetPlayerRoomAsync(player.Id, simpleTargetId, ct);
        if (targetRoom is null)
        {
            // Generate new room via narrator — pass a clean source room with simple ID
            var sourceForGen = new Room
            {
                Id = simpleSourceId,
                Name = room.Name,
                Description = room.Description,
                EnvironmentTags = room.EnvironmentTags,
                Exits = room.Exits
            };
            try
            {
                targetRoom = await _narrator.GenerateRoomAsync(simpleTargetId, direction, sourceForGen, ct);
                targetRoom.Id = simpleTargetId;
                targetRoom.IsDiscovered = true;
                targetRoom.DiscoveredAt = DateTimeOffset.UtcNow;
                // Ensure reverse exit uses simple ID
                targetRoom.Exits[OppositeDirection(direction)] = simpleSourceId;
                await _stateManager.SaveRoomAsync(targetRoom, ct);
                PublishToWikiInBackground(targetRoom);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room generation failed for {RoomId}", simpleTargetId);
                targetRoom = new Room
                {
                    Id = simpleTargetId,
                    Name = $"Unexplored Area ({simpleTargetId})",
                    Description = "A dimly lit area that stretches before you.",
                    IsDiscovered = true,
                    DiscoveredAt = DateTimeOffset.UtcNow,
                    Exits = new Dictionary<string, string> { [OppositeDirection(direction)] = simpleSourceId }
                };
                await _stateManager.SaveRoomAsync(targetRoom, ct);
            }
        }

        // Fix any composite keys that leaked into this room's exits
        RepairExitIds(targetRoom);

        // Decay NPC dispositions in the destination room
        foreach (var npc in targetRoom.Npcs)
        {
            var elapsed = DateTimeOffset.UtcNow - npc.DispositionState.LastUpdated;
            npc.DispositionState.DecayTowardBaseline(elapsed);
            npc.Disposition = npc.DispositionState.ToFlatDisposition();
        }

        var oldRoomId = player.CurrentRoomId;
        player.CurrentRoomId = simpleTargetId;

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
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
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

        // Check player's inventory
        var invItem = FindNamedEntity(player.Inventory, candidate => candidate.Name, action.Target);
        if (invItem is not null)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = string.Empty };

        // Check player's equipped items
        var equippedItems = player.Equipment.AllEquipped();
        var eqItem = FindNamedEntity(equippedItems, candidate => candidate.Name, action.Target);
        if (eqItem is not null)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = string.Empty };

        return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"'{action.Target}' was not found in the room or your inventory." };
    }

    private async Task<ActionResult> ProcessAttackAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You can't attack here." };

        var target = FindNamedEntity(room.Npcs, npc => npc.Name, action.Target);

        if (target is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Attack target '{action.Target}' was not found in the current room." };

        // Determine attack stat — caster classes use the higher of melee stat or magic stat
        var weapon = player.Equipment.Weapon;
        string attackStat = weapon?.DamageStat ?? _rules.Combat.MeleeStat;
        int attackMod = player.GetModifier(attackStat);

        // If the player's class is a caster and using default melee stat, let them use magic stat if it's better
        if (weapon?.DamageStat is null && IsCasterClass(player.Class))
        {
            int magicMod = player.GetModifier(_rules.Combat.MagicStat);
            if (magicMod > attackMod)
            {
                attackStat = _rules.Combat.MagicStat;
                attackMod = magicMod;
            }
        }

        var attackRoll = _dice.RollAttack(attackMod);
        int targetDefense = target.Defense ?? _rules.Combat.BaseDefense;

        // Determine outcome tier
        var outcome = ProbabilityEngine.DetermineOutcome(attackRoll, targetDefense);
        attackRoll.Outcome = outcome;
        attackRoll.TargetNumber = targetDefense;

        var result = new ActionResult
        {
            ActionId = action.Id,
            DiceRolls = [attackRoll]
        };

        if (outcome == RollOutcome.CriticalMiss)
        {
            result.Success = true;
            result.MechanicalSummary = $"You fumble your attack against {target.Name}! Critical miss!";
            return result;
        }

        if (outcome == RollOutcome.Miss)
        {
            result.Success = true;
            result.MechanicalSummary = $"You attack {target.Name} (rolled {attackRoll.Total} vs defense {targetDefense}) and miss!";
            return result;
        }

        // Hit (GlancingHit, Hit, or CriticalHit) — roll damage
        string damageDice = weapon?.DamageDice ?? "1d4";
        int damageMod = player.GetModifier(attackStat);
        var damageRoll = _dice.RollDamage(damageDice, damageMod);

        int totalDamage = outcome switch
        {
            RollOutcome.CriticalHit => damageRoll.Total * _rules.Combat.CriticalMultiplier,
            RollOutcome.GlancingHit => Math.Max(1, damageRoll.Total / 2),
            _ => damageRoll.Total
        };
        totalDamage = Math.Max(1, totalDamage);

        result.DiceRolls.Add(damageRoll);

        if (target.Hp.HasValue)
        {
            var oldHp = target.Hp.Value;
            target.Hp = Math.Max(0, target.Hp.Value - totalDamage);
            result.StateChanges.Add(new StateChange
            {
                EntityType = "Npc", EntityId = target.Id,
                Property = "Hp", OldValue = oldHp.ToString(), NewValue = target.Hp.Value.ToString()
            });
        }

        string outcomeText = outcome switch
        {
            RollOutcome.CriticalHit => " **CRITICAL HIT!**",
            RollOutcome.GlancingHit => " (glancing blow)",
            _ => ""
        };
        result.Success = true;
        result.MechanicalSummary = $"You attack {target.Name} (rolled {attackRoll.Total} vs defense {targetDefense}) and deal {totalDamage} damage!{outcomeText}";

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
            PublishEventToWikiInBackground(target.Id, "npc", $"{target.Name} Defeated",
                $"{player.Name} defeated {target.Name} (Level {target.Level}, {target.Faction}) in combat.", room.Id);
        }
        else if (target.Hp.HasValue && target.Hp.Value > 0)
        {
            // NPC survived — enter combat mode
            var hostiles = room.Npcs.Where(n => n.IsHostile || n == target).Distinct().ToList();
            await EnterCombatAsync(player, room, hostiles, ct);
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
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You find no one here to answer you." };

        var target = FindNamedEntity(room.Npcs, npc => npc.Name, action.Target);
        if (target is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Conversation target '{action.Target}' was not found in the current room." };

        // Decay disposition toward baseline before starting conversation
        var elapsed = DateTimeOffset.UtcNow - target.DispositionState.LastUpdated;
        target.DispositionState.DecayTowardBaseline(elapsed);
        target.Disposition = target.DispositionState.ToFlatDisposition();

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

    private async Task<ActionResult> ProcessTakeAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's nothing here to take." };

        var (requestedQuantity, rawTarget) = ParseQuantityTarget(action.Target);

        // Check if the item belongs to a shopkeeper — can't just take it
        var shopkeeper = room.Npcs.FirstOrDefault(n => n.IsShopkeeper);
        if (shopkeeper is not null)
        {
            var shopItem = FindNamedEntity(shopkeeper.ShopInventory, i => i.Name, rawTarget);
            if (shopItem is not null)
                return new ActionResult { ActionId = action.Id, Success = false,
                    MechanicalSummary = $"{shopItem.Name} belongs to {shopkeeper.Name} and costs {shopItem.Value} gold. Use `buy {shopItem.Name.ToLowerInvariant()}` to purchase it." };
        }

        // Try gold pickup first ("take gold", "pick up 10 gold")
        if (TryResolveGoldPickup(player, room, requestedQuantity, rawTarget, out var goldResult))
        {
            goldResult.ActionId = action.Id;
            await _stateManager.SaveRoomAsync(room, ct);
            return goldResult;
        }

        var item = FindNamedEntity(room.Items, roomItem => roomItem.Name, rawTarget);

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You don't see any '{action.Target}' here to pick up." };

        var quantityToTake = requestedQuantity ?? item.Quantity;
        if (quantityToTake <= 0)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You need to take at least one." };

        // When no explicit quantity and there are multiple individual stacks with same name, gather them all
        var allMatchingItems = room.Items
            .Where(i => string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int totalAvailable = allMatchingItems.Sum(i => i.Quantity);
        if (requestedQuantity.HasValue && requestedQuantity.Value > totalAvailable)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"There are only {totalAvailable} {item.Name} here." };

        int actualQuantity = requestedQuantity ?? totalAvailable;

        // Remove items from room
        int remaining = actualQuantity;
        foreach (var roomItem in allMatchingItems)
        {
            if (remaining <= 0) break;
            if (roomItem.Quantity <= remaining)
            {
                remaining -= roomItem.Quantity;
                room.Items.Remove(roomItem);
            }
            else
            {
                roomItem.Quantity -= remaining;
                remaining = 0;
            }
        }

        // Add to player inventory (stack with existing if possible)
        var existingStack = player.Inventory.FirstOrDefault(i =>
            string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase));

        var takenItem = CloneInventoryItem(item, actualQuantity);

        if (existingStack is not null)
        {
            existingStack.Quantity += actualQuantity;
        }
        else
        {
            player.Inventory.Add(takenItem);
        }


        // Auto-equip if the item is equippable and the relevant slot is empty
        string? autoEquipNote = null;
        var itemToEquip = existingStack ?? takenItem;
        if (itemToEquip.IsEquippable && actualQuantity == 1)
        {
            // Only auto-equip into single-slot types when that slot is empty
            // Don't auto-equip stackable accessories (rings/amulets/bracelets)
            bool slotEmpty = itemToEquip.Type switch
            {
                ItemType.Weapon => player.Equipment.MainHand is null,
                ItemType.Shield => player.Equipment.OffHand is null,
                ItemType.Armor => player.Equipment.Armor is null,
                ItemType.Helmet => player.Equipment.Helmet is null,
                ItemType.Cloak => player.Equipment.Cloak is null,
                ItemType.Boots => player.Equipment.Boots is null,
                ItemType.Gloves => player.Equipment.Gloves is null,
                _ => false
            };

            if (slotEmpty)
            {
                player.Equipment.Equip(itemToEquip, out _);
                player.Inventory.Remove(itemToEquip);
                autoEquipNote = $" Equipped {itemToEquip.Name}.";
            }
        }

        await _stateManager.SaveRoomAsync(room, ct);

        int totalInInventory = existingStack?.Quantity ?? actualQuantity;
        var itemLabel = actualQuantity > 1 ? $"{item.Name} (x{actualQuantity})" : item.Name;
        var inventoryNote = totalInInventory > actualQuantity
            ? $" You now have {totalInInventory}."
            : "";
        var takeChanges = new List<StateChange>
        {
            new() { EntityType = "Player", EntityId = player.Id, Property = "Inventory", NewValue = $"added {itemLabel}" },
            new() { EntityType = "Room", EntityId = room.Id, Property = "Items", NewValue = $"removed {itemLabel}" }
        };
        if (autoEquipNote is not null)
            takeChanges.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Equipment", NewValue = $"equipped {item.Name}" });


        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"{itemLabel} added to inventory.{inventoryNote}{autoEquipNote}",
            ItemsGained = [takenItem],
            StateChanges = takeChanges
        };
    }

    /// <summary>Handle "take gold" / "pick up gold coins" from room floor.</summary>
    private static bool TryResolveGoldPickup(PlayerCharacter player, Room room, int? requestedQuantity, string rawTarget, out ActionResult result)
    {
        result = null!;
        var normalizedTarget = NormalizeLookupText(rawTarget);
        if (normalizedTarget is not "gold" and not "coin" and not "coins" and not "gold coin" and not "gold coins"
            and not "gp" and not "gold piece" and not "gold pieces" and not "money")
            return false;

        var goldItems = room.Items
            .Where(i => string.Equals(i.Name, "Gold Coin", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (goldItems.Count == 0) return false;

        int totalGold = goldItems.Sum(i => i.Quantity);
        int toTake = requestedQuantity ?? totalGold;
        if (toTake > totalGold)
        {
            result = new ActionResult { Success = false, MechanicalSummary = $"There are only {totalGold} gold coins here." };
            return true;
        }

        // Remove gold from room
        int remaining = toTake;
        foreach (var coin in goldItems)
        {
            if (remaining <= 0) break;
            if (coin.Quantity <= remaining)
            {
                remaining -= coin.Quantity;
                room.Items.Remove(coin);
            }
            else
            {
                coin.Quantity -= remaining;
                remaining = 0;
            }
        }

        player.Gold += toTake;

        result = new ActionResult
        {
            Success = true,
            MechanicalSummary = $"Picked up {toTake} gold. You now have {player.Gold} gold.",
            GoldChange = toTake,
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Gold", OldValue = $"{player.Gold - toTake}", NewValue = $"{player.Gold}" }
            ]
        };
        return true;
    }

    private async Task<ActionResult> ProcessDropAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
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

    private async Task<ActionResult> ProcessShopAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's no shop here." };

        var shopkeeper = room.Npcs.FirstOrDefault(n => n.IsShopkeeper);
        if (shopkeeper is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's no shopkeeper here." };

        if (shopkeeper.ShopInventory.Count == 0)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = $"{shopkeeper.Name} has nothing for sale right now." };

        var lines = new List<string> { $"**{shopkeeper.Name}'s Wares:**", "" };
        foreach (var item in shopkeeper.ShopInventory)
        {
            var details = new List<string>();
            if (!string.IsNullOrEmpty(item.DamageDice)) details.Add($"Dmg: {item.DamageDice}");
            if (item.ArmorValue > 0) details.Add($"AC: +{item.ArmorValue}");
            if (!string.IsNullOrEmpty(item.Effect)) details.Add(item.Effect);
            var detailStr = details.Count > 0 ? $" ({string.Join(", ", details)})" : "";
            lines.Add($"  {item.Name} -- {item.Value}g{detailStr}");
            if (!string.IsNullOrEmpty(item.Description))
                lines.Add($"    *{item.Description}*");
        }
        lines.Add("");
        lines.Add($"You have **{player.Gold} gold**. Use `buy <item>` to purchase or `sell <item>` to sell.");

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = string.Join("\n", lines)
        };
    }

    private async Task<ActionResult> ProcessEquipAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var item = FindNamedEntity(player.Inventory.Where(i => i.IsEquippable), inventoryItem => inventoryItem.Name, action.Target);

        // If not in inventory, check the room — auto-take and equip in one step
        Room? room = null;
        bool takenFromRoom = false;
        if (item is null)
        {
            room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
            if (room is not null)
            {
                var roomItem = FindNamedEntity(room.Items.Where(i => i.IsEquippable), ri => ri.Name, action.Target);
                if (roomItem is not null)
                {
                    // Take from room into inventory, then equip below
                    room.Items.Remove(roomItem);
                    item = roomItem;
                    takenFromRoom = true;
                }
            }
        }

        if (item is null)
        {
            // Check if the item exists but isn't equippable (better error message)
            var nonEquippable = FindNamedEntity(player.Inventory, i => i.Name, action.Target);
            if (nonEquippable is not null)
                return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"{nonEquippable.Name} cannot be equipped." };
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Equip target '{action.Target}' was not found in your inventory or the current room." };
        }

        // Remove from inventory if it was there (not if just taken from room)
        if (!takenFromRoom)
            player.Inventory.Remove(item);

        // Equip into the correct slot(s), returning displaced items to inventory
        var slotName = player.Equipment.Equip(item, out var displaced);
        if (slotName is null)
        {
            // Not equippable — put it back
            if (takenFromRoom && room is not null)
                room.Items.Add(item);
            else
                player.Inventory.Add(item);
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"{item.Name} is not equippable." };
        }

        // Return displaced items to inventory
        foreach (var old in displaced)
            player.Inventory.Add(old);

        if (takenFromRoom && room is not null)
            await _stateManager.SaveRoomAsync(room, ct);
        await _stateManager.SavePlayerAsync(player, ct);

        var pickupNote = takenFromRoom ? $"You pick up {item.Name} and equip it." : $"You equip {item.Name}.";
        var changes = new List<StateChange>
        {
            new() { EntityType = "Player", EntityId = player.Id, Property = "Equipment", NewValue = item.Name }
        };
        if (takenFromRoom)
            changes.Add(new StateChange { EntityType = "Room", EntityId = room!.Id, Property = "Items", NewValue = $"removed {item.Name}" });

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = pickupNote,
            StateChanges = changes
        };
    }

    private ActionResult ProcessUnequip(PlayerCharacter player, GameAction action)
    {
        var item = FindNamedEntity(player.Equipment.AllEquipped(), i => i.Name, action.Target);

        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Unequip target '{action.Target}' was not found among your equipped items." };

        player.Equipment.Unequip(item);
        player.Inventory.Add(item);

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
        int hpRecovery = Math.Max(1, hpRoll.Total + PlayerCharacter.GetStatModifier(player.Con));
        int oldHp = player.Hp;
        player.Hp = Math.Min(player.MaxHp, player.Hp + hpRecovery);

        var mpRoll = _dice.Roll("1d4", "Short rest MP recovery");
        int mpRecovery = Math.Max(1, mpRoll.Total + PlayerCharacter.GetStatModifier(player.Int));
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
            ? string.Join("\n", player.Inventory.Select(i => $"  - {i.Name} (x{i.Quantity}) -- {i.Description}"))
            : "  Your inventory is empty.";

        var equipped = player.Equipment.AllEquipped()
            .Select(e => FormatEquippedItem(e))
            .ToList();
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
                **{player.Name}** -- Level {player.Level} {player.Race} {player.Class}
                HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp} | Gold: {player.Gold} | XP: {player.Xp}
                {player.FormatStatsDetailed(" | ")} | Defense: {player.Defense}
                """
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CAST — registered spell with exact mechanics, or improvised spell
    //         evaluated by the LLM with a power-budget system.
    // ═══════════════════════════════════════════════════════════════════

    private async Task<ActionResult> ProcessCastAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var spellName = action.Target ?? action.RawInput;
        var target = action.Parameters.GetValueOrDefault("target");
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        room ??= new Room { Id = player.CurrentRoomId, Name = "Unknown" };

        _logger.LogInformation("Cast attempt by {Player}: spell={Spell}, target={Target}", player.Name, spellName, target ?? "(none)");

        // ── Try registered spell first ──────────────────────────────
        if (_registry is not null)
        {
            var validation = _registry.ValidateSpellCast(spellName, player.Class, player.Level, player.Mp);
            if (validation.IsValid && validation.Spell is not null)
                return await CastRegisteredSpellAsync(player, validation.Spell, target, room, action, ct);

            // Spell IS in registry but player can't use it
            if (validation.Spell is not null && validation.FailureReason is not null)
            {
                var failResult = new ActionResult
                {
                    ActionId = action.Id,
                    Success = false,
                    MechanicalSummary = validation.FailureReason
                };

                // Ask narrator for a fun fizzle description
                var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, 3, ct);
                var fizzleNarration = await _narrator.ProcessFreeFormAsync(player, room,
                    $"I try to cast {spellName} but fail because: {validation.FailureReason}. Describe the comedic fizzle.",
                    recentStory, ct);
                failResult.Narration = fizzleNarration.Narration;
                return failResult;
            }
        }

        // ── Not in registry — improvised spell with power budget ────
        return await CastImprovisedSpellAsync(player, spellName, target, room, action, ct);
    }

    private async Task<ActionResult> CastRegisteredSpellAsync(
        PlayerCharacter player, SpellDefinition spell, string? target,
        Room room, GameAction action, CancellationToken ct)
    {
        var result = new ActionResult { ActionId = action.Id, Success = true };
        var mechanics = new List<string>();

        // Deduct mana
        var oldMp = player.Mp;
        player.Mp -= spell.ManaCost;
        result.StateChanges.Add(new StateChange
        {
            EntityType = "player", EntityId = player.Id,
            Property = "mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString()
        });
        mechanics.Add($"MP: {oldMp} -> {player.Mp} (-{spell.ManaCost})");

        // Resolve damage
        if (!string.IsNullOrEmpty(spell.DamageDice))
        {
            var statMod = player.GetModifier(spell.DamageStat ?? "int");
            var damageRoll = _dice.RollDamage(spell.DamageDice, statMod);
            mechanics.Add($"Damage: {damageRoll.Expression} + {statMod} = {damageRoll.Total}");

            // Apply damage to target NPC if specified
            if (!string.IsNullOrEmpty(target))
            {
                var targetNpc = room.Npcs.FirstOrDefault(n =>
                    n.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
                if (targetNpc is not null && targetNpc.Hp.HasValue)
                {
                    var oldHp = targetNpc.Hp.Value;
                    targetNpc.Hp = Math.Max(0, targetNpc.Hp.Value - damageRoll.Total);
                    mechanics.Add($"{targetNpc.Name} HP: {oldHp} -> {targetNpc.Hp}");
                    result.StateChanges.Add(new StateChange
                    {
                        EntityType = "npc", EntityId = targetNpc.Id,
                        Property = "hp", OldValue = oldHp.ToString(), NewValue = targetNpc.Hp.Value.ToString()
                    });

                    if (targetNpc.Hp <= 0)
                        mechanics.Add($"{targetNpc.Name} has been defeated!");
                }
            }
        }

        // Resolve healing
        if (!string.IsNullOrEmpty(spell.HealDice))
        {
            var healRoll = _dice.Roll(spell.HealDice, "Spell healing");
            var oldHp = player.Hp;
            player.Hp = Math.Min(player.MaxHp, player.Hp + healRoll.Total);
            mechanics.Add($"Healed: {healRoll.Total} HP ({oldHp} -> {player.Hp})");
            result.StateChanges.Add(new StateChange
            {
                EntityType = "player", EntityId = player.Id,
                Property = "hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString()
            });
        }

        // Status effect
        if (!string.IsNullOrEmpty(spell.StatusEffect))
        {
            mechanics.Add($"Applied: {spell.StatusEffect} ({spell.Duration} turns)");
        }

        result.MechanicalSummary = $"**{spell.Name}** ({spell.School})\n" + string.Join("\n", mechanics);

        // Narrate the successful cast
        var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, 3, ct);
        var narration = await _narrator.ProcessFreeFormAsync(player, room,
            $"I cast {spell.Name}{(target is not null ? $" at {target}" : "")}. Mechanical result: {result.MechanicalSummary}",
            recentStory, ct);
        result.Narration = narration.Narration;

        // Save state
        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);

        return result;
    }

    private async Task<ActionResult> CastImprovisedSpellAsync(
        PlayerCharacter player, string spellName, string? target,
        Room room, GameAction action, CancellationToken ct)
    {
        // Determine the player's power budget
        var powerCap = _registry?.GetImprovisedSpellCap(player.Class, player.Level) ?? Math.Max(1, (player.Level + 1) / 2);

        _logger.LogInformation("Improvised spell '{Spell}' by {Player} (Lv.{Level} {Class}). Power cap: {Cap}",
            spellName, player.Name, player.Level, player.Class, powerCap);

        // Ask the LLM to evaluate the improvised spell
        var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, 3, ct);
        var evalResult = await _narrator.EvaluateImprovisedSpellAsync(
            player, room, spellName, target, powerCap, recentStory, ct);

        var result = new ActionResult
        {
            ActionId = action.Id,
            Success = evalResult.Success,
            Narration = evalResult.Narration
        };

        if (evalResult.Success)
        {
            // Apply mana cost
            if (evalResult.ManaCost > 0)
            {
                var oldMp = player.Mp;
                player.Mp = Math.Max(0, player.Mp - evalResult.ManaCost);
                result.StateChanges.Add(new StateChange
                {
                    EntityType = "player", EntityId = player.Id,
                    Property = "mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString()
                });
            }

            // Apply stat changes from the evaluation
            foreach (var (stat, delta) in evalResult.StatChanges)
            {
                switch (stat.ToLowerInvariant())
                {
                    case "hp":
                        var oldHp = player.Hp;
                        player.Hp = Math.Clamp(player.Hp + delta, 0, player.MaxHp);
                        result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() });
                        break;
                    case "gold":
                        var oldGold = player.Gold;
                        player.Gold = Math.Max(0, player.Gold + delta);
                        result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() });
                        break;
                }
            }

            // Apply damage to target NPC
            if (evalResult.Damage > 0 && !string.IsNullOrEmpty(target))
            {
                var targetNpc = room.Npcs.FirstOrDefault(n =>
                    n.Name.Contains(target, StringComparison.OrdinalIgnoreCase));
                if (targetNpc is not null && targetNpc.Hp.HasValue)
                {
                    var oldHp = targetNpc.Hp.Value;
                    targetNpc.Hp = Math.Max(0, targetNpc.Hp.Value - evalResult.Damage);
                    result.StateChanges.Add(new StateChange
                    {
                        EntityType = "npc", EntityId = targetNpc.Id,
                        Property = "hp", OldValue = oldHp.ToString(), NewValue = targetNpc.Hp.Value.ToString()
                    });
                }
            }

            // Apply healing
            if (evalResult.Healing > 0)
            {
                var oldHp = player.Hp;
                player.Hp = Math.Min(player.MaxHp, player.Hp + evalResult.Healing);
                result.StateChanges.Add(new StateChange
                {
                    EntityType = "player", EntityId = player.Id,
                    Property = "hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString()
                });
            }

            result.MechanicalSummary = $"**Improvised: {spellName}** (Power {evalResult.PowerLevel}/{powerCap})"
                + (evalResult.ManaCost > 0 ? $" | MP: -{evalResult.ManaCost}" : "")
                + (evalResult.Damage > 0 ? $" | Damage: {evalResult.Damage}" : "")
                + (evalResult.Healing > 0 ? $" | Healed: {evalResult.Healing}" : "");
        }
        else
        {
            // Fizzle — still costs some mana for the attempt
            var fizzleCost = Math.Max(1, evalResult.ManaCost / 2);
            if (player.Mp >= fizzleCost)
            {
                var oldMp = player.Mp;
                player.Mp -= fizzleCost;
                result.StateChanges.Add(new StateChange
                {
                    EntityType = "player", EntityId = player.Id,
                    Property = "mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString()
                });
            }

            result.MechanicalSummary = $"**Fizzle: {spellName}** (Power {evalResult.PowerLevel} exceeds your cap of {powerCap})"
                + (fizzleCost > 0 ? $" | MP wasted: -{fizzleCost}" : "");
        }

        player.LastActiveAt = DateTimeOffset.UtcNow;
        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);

        return result;
    }

    private async Task<ActionResult> ProcessFreeFormActionAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        _logger.LogInformation("Routing free-form action for {PlayerId} in room {RoomId}: {RawInput}", player.Id, player.CurrentRoomId, action.RawInput);

        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
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

        // Apply stat changes — only HP, MP, gold, XP are allowed from free-form.
        // Gold and XP gains are passed through (the LLM prompt handles legitimacy),
        // but we log large values for monitoring.
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
                    if (delta > 50)
                        _logger.LogWarning("Large gold change from free-form: {Delta} for player {PlayerId} (action: {Action})", delta, player.Id, action.RawInput);
                    result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() });
                    break;
                case "xp":
                    var oldXp = player.Xp;
                    player.Xp = Math.Max(0, player.Xp + delta);
                    result.XpGained += Math.Max(0, delta);
                    if (delta > 100)
                        _logger.LogWarning("Large XP change from free-form: {Delta} for player {PlayerId}", delta, player.Id);
                    result.StateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "xp", OldValue = oldXp.ToString(), NewValue = player.Xp.ToString() });
                    break;
                default:
                    // Block arbitrary stat modifications (str, dex, etc.) from free-form
                    _logger.LogWarning("Anti-cheat: blocked stat change '{Stat}' = {Delta} from free-form for player {PlayerId}", stat, delta, player.Id);
                    break;
            }
        }

        // Apply inventory changes — only allow items that exist in the room or a shopkeeper's inventory
        foreach (var change in freeForm.InventoryChanges)
        {
            if (string.Equals(change.Action, "add", StringComparison.OrdinalIgnoreCase))
            {
                // Anti-cheat: only allow adding items that exist in a known source
                var resolved = TryResolveKnownItem(change.ItemName, room);
                if (resolved is null)
                {
                    _logger.LogWarning("Anti-cheat: blocked item addition '{ItemName}' — not found in room or shop inventory for player {PlayerId}",
                        change.ItemName, player.Id);
                    continue; // Skip this item — it doesn't exist in a legitimate source
                }
                resolved.Quantity = Math.Max(1, change.Quantity);
                player.Inventory.Add(resolved);
                result.ItemsGained.Add(resolved);
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
        if (normalizedTarget is not "gold" and not "coin" and not "coins" and not "gold coin" and not "gold coins"
            and not "gp" and not "gold piece" and not "gold pieces" and not "money")
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
            IsTwoHanded = item.IsTwoHanded,
            Effect = item.Effect,
            StatBonuses = new Dictionary<string, int>(item.StatBonuses)
        };

    /// <summary>
    /// Tries to find a matching item in the room's items or any shopkeeper's inventory.
    /// Returns a cloned copy with full stats if found, or null if not found.
    /// This prevents the LLM from creating items out of thin air.
    /// </summary>
    private static InventoryItem? TryResolveKnownItem(string itemName, Room room)
    {
        // Check room floor items
        var roomItem = FindNamedEntity(room.Items, i => i.Name, itemName);
        if (roomItem is not null)
            return CloneInventoryItem(roomItem, 1);

        // Check shopkeeper inventories in the room
        foreach (var npc in room.Npcs.Where(n => n.IsShopkeeper))
        {
            var shopItem = FindNamedEntity(npc.ShopInventory, i => i.Name, itemName);
            if (shopItem is not null)
                return CloneInventoryItem(shopItem, 1);
        }

        return null;
    }

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

    private static string FormatEquippedItem(InventoryItem item)
    {
        var label = item.Type.ToString();
        var bonuses = item.StatBonuses.Count > 0
            ? " (" + string.Join(", ", item.StatBonuses.Select(kv => $"{kv.Key.ToUpper()} {kv.Value:+0;-0}")) + ")"
            : "";
        return $"{label}: {item.Name}{bonuses}";
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

    /// <summary>
    /// Orders the standard array values so the highest stats go to the class's primary attributes.
    /// e.g. a Mage gets INT=15, WIS=14; a Warrior gets STR=15, CON=14, etc.
    /// </summary>
    private static int[] OrderStandardArrayByClass(string className, List<string> attributeKeys, int[] standardArray)
    {
        // Define priority stat order per class archetype
        var classPriority = className.ToLowerInvariant() switch
        {
            "warrior" or "fighter" or "barbarian" or "paladin" or "knight" => new[] { "str", "con", "dex", "wis", "cha", "int", "luck" },
            "mage" or "wizard" or "sorcerer" or "warlock" => new[] { "int", "wis", "con", "dex", "cha", "str", "luck" },
            "rogue" or "thief" or "assassin" => new[] { "dex", "str", "con", "wis", "cha", "int", "luck" },
            "ranger" or "hunter" => new[] { "dex", "wis", "str", "con", "cha", "int", "luck" },
            "cleric" or "priest" or "healer" or "monk" => new[] { "wis", "con", "str", "cha", "dex", "int", "luck" },
            "bard" or "skald" => new[] { "cha", "dex", "int", "con", "wis", "str", "luck" },
            _ => new[] { "str", "dex", "con", "int", "wis", "cha", "luck" } // generic fallback
        };

        var sorted = new int[attributeKeys.Count];
        var arrayValues = standardArray.OrderByDescending(v => v).ToArray();

        // Map priority order to attribute key positions
        int valueIndex = 0;
        foreach (var stat in classPriority)
        {
            var keyIndex = attributeKeys.FindIndex(k => k.Equals(stat, StringComparison.OrdinalIgnoreCase));
            if (keyIndex >= 0 && valueIndex < arrayValues.Length)
            {
                sorted[keyIndex] = arrayValues[valueIndex++];
            }
        }

        // Fill any remaining stats (in case attribute keys don't match priority list)
        for (int i = 0; i < sorted.Length; i++)
        {
            if (sorted[i] == 0 && valueIndex < arrayValues.Length)
                sorted[i] = arrayValues[valueIndex++];
        }

        return sorted;
    }

    private static readonly HashSet<string> CasterClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mage", "Wizard", "Sorcerer", "Warlock", "Druid", "Cleric", "Necromancer", "Bard"
    };

    private static bool IsCasterClass(string? className) =>
        !string.IsNullOrEmpty(className) && CasterClasses.Contains(className);

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
        "goodbye", "bye", "farewell", "leave", "walk away", "end conversation", "nevermind", "never mind",
        "leave conversation", "stop talking", "done talking", "go away", "i'm done", "im done"
    };

    private static bool IsExitPhrase(string input)
    {
        var trimmed = input.Trim();
        // Exact match first
        if (ConversationExitPhrases.Contains(trimmed))
            return true;
        // Also check if the input starts with any exit phrase (e.g. "leave conversation with")
        foreach (var phrase in ConversationExitPhrases)
        {
            if (trimmed.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly HashSet<string> DungeonRequestKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dungeon", "adventure", "quest", "challenge", "explore", "delve",
        "expedition", "cave", "crypt", "tomb", "lair", "ruins"
    };

    private static bool IsDungeonRequestPhrase(string input)
    {
        var lower = input.ToLowerInvariant();
        return DungeonRequestKeywords.Any(kw => lower.Contains(kw));
    }

    /// <summary>
    /// Creates a dungeon entrance room connected to the current room.
    /// Returns a message describing the new dungeon exit, or null if creation failed.
    /// </summary>
    private async Task<string?> TryCreateDungeonAsync(PlayerCharacter player, Room room, Npc npc, CancellationToken ct)
    {
        try
        {
            var dungeonId = $"generated_dungeon_{Guid.NewGuid().ToString("N")[..8]}";
            var entrance = await _narrator.GenerateDungeonEntranceAsync(dungeonId, player.Level, room, ct);

            // Wire the entrance back to the source room
            entrance.Exits["back"] = room.Id;
            await _stateManager.SaveRoomAsync(entrance, ct);

            // Add exit from current room to the dungeon
            var exitDirection = PickDungeonExitDirection(room);
            room.Exits[exitDirection] = dungeonId;
            await _stateManager.SaveRoomAsync(room, ct);

            _logger.LogInformation(
                "Dungeon '{DungeonName}' ({DungeonId}) created by {NpcName} for player {PlayerId} (level {Level}). Exit: {Direction}",
                entrance.Name, dungeonId, npc.Name, player.Id, player.Level, exitDirection);

            PublishToWikiInBackground(entrance);

            return $"⚔️ A new dungeon has appeared! **{entrance.Name}** — go **{exitDirection}** to enter.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create dungeon for player {PlayerId}", player.Id);
            return null;
        }
    }

    /// <summary>Picks an exit direction for the dungeon that doesn't conflict with existing exits.</summary>
    private static string PickDungeonExitDirection(Room room)
    {
        // Preferred directions for dungeon entrances
        string[] preferred = ["down", "underground", "north", "east", "west", "south"];
        foreach (var dir in preferred)
        {
            if (!room.Exits.ContainsKey(dir))
                return dir;
        }
        // Fallback: use a unique descriptive name
        return "dungeon entrance";
    }

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
            InteractionMode.Trading => await ProcessTradingTurnAsync(player, action, ct),
            _ => null
        };
    }

    private async Task<ActionResult?> ProcessConversationTurnAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
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
                await EnterCombatAsync(player, room, [npc], ct);
                await _stateManager.SavePlayerAsync(player, ct);
                return await ProcessCombatTurnAsync(player, action, ct);
            }

            return null; // Fall through to normal attack processing
        }

        // Explicit goodbye / leave
        if (IsExitPhrase(action.RawInput))
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

        // Normal conversation turn —  everything the player says goes to the AI as dialogue
        {
            // Check for social skill intent and roll if detected
            var socialCheck = TryRollSocialCheck(player, npc, action.RawInput);
            var promptInput = action.RawInput;
            if (socialCheck is not null)
            {
                // Prepend the roll result so the LLM knows the mechanical outcome
                promptInput = $"{action.RawInput}\n[Social check: {socialCheck.SkillName} ({socialCheck.StatUsed.ToUpperInvariant()} {socialCheck.Roll.Modifier:+0;-0}) — rolled {socialCheck.Roll.Total} vs DC {socialCheck.DC} = {(socialCheck.Succeeded ? "SUCCESS" : "FAILURE")}]";
                player.Interaction.AppendContext($"[{socialCheck.SkillName} check: {socialCheck.Roll.Total} vs DC {socialCheck.DC} — {(socialCheck.Succeeded ? "success" : "failure")}]");
            }

            var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, npc, player.Interaction, promptInput, ct);
            ApplyInteractionUpdate(player, room, npc, freeForm);

            if (freeForm.InteractionUpdate?.Mode == InteractionMode.Explore)
                player.Interaction.Reset();

            if (freeForm.CombatInitiated && npc.Hp.HasValue && npc.Hp > 0)
            {
                player.Interaction.Reset();
                await EnterCombatAsync(player, room, [npc], ct);
            }

            await _stateManager.SavePlayerAsync(player, ct);
            await _stateManager.SaveRoomAsync(room, ct);

            // --- Dungeon generation: if an NPC with dungeon knowledge agrees to create one ---
            string? dungeonCreatedMessage = null;
            if (npc.KnowledgeScopes.Any(k => k.Equals("dungeons", StringComparison.OrdinalIgnoreCase))
                && IsDungeonRequestPhrase(action.RawInput)
                && !room.Exits.Values.Any(v => v.Contains("_dungeon_", StringComparison.OrdinalIgnoreCase)))
            {
                dungeonCreatedMessage = await TryCreateDungeonAsync(player, room, npc, ct);
            }

            var mechanicalLine = $"[Conversation with {npc.Name}, turn {player.Interaction.TurnCount}]";
            if (socialCheck is not null)
                mechanicalLine = $"[{socialCheck.SkillName} check: {socialCheck.Roll.Total} vs DC {socialCheck.DC} — {(socialCheck.Succeeded ? "SUCCESS" : "FAILURE")}]";
            if (dungeonCreatedMessage is not null)
                mechanicalLine += $" {dungeonCreatedMessage}";

            var narration = freeForm.Narration;
            if (dungeonCreatedMessage is not null)
                narration += $"\n\n{dungeonCreatedMessage}";

            var result = new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = freeForm.Success,
                MechanicalSummary = mechanicalLine,
                Narration = narration,
                DiceRolls = socialCheck is not null ? [socialCheck.Roll] : [],
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
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
        {
            player.Interaction.Reset();
            return null;
        }

        // Get combat state (multi-enemy tracking)
        var combat = await _stateManager.GetCombatStateAsync(room.Id, ct);
        var enemies = room.Npcs.Where(n => combat?.TurnOrder.Any(t => !t.IsPlayer && t.Id == n.Id) == true).ToList();

        // Fallback: if no CombatState, fight the interaction target
        if (combat is null || enemies.Count == 0)
        {
            var singleEnemy = FindNamedEntity(room.Npcs, n => n.Name, player.Interaction.Target);
            if (singleEnemy is null)
            {
                player.Interaction.Reset();
                await _stateManager.SavePlayerAsync(player, ct);
                return new ActionResult
                {
                    ActionId = action.Id, RawInput = action.RawInput, Success = true,
                    MechanicalSummary = "Your opponent is no longer here. Combat ends.",
                    InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
                };
            }
            enemies = [singleEnemy];
        }

        // --- Flee attempt ---
        var rawLower = action.RawInput.Trim().ToLowerInvariant();
        if (rawLower is "flee" or "run" or "escape" or "run away")
        {
            int totalOppDamage = 0;
            var diceRolls = new List<DiceRoll>();
            foreach (var enemy in enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0))
            {
                if (enemy.DamageDice is not null)
                {
                    var oppRoll = _dice.Roll(enemy.DamageDice, $"{enemy.Name} opportunity attack");
                    int oppDmg = Math.Max(1, oppRoll.Total);
                    totalOppDamage += oppDmg;
                    diceRolls.Add(oppRoll);
                }
            }
            player.Hp = Math.Max(0, player.Hp - totalOppDamage);
            player.Interaction.Reset();

            string? escapeDirection = room.Exits.Keys.FirstOrDefault();
            if (escapeDirection is not null)
                player.CurrentRoomId = room.Exits[escapeDirection];

            if (combat is not null)
                await _stateManager.RemoveCombatStateAsync(room.Id, ct);

            await _stateManager.SavePlayerAsync(player, ct);

            var fleeResult = new ActionResult
            {
                ActionId = action.Id, RawInput = action.RawInput, Success = true,
                MechanicalSummary = totalOppDamage > 0
                    ? $"You flee! The enemies strike you for {totalOppDamage} total damage as you escape."
                    : "You flee from combat!",
                DiceRolls = diceRolls,
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "fled" },
                StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", NewValue = player.Hp.ToString() }]
            };
            await PersistInteractionStoryEntry(player, action, fleeResult, ct);
            return fleeResult;
        }

        // --- Player's attack turn ---
        // Determine target: explicit from input, or first living enemy
        var targetName = action.Target;
        Npc? target = null;
        if (!string.IsNullOrWhiteSpace(targetName))
            target = FindNamedEntity(enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0), n => n.Name, targetName);
        target ??= enemies.FirstOrDefault(e => e.Hp.HasValue && e.Hp.Value > 0);

        if (target is null)
        {
            // All enemies already dead — victory
            player.Interaction.Reset();
            if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, ct);
            await _stateManager.SavePlayerAsync(player, ct);
            return new ActionResult
            {
                ActionId = action.Id, RawInput = action.RawInput, Success = true,
                MechanicalSummary = "All enemies have been defeated!",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "victory" }
            };
        }

        var allDiceRolls = new List<DiceRoll>();
        var allStateChanges = new List<StateChange>();
        var mechanicalParts = new List<string>();
        int totalXp = 0;
        int totalGold = 0;
        var totalLoot = new List<InventoryItem>();

        // Player attack
        var weapon = player.Equipment.Weapon;
        string attackStat = weapon?.DamageStat ?? _rules.Combat.MeleeStat;
        int attackMod = player.GetModifier(attackStat);
        var attackRoll = _dice.RollAttack(attackMod);
        int targetDefense = target.Defense ?? _rules.Combat.BaseDefense;

        // Determine outcome tier
        var outcome = ProbabilityEngine.DetermineOutcome(attackRoll, targetDefense);
        attackRoll.Outcome = outcome;
        attackRoll.TargetNumber = targetDefense;
        allDiceRolls.Add(attackRoll);

        if (outcome == RollOutcome.CriticalMiss)
        {
            mechanicalParts.Add($"You fumble your attack against {target.Name}! Critical miss!");
        }
        else if (outcome == RollOutcome.Miss)
        {
            mechanicalParts.Add($"You attack {target.Name} (rolled {attackRoll.Total} vs defense {targetDefense}) and miss!");
        }
        else
        {
            string damageDice = weapon?.DamageDice ?? "1d4";
            int damageMod = player.GetModifier(attackStat);
            var damageRoll = _dice.RollDamage(damageDice, damageMod);
            int totalDamage = outcome switch
            {
                RollOutcome.CriticalHit => damageRoll.Total * _rules.Combat.CriticalMultiplier,
                RollOutcome.GlancingHit => Math.Max(1, damageRoll.Total / 2),
                _ => damageRoll.Total
            };
            totalDamage = Math.Max(1, totalDamage);
            allDiceRolls.Add(damageRoll);

            if (target.Hp.HasValue)
            {
                var oldHp = target.Hp.Value;
                target.Hp = Math.Max(0, target.Hp.Value - totalDamage);
                allStateChanges.Add(new StateChange { EntityType = "Npc", EntityId = target.Id, Property = "Hp", OldValue = oldHp.ToString(), NewValue = target.Hp.Value.ToString() });
            }

            string outcomeText = outcome switch
            {
                RollOutcome.CriticalHit => " **CRITICAL HIT!**",
                RollOutcome.GlancingHit => " (glancing blow)",
                _ => ""
            };
            mechanicalParts.Add($"You hit {target.Name} for {totalDamage} damage!{outcomeText}");

            // Check if target died
            if (target.Hp.HasValue && target.Hp.Value <= 0)
            {
                mechanicalParts.Add($"{target.Name} has been defeated!");
                int xpGain = target.Level * 10;
                totalXp += xpGain;

                if (_dice.Roll("1d100", "Loot check").Total <= (int)(_rules.Loot.EnemyDropChance * 100))
                {
                    var goldRoll = _dice.Roll("1d20", "Gold drop");
                    int goldAmount = goldRoll.Total + (target.Level * 2);
                    totalGold += goldAmount;
                }

                room.Npcs.Remove(target);
                combat?.TurnOrder.RemoveAll(t => t.Id == target.Id);
                PublishEventToWikiInBackground(target.Id, "npc", $"{target.Name} Defeated",
                    $"{player.Name} defeated {target.Name} (Level {target.Level}, {target.Faction}) in combat.", room.Id);
            }
        }

        // --- Enemy turns ---
        var survivingEnemies = enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)).ToList();
        foreach (var enemy in survivingEnemies)
        {
            int enemyAttackBonus = enemy.AttackBonus ?? 0;
            var enemyAttackRoll = _dice.RollAttack(enemyAttackBonus);
            allDiceRolls.Add(enemyAttackRoll);

            if (enemyAttackRoll.IsFumble)
            {
                mechanicalParts.Add($"{enemy.Name} fumbles their attack!");
                continue;
            }

            if (enemyAttackRoll.Total < player.Defense && !enemyAttackRoll.IsCritical)
            {
                mechanicalParts.Add($"{enemy.Name} attacks you (rolled {enemyAttackRoll.Total} vs {player.Defense}) and misses!");
                continue;
            }

            string enemyDamageDice = enemy.DamageDice ?? "1d4";
            var enemyDamageRoll = _dice.Roll(enemyDamageDice, $"{enemy.Name} damage");
            int enemyDamage = enemyAttackRoll.IsCritical ? enemyDamageRoll.Total * _rules.Combat.CriticalMultiplier : enemyDamageRoll.Total;
            enemyDamage = Math.Max(1, enemyDamage);
            allDiceRolls.Add(enemyDamageRoll);

            var oldPlayerHp = player.Hp;
            player.Hp = Math.Max(0, player.Hp - enemyDamage);
            allStateChanges.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", OldValue = oldPlayerHp.ToString(), NewValue = player.Hp.ToString() });

            string enemyCrit = enemyAttackRoll.IsCritical ? " **CRITICAL!**" : "";
            mechanicalParts.Add($"{enemy.Name} hits you for {enemyDamage} damage!{enemyCrit}");

            if (player.Hp <= 0)
            {
                mechanicalParts.Add("You have been defeated!");
                break;
            }
        }

        // --- Check for victory or defeat ---
        var allEnemiesAlive = enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)).ToList();
        string combatStatus = "ongoing";

        if (player.Hp <= 0)
        {
            combatStatus = "defeat";
            player.Interaction.Reset();
            if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, ct);
            PublishEventToWikiInBackground(player.Id, "player", $"{player.Name} Defeated",
                $"{player.Name} was defeated in multi-enemy combat.", room.Id);
        }
        else if (allEnemiesAlive.Count == 0)
        {
            combatStatus = "victory";
            player.Xp += totalXp;
            player.Gold += totalGold;
            player.Interaction.Reset();
            if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, ct);

            if (totalXp > 0) mechanicalParts.Add($"You gain {totalXp} XP!");
            if (totalGold > 0) mechanicalParts.Add($"You find {totalGold} gold!");
        }
        else if (combat is not null)
        {
            // Update combat state for next round
            combat.RoundNumber++;
            var playerParticipant = combat.TurnOrder.FirstOrDefault(t => t.IsPlayer);
            if (playerParticipant is not null) playerParticipant.Hp = player.Hp;
            foreach (var ep in combat.TurnOrder.Where(t => !t.IsPlayer))
            {
                var npc = enemies.FirstOrDefault(e => e.Id == ep.Id);
                if (npc?.Hp.HasValue == true) ep.Hp = npc.Hp.Value;
            }
            await _stateManager.SaveCombatStateAsync(combat, ct);
        }

        var summary = string.Join("\n", mechanicalParts);
        var combatResult = new ActionResult
        {
            ActionId = action.Id,
            RawInput = action.RawInput,
            Success = true,
            MechanicalSummary = summary,
            DiceRolls = allDiceRolls,
            StateChanges = allStateChanges,
            XpGained = totalXp,
            GoldChange = totalGold,
            InteractionUpdate = new InteractionUpdate
            {
                Mode = combatStatus == "ongoing" ? InteractionMode.Combat : InteractionMode.Explore,
                CombatStatus = combatStatus
            }
        };

        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);
        await PersistInteractionStoryEntry(player, action, combatResult, ct);
        return combatResult;
    }

    /// <summary>Handles player death: respawn at tavern with penalties.</summary>
    private async Task<(string summary, List<StateChange> changes)> HandlePlayerDeathAsync(
        PlayerCharacter player, Room room, string defeatedBy, CancellationToken ct)
    {
        var oldHp = player.Hp;
        var oldMp = player.Mp;
        var oldGold = player.Gold;
        var oldRoom = player.CurrentRoomId;

        // Respawn at 50% HP/MP
        player.Hp = Math.Max(1, player.MaxHp / 2);
        player.Mp = Math.Max(0, player.MaxMp / 2);

        // Death tax: lose 25% gold
        var goldLost = player.Gold / 4;
        player.Gold = Math.Max(0, player.Gold - goldLost);

        // Teleport to spawn
        player.CurrentRoomId = "spawn";
        player.Interaction.Reset();

        // Memory flag on the enemy
        var killer = room.Npcs.FirstOrDefault(n => n.Name == defeatedBy);
        killer?.DispositionState.MemoryFlags.Add("defeated-player");

        var changes = new List<StateChange>
        {
            new() { EntityType = "Player", EntityId = player.Id, Property = "Hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() },
            new() { EntityType = "Player", EntityId = player.Id, Property = "Mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString() },
            new() { EntityType = "Player", EntityId = player.Id, Property = "Gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() },
            new() { EntityType = "Player", EntityId = player.Id, Property = "CurrentRoomId", OldValue = oldRoom, NewValue = "spawn" }
        };

        var summary = $"You have been defeated by {defeatedBy}! You wake up in The Rusted Flagon.\n" +
            $"\U0001F480 You lost {goldLost} gold.\n" +
            $"\u2764\uFE0F {player.Hp}/{player.MaxHp}  \u2728 {player.Mp}/{player.MaxMp}  \U0001F4B0 {player.Gold}g";

        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);

        return (summary, changes);
    }

    private async Task EnterCombatAsync(PlayerCharacter player, Room room, List<Npc> enemies, CancellationToken ct)
    {
        var primaryEnemy = enemies[0];
        player.Interaction = new InteractionState
        {
            Mode = InteractionMode.Combat,
            Target = primaryEnemy.Name,
            CanLeave = false,
            LeaveConsequence = "flee_penalty"
        };

        var allNames = string.Join(", ", enemies.Select(e => e.Name));
        player.Interaction.AppendContext($"Combat initiated with {allNames}.");

        // Build CombatState with initiative
        var combat = new CombatState
        {
            RoomId = room.Id,
            Phase = CombatPhase.PlayerTurn,
            RoundNumber = 1,
            IsActive = true
        };

        // Player initiative
        int playerDexMod = player.GetModifier("dex");
        var playerInit = _dice.RollInitiative(playerDexMod);
        combat.TurnOrder.Add(new CombatParticipant
        {
            Id = player.Id, Name = player.Name, IsPlayer = true,
            Initiative = playerInit.Total, Hp = player.Hp, MaxHp = player.MaxHp
        });

        // Enemy initiative
        foreach (var enemy in enemies)
        {
            int enemyDexMod = (enemy.AttackBonus ?? 0) / 2;
            var enemyInit = _dice.RollInitiative(enemyDexMod);
            combat.TurnOrder.Add(new CombatParticipant
            {
                Id = enemy.Id, Name = enemy.Name, IsPlayer = false,
                Initiative = enemyInit.Total, Hp = enemy.Hp ?? 10, MaxHp = enemy.MaxHp ?? 10
            });
        }

        // Sort by initiative descending
        combat.TurnOrder = combat.TurnOrder.OrderByDescending(p => p.Initiative).ToList();
        combat.CurrentTurnIndex = 0;

        await _stateManager.SaveCombatStateAsync(combat, ct);
    }

    private static void ApplyInteractionUpdate(PlayerCharacter player, Room room, Npc npc, FreeFormResponse freeForm)
    {
        var update = freeForm.InteractionUpdate;
        if (update is null) return;

        // Rich disposition takes priority —  sync flat string from it
        if (update.DispositionState is not null)
        {
            npc.DispositionState = update.DispositionState;
            npc.DispositionState.LastUpdated = DateTimeOffset.UtcNow;
            npc.Disposition = npc.DispositionState.ToFlatDisposition();
        }
        else if (update.NpcDisposition is not null)
        {
            npc.Disposition = update.NpcDisposition;
        }

        // Apply memory flags from the LLM (additive —  never remove via this path)
        foreach (var flag in update.MemoryFlags)
        {
            if (!string.IsNullOrWhiteSpace(flag) && !npc.DispositionState.MemoryFlags.Contains(flag, StringComparer.OrdinalIgnoreCase))
                npc.DispositionState.MemoryFlags.Add(flag);
        }

        // Faction ripple: shift mood of same-faction NPCs in the room
        if (update.FactionMoodShift != 0 && !string.IsNullOrWhiteSpace(npc.Faction) && npc.Faction != "neutral")
        {
            var factionPeers = room.Npcs
                .Where(n => n.Id != npc.Id && string.Equals(n.Faction, npc.Faction, StringComparison.OrdinalIgnoreCase));

            foreach (var peer in factionPeers)
            {
                // Peers get 50% of the faction shift (dampened ripple)
                var peerShift = update.FactionMoodShift / 2;
                peer.DispositionState.Intensity = Math.Clamp(peer.DispositionState.Intensity + peerShift, 0, 100);
                peer.DispositionState.LastUpdated = DateTimeOffset.UtcNow;
                peer.Disposition = peer.DispositionState.ToFlatDisposition();
            }
        }

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

    // --- Social skill checks ---

    private record SocialCheckResult(string SkillName, string StatUsed, DiceRoll Roll, int DC, bool Succeeded);

    /// <summary>
    /// Built-in social skill definitions that always work, even if the DM strips the YAML config.
    /// If social_skills is defined in rules, those take priority. Otherwise these kick in.
    /// Stats resolve via PlayerCharacter.GetModifier() which returns +0 for unknown stats (equivalent to 10, average).
    /// </summary>
    private static readonly Dictionary<string, SocialSkillConfig> DefaultSocialSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        ["persuade"] = new() { Stat = "cha", Keywords = ["persuade", "convince", "charm", "flatter", "flirt", "seduce", "sweet-talk", "beg", "plead", "appeal", "compliment", "woo", "barter", "haggle", "negotiate"] },
        ["intimidate"] = new() { Stat = "str", AltStat = "cha", Keywords = ["intimidate", "threaten", "scare", "demand", "bully", "menace", "glare", "growl", "loom"] },
        ["deceive"] = new() { Stat = "cha", Keywords = ["lie", "bluff", "deceive", "trick", "con", "fool", "mislead", "fake", "pretend"] },
        ["insight"] = new() { Stat = "wis", Keywords = ["read", "gauge", "sense", "detect", "study", "observe", "scrutinize", "size up"] }
    };

    /// <summary>
    /// Scans the player's raw input for social intent keywords. If detected, rolls d20 + stat mod
    /// against a DC derived from the NPC's disposition intensity (friendlier = easier).
    /// Returns null if no social intent was detected.
    /// Falls back to hardcoded defaults if the DM hasn't configured social_skills in the rules YAML.
    /// Stat resolution gracefully returns +0 (average) for any stat not on the player sheet.
    /// </summary>
    private SocialCheckResult? TryRollSocialCheck(PlayerCharacter player, Npc npc, string rawInput)
    {
        // Use configured social skills if available, otherwise fall back to built-in defaults
        var socialSkills = _rules.SkillChecks.SocialSkills.Count > 0
            ? _rules.SkillChecks.SocialSkills
            : DefaultSocialSkills;

        var input = rawInput.ToLowerInvariant();
        foreach (var (skillName, config) in socialSkills)
        {
            if (!config.Keywords.Any(kw => input.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Pick the better stat if an alt is available (e.g. intimidate: STR or CHA)
            // GetModifier returns +0 for unknown stats —  equivalent to stat value 10 (average)
            int statMod = player.GetModifier(config.Stat);
            string statUsed = config.Stat;
            if (config.AltStat is not null)
            {
                int altMod = player.GetModifier(config.AltStat);
                if (altMod > statMod)
                {
                    statMod = altMod;
                    statUsed = config.AltStat;
                }
            }

            var roll = _dice.RollSkillCheck(skillName, statMod);
            int dc = CalculateSocialDC(npc, skillName);

            // Determine outcome tier for skill checks
            var skillOutcome = ProbabilityEngine.DetermineSkillOutcome(roll, dc);
            roll.Outcome = skillOutcome;
            roll.TargetNumber = dc;

            bool succeeded = skillOutcome >= RollOutcome.GlancingHit;
            return new SocialCheckResult(skillName, statUsed, roll, dc, succeeded);
        }

        return null;
    }

    /// <summary>
    /// Calculates the DC for a social check based on NPC disposition, level, personality traits,
    /// faction role, hostility, and memory flags. Higher-level NPCs are harder to influence.
    /// Personality keywords make certain skills easier or harder (a brave guard resists intimidation,
    /// a naive barmaid is easier to deceive, etc.).
    /// </summary>
    private int CalculateSocialDC(Npc npc, string skillName)
    {
        int easy = _rules.SkillChecks.DifficultyClasses.GetValueOrDefault("easy", 8);
        int medium = _rules.SkillChecks.DifficultyClasses.GetValueOrDefault("medium", 12);
        int hard = _rules.SkillChecks.DifficultyClasses.GetValueOrDefault("hard", 16);
        int veryHard = _rules.SkillChecks.DifficultyClasses.GetValueOrDefault("very_hard", 20);

        // --- Base DC from disposition: friendlier NPCs are easier to persuade ---
        int dc = npc.DispositionState.Intensity switch
        {
            >= 70 => easy,
            >= 50 => medium - 2,
            >= 30 => medium,
            >= 15 => hard,
            _ => veryHard
        };

        // --- Level scaling: experienced NPCs are harder to influence ---
        // +1 DC per level above 1 (a level 5 guard is +4 harder than a level 1 peasant)
        if (npc.Level > 1)
            dc += npc.Level - 1;

        // --- Hostility: openly hostile NPCs are harder for everything except intimidation ---
        if (npc.IsHostile && !string.Equals(skillName, "intimidate", StringComparison.OrdinalIgnoreCase))
            dc += 3;

        // --- Personality trait modifiers: scan the NPC's personality for relevant keywords ---
        dc += GetPersonalityDCModifier(npc, skillName);

        // --- Memory flags: NPC remembers what you've done ---
        dc += GetMemoryDCModifier(npc.DispositionState.MemoryFlags);

        return Math.Clamp(dc, 3, 25); // floor 3 so nat 20 almost always works, cap 25
    }

    /// <summary>
    /// Scans NPC personality text and faction for traits that make certain social skills
    /// easier or harder. Returns a DC modifier (positive = harder, negative = easier).
    /// </summary>
    private static int GetPersonalityDCModifier(Npc npc, string skillName)
    {
        // Combine personality + faction into one searchable string
        var profile = $"{npc.Personality} {npc.Faction} {npc.Name}".ToLowerInvariant();
        int mod = 0;
        var skill = skillName.ToLowerInvariant();

        // --- PERSUADE / CHARM modifiers ---
        if (skill == "persuade")
        {
            // Harder to charm
            if (HasAny(profile, "gruff", "stern", "cold", "stoic", "aloof", "reserved", "prickly"))
                mod += 2;
            if (HasAny(profile, "married", "devoted", "faithful", "taken", "spouse", "husband", "wife"))
                mod += 3; // committed relationships resist romantic persuasion
            if (HasAny(profile, "stubborn", "obstinate", "unyielding", "hardened", "resolute"))
                mod += 2;
            // Easier to charm
            if (HasAny(profile, "flirty", "flirtatious", "lonely", "romantic", "amorous", "smitten"))
                mod -= 3;
            if (HasAny(profile, "friendly", "warm", "cheerful", "welcoming", "amiable", "sociable"))
                mod -= 2;
            if (HasAny(profile, "naive", "innocent", "trusting", "gullible"))
                mod -= 2;
        }

        // --- INTIMIDATE modifiers ---
        if (skill == "intimidate")
        {
            // Harder to intimidate (trained or brave)
            if (HasAny(profile, "brave", "fearless", "bold", "courageous", "unflappable", "steely"))
                mod += 3;
            if (HasAny(profile, "guard", "soldier", "captain", "sergeant", "knight", "warrior", "veteran", "military"))
                mod += 2; // trained to stand firm
            if (HasAny(profile, "stubborn", "proud", "defiant"))
                mod += 2;
            // Easier to intimidate
            if (HasAny(profile, "cowardly", "nervous", "timid", "meek", "fearful", "anxious", "jittery", "skittish"))
                mod -= 3;
            if (HasAny(profile, "small", "frail", "weak", "elderly", "old"))
                mod -= 2;
            if (HasAny(profile, "merchant", "trader", "shopkeeper", "peddler", "innkeeper", "barmaid", "barkeep"))
                mod -= 1; // civilians are easier to push around
        }

        // --- DECEIVE modifiers ---
        if (skill == "deceive")
        {
            // Harder to fool
            if (HasAny(profile, "perceptive", "shrewd", "suspicious", "paranoid", "sharp", "observant", "skeptical", "wary"))
                mod += 3;
            if (HasAny(profile, "merchant", "trader", "fence", "broker", "con"))
                mod += 2; // seen every trick in the book
            if (HasAny(profile, "wise", "experienced", "worldly"))
                mod += 2;
            // Easier to fool
            if (HasAny(profile, "gullible", "naive", "trusting", "simple", "innocent", "dim", "clueless"))
                mod -= 3;
            if (HasAny(profile, "drunk", "distracted", "absent-minded", "oblivious"))
                mod -= 2;
        }

        // --- INSIGHT modifiers ---
        if (skill == "insight")
        {
            // Harder to read (good at hiding emotions)
            if (HasAny(profile, "stoic", "inscrutable", "poker", "mask", "guarded", "cryptic", "enigmatic"))
                mod += 3;
            if (HasAny(profile, "deceptive", "sly", "cunning", "manipulative", "scheming"))
                mod += 2;
            // Easier to read (wears heart on sleeve)
            if (HasAny(profile, "expressive", "emotional", "hot-headed", "transparent", "open", "loud"))
                mod -= 2;
            if (HasAny(profile, "nervous", "fidgety", "twitchy", "anxious"))
                mod -= 2;
        }

        return mod;
    }

    /// <summary>DC modifiers from NPC memory flags (what they remember about the player).</summary>
    private static int GetMemoryDCModifier(List<string> memoryFlags)
    {
        int mod = 0;
        foreach (var flag in memoryFlags)
        {
            var f = flag.ToLowerInvariant();
            if (f.Contains("insult")) mod += 3;        // offended — harder to influence
            if (f.Contains("betrayal")) mod += 5;       // deep grudge
            if (f.Contains("crime")) mod += 4;          // witnessed crime — distrustful
            if (f.Contains("romance")) mod -= 4;        // in love — very receptive
            if (f.Contains("friendship")) mod -= 2;     // friends — easier
            if (f.Contains("helped")) mod -= 2;         // gratitude
            if (f.Contains("flirted")) mod -= 1;        // some rapport established
        }
        return mod;
    }

    /// <summary>Returns true if the text contains any of the specified keywords.</summary>
    private static bool HasAny(string text, params string[] keywords) =>
        keywords.Any(kw => text.Contains(kw, StringComparison.Ordinal));

    private static string ShiftDisposition(string current, int direction)
    {
        string[] scale = ["hostile", "angry", "annoyed", "suspicious", "neutral", "amused", "friendly", "flirtatious"];
        var index = Array.FindIndex(scale, d => d.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 4; // default to neutral
        var newIndex = Math.Clamp(index + direction, 0, scale.Length - 1);
        return scale[newIndex];
    }

    private async Task<ActionResult> ProcessMapAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var allRooms = await _stateManager.GetAllRoomsAsync(ct);
        var discovered = allRooms.Where(r => r.IsDiscovered).ToList();

        if (discovered.Count == 0)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You haven't discovered any locations yet." };

        var currentSimpleId = StripPlayerPrefix(player.CurrentRoomId);
        var mapText = BuildAsciiMap(discovered, currentSimpleId);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = mapText
        };
    }

    private static string BuildAsciiMap(IReadOnlyList<Room> rooms, string currentRoomId)
    {
        // Build adjacency from exits
        var roomById = rooms.ToDictionary(r => r.Id, r => r);

        // BFS from spawn to assign grid positions
        var positions = new Dictionary<string, (int x, int y)>();
        var visited = new HashSet<string>();
        var queue = new Queue<(string id, int x, int y)>();

        // Start from spawn or current room
        var startId = roomById.ContainsKey("spawn") ? "spawn" : currentRoomId;
        queue.Enqueue((startId, 0, 0));
        visited.Add(startId);
        positions[startId] = (0, 0);

        var dirOffsets = new Dictionary<string, (int dx, int dy)>
        {
            ["north"] = (0, -1), ["south"] = (0, 1),
            ["east"] = (1, 0), ["west"] = (-1, 0),
            ["up"] = (0, -1), ["down"] = (0, 1)
        };

        while (queue.Count > 0)
        {
            var (id, x, y) = queue.Dequeue();
            if (!roomById.TryGetValue(id, out var room)) continue;

            foreach (var exit in room.Exits)
            {
                var dir = exit.Key.ToLowerInvariant();
                var targetId = exit.Value;

                if (!dirOffsets.TryGetValue(dir, out var offset)) continue;
                if (visited.Contains(targetId)) continue;

                visited.Add(targetId);
                var nx = x + offset.dx;
                var ny = y + offset.dy;

                // Avoid collisions
                while (positions.ContainsValue((nx, ny)))
                {
                    nx += offset.dx;
                    ny += offset.dy;
                }

                positions[targetId] = (nx, ny);
                queue.Enqueue((targetId, nx, ny));
            }
        }

        if (positions.Count == 0) return "No map data available.";

        // Normalize to positive coordinates
        var minX = positions.Values.Min(p => p.x);
        var minY = positions.Values.Min(p => p.y);
        foreach (var key in positions.Keys.ToList())
            positions[key] = (positions[key].x - minX, positions[key].y - minY);

        var maxX = positions.Values.Max(p => p.x);
        var maxY = positions.Values.Max(p => p.y);

        // Each cell is 14 chars wide, 3 rows tall
        const int cellW = 14;
        const int cellH = 3;
        var gridW = (maxX + 1) * cellW + 1;
        var gridH = (maxY + 1) * cellH + 1;
        var grid = new char[gridH, gridW];

        // Fill with spaces
        for (var gy = 0; gy < gridH; gy++)
            for (var gx = 0; gx < gridW; gx++)
                grid[gy, gx] = ' ';

        // Draw rooms and connections
        foreach (var (id, (x, y)) in positions)
        {
            var cx = x * cellW + cellW / 2;
            var cy = y * cellH + cellH / 2;

            // Room box
            var isCurrent = id.Equals(currentRoomId, StringComparison.OrdinalIgnoreCase);
            var name = roomById.TryGetValue(id, out var rm) ? rm.Name : id;
            if (name.Length > 10) name = name[..10];

            // Draw bracket-style room
            var label = isCurrent ? $"[@{name}]" : $"[{name}]";
            if (label.Length > cellW - 2) label = label[..(cellW - 2)];
            var startX = cx - label.Length / 2;
            for (var i = 0; i < label.Length && startX + i < gridW; i++)
            {
                if (startX + i >= 0)
                    grid[cy, startX + i] = label[i];
            }

            // Draw connections
            if (roomById.TryGetValue(id, out var room))
            {
                foreach (var exit in room.Exits)
                {
                    var dir = exit.Key.ToLowerInvariant();
                    var targetId = exit.Value;
                    if (!positions.ContainsKey(targetId)) continue;

                    var (tx, ty) = positions[targetId];
                    var tcx = tx * cellW + cellW / 2;
                    var tcy = ty * cellH + cellH / 2;

                    // Draw path characters between rooms
                    if (dir is "north" or "south" or "up" or "down")
                    {
                        var fromY = Math.Min(cy, tcy) + 1;
                        var toY = Math.Max(cy, tcy) - 1;
                        for (var py = fromY; py <= toY && py < gridH; py++)
                        {
                            if (py >= 0 && cx >= 0 && cx < gridW && grid[py, cx] == ' ')
                                grid[py, cx] = '|';
                        }
                    }
                    else if (dir is "east" or "west")
                    {
                        var fromX = Math.Min(cx, tcx) + 1;
                        var toX = Math.Max(cx, tcx) - 1;
                        for (var px = fromX; px <= toX && px < gridW; px++)
                        {
                            if (px >= 0 && cy >= 0 && cy < gridH && grid[cy, px] == ' ')
                                grid[cy, px] = '-';
                        }
                    }
                }
            }
        }

        // Render to string
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine("     WORLD MAP — @ marks your location");
        sb.AppendLine();
        for (var gy = 0; gy < gridH; gy++)
        {
            var lineEnd = gridW - 1;
            while (lineEnd >= 0 && grid[gy, lineEnd] == ' ') lineEnd--;
            for (var gx = 0; gx <= lineEnd; gx++)
                sb.Append(grid[gy, gx]);
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine($"  Discovered: {positions.Count} location{(positions.Count == 1 ? "" : "s")}");
        sb.Append("```");

        return sb.ToString();
    }

    private static ActionResult ProcessHelp(GameAction action)
    {
        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = """
                **Available Commands:**
                `go <direction>` -- Move north/south/east/west/up/down
                `look` / `look at <target>` -- Examine surroundings or a specific thing
                `attack <target>` -- Attack an NPC
                `talk to <target>` -- Talk to an NPC
                `take <item>` -- Pick up an item
                `drop <item>` -- Drop an item from inventory
                `use <item>` -- Use a consumable item
                `shop` / `browse` -- View a shopkeeper's wares and prices
                `buy <item>` -- Buy from a shopkeeper
                `sell <item>` -- Sell an item to a shopkeeper
                `equip <item>` -- Equip an item
                `unequip <item>` -- Unequip an item
                `cast <spell>` / `cast <spell> at <target>` -- Cast a spell
                `rest` / `short rest` / `long rest` -- Rest and recover
                `inventory` -- View your inventory
                `stats` -- View your character stats
                `map` -- View ASCII world map of discovered rooms
                `help` -- Show this help message

                You can also type anything in natural language and the narrator will try to interpret it!
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

    /// <summary>Strip player-prefixed composite key back to simple room ID (e.g. "12345:spawn" → "spawn").</summary>
    private static string StripPlayerPrefix(string roomId)
    {
        var colonIdx = roomId.LastIndexOf(':');
        return colonIdx >= 0 ? roomId[(colonIdx + 1)..] : roomId;
    }

    /// <summary>Repair any composite keys that leaked into a room's exit dictionary.</summary>
    private static void RepairExitIds(Room room)
    {
        foreach (var key in room.Exits.Keys.ToList())
        {
            var val = room.Exits[key];
            if (val.Contains(':'))
                room.Exits[key] = StripPlayerPrefix(val);
        }
    }

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

    private void PublishEventToWikiInBackground(string entityId, string entityType, string title, string description, string roomId)
    {
        if (_wiki is null) return;

        var timestamp = DateTimeOffset.UtcNow;
        var path = $"events/{entityType}/{entityId}/{timestamp:yyyyMMdd-HHmmss}";
        var content = $"""
            ---
            title: {title}
            tags: [event, {entityType}]
            ---

            # {title}

            **Time:** {timestamp:u}
            **Location:** {roomId}

            {description}
            """;

        _ = Task.Run(async () =>
        {
            try
            {
                await _wiki.CreateOrUpdatePageAsync(path, title, content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background wiki event publish failed for {Path}", path);
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

    // --- P1: Mechanical Use / Consume ---

    private static readonly System.Text.RegularExpressions.Regex EffectHpRegex = new(@"[Rr]estores?\s+(\d+)\s*(?:HP|hit\s*points?|health)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex EffectMpRegex = new(@"[Rr]estores?\s+(\d+)\s*(?:MP|mana|magic)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex EffectDamageRegex = new(@"[Dd]eals?\s+(\d+)\s*(?:damage|HP\s*damage)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Handles the "use" command mechanically for consumable items.
    /// Parses the Effect string to apply HP/MP changes directly, then asks the narrator for flavor text only.
    /// Falls back to free-form processing if the effect can't be parsed mechanically.
    /// </summary>
    private async Task<ActionResult> ProcessUseAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var item = FindNamedEntity(player.Inventory, i => i.Name, action.Target);
        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You don't have any '{action.Target}' to use." };

        if (!item.IsConsumable)
        {
            // Not consumable —  fall through to free-form
            _logger.LogInformation("Item '{ItemName}' is not consumable, routing to free-form.", item.Name);
            return await ProcessFreeFormActionAsync(player, action, ct);
        }

        // Try to parse the Effect string mechanically
        var (hpDelta, mpDelta) = ParseItemEffect(item.Effect);

        if (hpDelta == 0 && mpDelta == 0)
        {
            // Can't parse effect —  fall through to free-form
            _logger.LogInformation("Could not parse effect for '{ItemName}' (Effect: '{Effect}'), routing to free-form.", item.Name, item.Effect);
            return await ProcessFreeFormActionAsync(player, action, ct);
        }

        // Apply mechanical changes
        var stateChanges = new List<StateChange>();
        var mechanicalParts = new List<string>();

        if (hpDelta != 0)
        {
            var oldHp = player.Hp;
            player.Hp = Math.Clamp(player.Hp + hpDelta, 0, player.MaxHp);
            stateChanges.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() });
            mechanicalParts.Add(hpDelta > 0 ? $"Restored {player.Hp - oldHp} HP" : $"Took {oldHp - player.Hp} damage");
        }

        if (mpDelta != 0)
        {
            var oldMp = player.Mp;
            player.Mp = Math.Clamp(player.Mp + mpDelta, 0, player.MaxMp);
            stateChanges.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Mp", OldValue = oldMp.ToString(), NewValue = player.Mp.ToString() });
            mechanicalParts.Add(mpDelta > 0 ? $"Restored {player.Mp - oldMp} MP" : $"Lost {oldMp - player.Mp} MP");
        }

        // Consume the item
        if (item.Quantity <= 1)
            player.Inventory.Remove(item);
        else
            item.Quantity--;

        stateChanges.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Inventory", NewValue = $"consumed {item.Name}" });

        var mechanicalSummary = $"Used {item.Name}. {string.Join(". ", mechanicalParts)}. (HP: {player.Hp}/{player.MaxHp}, MP: {player.Mp}/{player.MaxMp})";

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = mechanicalSummary,
            ItemsLost = [CloneInventoryItem(item, 1)],
            StateChanges = stateChanges
        };
    }

    /// <summary>Parses an item's Effect string into mechanical HP/MP deltas.</summary>
    private static (int HpDelta, int MpDelta) ParseItemEffect(string? effect)
    {
        if (string.IsNullOrWhiteSpace(effect))
            return (0, 0);

        int hp = 0, mp = 0;

        var hpMatch = EffectHpRegex.Match(effect);
        if (hpMatch.Success && int.TryParse(hpMatch.Groups[1].Value, out var hpVal))
            hp = hpVal;

        var mpMatch = EffectMpRegex.Match(effect);
        if (mpMatch.Success && int.TryParse(mpMatch.Groups[1].Value, out var mpVal))
            mp = mpVal;

        var dmgMatch = EffectDamageRegex.Match(effect);
        if (dmgMatch.Success && int.TryParse(dmgMatch.Groups[1].Value, out var dmgVal))
            hp = -dmgVal;

        return (hp, mp);
    }

    // --- P3: Buy / Sell System ---

    /// <summary>
    /// Handles "buy <item>" when the player is near a shopkeeper NPC.
    /// Deducts gold from the player and transfers the item from the shop inventory.
    /// </summary>
    private async Task<ActionResult> ProcessBuyAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's no shop here." };

        var shopkeeper = room.Npcs.FirstOrDefault(n => n.IsShopkeeper);
        if (shopkeeper is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's no shopkeeper here to buy from." };

        var shopItem = FindNamedEntity(shopkeeper.ShopInventory, i => i.Name, action.Target);
        if (shopItem is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"The shop doesn't have any '{action.Target}' for sale." };

        int price = shopItem.Value;
        if (price <= 0) price = 1;

        if (player.Gold < price)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"{shopItem.Name} costs {price} gold, but you only have {player.Gold}." };

        // Deduct gold
        var oldGold = player.Gold;
        player.Gold -= price;

        // Transfer item to player (clone, dont remove from shop -- shops restock)
        var boughtItem = CloneInventoryItem(shopItem, 1);
        var existingStack = player.Inventory.FirstOrDefault(i =>
            string.Equals(i.Name, shopItem.Name, StringComparison.OrdinalIgnoreCase));

        if (existingStack is not null)
            existingStack.Quantity++;
        else
            player.Inventory.Add(boughtItem);

        await _stateManager.SaveRoomAsync(room, ct);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"Bought {shopItem.Name} for {price} gold. You have {player.Gold} gold remaining.",
            GoldChange = -price,
            ItemsGained = [boughtItem],
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() },
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Inventory", NewValue = $"bought {shopItem.Name}" }
            ]
        };
    }

    /// <summary>
    /// Handles "sell <item>" when the player is near a shopkeeper NPC.
    /// Removes item from player inventory and adds gold (at half value).
    /// </summary>
    private async Task<ActionResult> ProcessSellAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's no shop here." };

        var shopkeeper = room.Npcs.FirstOrDefault(n => n.IsShopkeeper);
        if (shopkeeper is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There's no shopkeeper here to sell to." };

        var item = FindNamedEntity(player.Inventory, i => i.Name, action.Target);
        if (item is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You don't have any '{action.Target}' to sell." };

        int sellPrice = Math.Max(1, item.Value / 2);

        // Remove from inventory
        if (item.Quantity <= 1)
            player.Inventory.Remove(item);
        else
            item.Quantity--;

        // Add gold
        var oldGold = player.Gold;
        player.Gold += sellPrice;

        await _stateManager.SaveRoomAsync(room, ct);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"Sold {item.Name} for {sellPrice} gold. You now have {player.Gold} gold.",
            GoldChange = sellPrice,
            ItemsLost = [CloneInventoryItem(item, 1)],
            StateChanges =
            [
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Gold", OldValue = oldGold.ToString(), NewValue = player.Gold.ToString() },
                new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Inventory", NewValue = $"sold {item.Name}" }
            ]
        };
    }

    /// <summary>
    /// Handles interaction turns while in Trading mode.
    /// "buy X" and "sell X" work normally. "goodbye"/"leave" exits trading.
    /// Other input falls through to free-form conversation with the shopkeeper.
    /// </summary>
    private async Task<ActionResult?> ProcessTradingTurnAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        // Exit trading on farewell
        if (IsExitPhrase(action.RawInput))
        {
            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);

            var result = new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = $"You step away from the counter.",
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            };

            await PersistInteractionStoryEntry(player, action, result, ct);
            return result;
        }

        // Buy and sell still work in trading mode
        if (action.Type == ActionType.Buy)
            return await ProcessBuyAsync(player, action, ct);
        if (action.Type == ActionType.Sell)
            return await ProcessSellAsync(player, action, ct);

        // Movement exits trading
        if (action.Type == ActionType.Move)
        {
            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);
            return null; // Fall through to normal move
        }

        // Other input: treat as conversation with shopkeeper
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        var shopkeeper = room?.Npcs.FirstOrDefault(n => n.IsShopkeeper);
        if (room is null || shopkeeper is null)
        {
            player.Interaction.Reset();
            await _stateManager.SavePlayerAsync(player, ct);
            return null;
        }

        var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, shopkeeper, player.Interaction, action.RawInput, ct);
        ApplyInteractionUpdate(player, room, shopkeeper, freeForm);

        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);

        var convoResult = new ActionResult
        {
            ActionId = action.Id,
            RawInput = action.RawInput,
            Success = freeForm.Success,
            MechanicalSummary = $"[Trading with {shopkeeper.Name}]",
            Narration = freeForm.Narration,
            InteractionUpdate = freeForm.InteractionUpdate ?? new InteractionUpdate { Mode = InteractionMode.Trading }
        };

        await PersistInteractionStoryEntry(player, action, convoResult, ct);
        return convoResult;
    }
}
