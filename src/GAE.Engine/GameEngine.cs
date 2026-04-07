using System.Text;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Configuration;
using GAE.Engine.Worlds;
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
    private readonly IContentRegistryService? _registry;
    private readonly QuestEngine? _questEngine;
    private readonly QuestTracker? _questTracker;
    private readonly IRealmTravelService? _realmTravelService;
    private readonly IWorldRepository? _worldRepository;


    public GameEngine(
        IStateManager stateManager,
        IProbabilityEngine dice,
        INarratorService narrator,
        CommandParser parser,
        GameRulesConfig rules,
        ILogger<GameEngine> logger,
        IContentRegistryService? registry = null,
        QuestEngine? questEngine = null,
        QuestTracker? questTracker = null,
        IRealmTravelService? realmTravelService = null,
        IWorldRepository? worldRepository = null)
    {
        _stateManager = stateManager;
        _dice = dice;
        _narrator = narrator;
        _parser = parser;
        _rules = rules;
        _logger = logger;
        _registry = registry;
        _questEngine = questEngine;
        _questTracker = questTracker;
        _realmTravelService = realmTravelService;
        _worldRepository = worldRepository;
    }

    public GameAction ParseCommand(string playerId, string rawInput)
        => _parser.Parse(playerId, rawInput);

    public async Task<ActionResult> ProcessActionAsync(string playerId, GameAction action, CancellationToken ct = default)
    {
        var player = await _stateManager.GetPlayerAsync(playerId, ct);
        if (player is null)
            return new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = false, MechanicalSummary = "Player not found." };

        // Ensure MaxHP/MaxMP are correct for this player's level and stats
        // (fixes existing characters created before level scaling was added)
        RecalculateMaxHpMp(player);

        // Dead players auto-respawn at the tavern with penalties
        if (!player.IsAlive)
        {
            var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
            room ??= new Room { Id = player.CurrentRoomId, Name = "Unknown" };
            var (deathSummary, deathChanges) = await HandlePlayerDeathAsync(player, room, "their wounds", ct);
            return new ActionResult
            {
                ActionId = action.Id,
                RawInput = action.RawInput,
                Success = true,
                MechanicalSummary = deathSummary,
                StateChanges = deathChanges,
                InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore }
            };
        }

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
            ActionType.Attack or ActionType.PowerAttack or ActionType.AimedStrike => await ProcessAttackAsync(player, action, ct),
            ActionType.Defend => new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You can only defend during combat." },
            ActionType.Flee => new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You're not in combat — nothing to flee from." },
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
            ActionType.Spellbook => ProcessSpellbook(player, action),
            ActionType.Help => ProcessHelp(action),
            ActionType.Map => await ProcessMapAsync(player, action, ct),
            ActionType.Journal => ProcessJournal(player, action),
            ActionType.CompletedQuests => ProcessCompletedQuests(player, action),
            ActionType.QuestInfo => ProcessQuestInfo(player, action),
            ActionType.AcceptQuest => await ProcessAcceptQuestAsync(player, action, ct),
            ActionType.AbandonQuest => await ProcessAbandonQuest(player, action, ct),
            ActionType.TravelWorld => await ProcessTravelWorldAsync(player, action, ct),
            _ => await ProcessFreeFormActionAsync(player, action, ct)
        };

        result.RawInput = action.RawInput;

        // Save player state after any mutation
        if (result.Success && result.StateChanges.Count > 0 && action.Type != ActionType.TravelWorld)
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
            && action.Type != ActionType.Spellbook
            && action.Type != ActionType.Shop
            && action.Type != ActionType.Journal
            && action.Type != ActionType.QuestInfo
            && action.Type != ActionType.AcceptQuest
            && action.Type != ActionType.AbandonQuest
            && action.Type != ActionType.Unknown;

        if (shouldNarrate)
        {
            try
            {
                var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
                var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, player.ActiveWorldId, 5, ct);
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
                WorldId = player.ActiveWorldId,
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
            DiscordId = concept.PlayerDiscordId,
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

        // Calculate HP/MP from rules — scale with level using percentage model
        RecalculateMaxHpMp(player);
        player.Hp = player.MaxHp;
        player.Mp = player.MaxMp;

        // Grant starting items from the registry / lore seed
        await GrantStartingItemsAsync(player, ct);

        // Grant default Heal spell
        player.Spellbook.Add(new LearnedSpell
        {
            Name = "Heal",
            Description = "Channel restorative magic to mend your wounds.",
            DamageDice = "1d4+2",
            DamageStat = "wis",
            Category = SpellCategory.Healing,
            MpCost = 2,
            BasePower = 1,
            LearnedAtLevel = 1,
            TargetType = "self"
        });

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
        var itemLookups = GetStartingItemLookups(player);
        if (itemLookups.Count == 0) return;

        foreach (var itemLookup in itemLookups)
        {
            var item = await ResolveStartingItemAsync(player, itemLookup, ct);

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

                _logger.LogInformation("Granted starting item '{ItemLookup}' to {PlayerId}", itemLookup, player.Id);
            }
            else
            {
                _logger.LogWarning("Could not resolve starting item '{ItemLookup}' from registry or shops", itemLookup);
            }
        }
    }

    private List<string> GetStartingItemLookups(PlayerCharacter player)
    {
        var itemLookups = new List<string>();

        if (_registry is not null)
        {
            var classDefinition = _registry.Classes.GetAll()
                .FirstOrDefault(candidate =>
                    candidate.Id.Equals(player.Class, StringComparison.OrdinalIgnoreCase)
                    || candidate.Name.Equals(player.Class, StringComparison.OrdinalIgnoreCase));

            if (classDefinition is not null)
                itemLookups.AddRange(classDefinition.StartingEquipment);
        }

        itemLookups.AddRange(_rules.CharacterCreation.StartingItems);
        return itemLookups;
    }

    private async Task<InventoryItem?> ResolveStartingItemAsync(PlayerCharacter player, string itemLookup, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemLookup))
            return null;

        if (_registry is not null)
        {
            var template = _registry.Items.GetAll().FirstOrDefault(candidate =>
                candidate.Id.Equals(itemLookup, StringComparison.OrdinalIgnoreCase)
                || candidate.Name.Equals(itemLookup, StringComparison.OrdinalIgnoreCase));

            if (template is not null)
                return template.ToInventoryItem();
        }

        var spawnRoom = await _stateManager.GetPlayerRoomAsync(player.Id, "spawn", ct);
        if (spawnRoom is not null)
        {
            var roomItem = TryResolveKnownItem(itemLookup, spawnRoom);
            if (roomItem is not null)
                return roomItem;

            foreach (var exitRoomId in spawnRoom.Exits.Values)
            {
                var exitRoom = await _stateManager.GetPlayerRoomAsync(player.Id, exitRoomId, ct);
                if (exitRoom is null)
                    continue;

                roomItem = TryResolveKnownItem(itemLookup, exitRoom);
                if (roomItem is not null)
                    return roomItem;

                foreach (var deepExitId in exitRoom.Exits.Values)
                {
                    var deepRoom = await _stateManager.GetPlayerRoomAsync(player.Id, deepExitId, ct);
                    if (deepRoom is null)
                        continue;

                    roomItem = TryResolveKnownItem(itemLookup, deepRoom);
                    if (roomItem is not null)
                        return roomItem;
                }
            }
        }

        return null;
    }

    public async Task<CombatState?> GetActiveCombatAsync(string roomId, string worldId, CancellationToken ct = default)
        => await _stateManager.GetCombatStateAsync(roomId, worldId, ct);

    private async Task<ActionResult> ProcessTravelWorldAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        if (_realmTravelService is null)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "Realm travel service is not available."
            };
        }

        if (string.Equals(action.Parameters.GetValueOrDefault("travelMode"), "portal", StringComparison.OrdinalIgnoreCase))
        {
            return await ProcessPortalTravelAsync(player, action, ct);
        }

        if (string.IsNullOrWhiteSpace(action.Target))
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "Specify a destination world ID. Example: travel to world default-world"
            };
        }

        var result = await _realmTravelService.TransferPlayerAsync(
            player.Id,
            action.Target.Trim(),
            "player-command",
            ct: ct);
        result.ActionId = action.Id;
        return result;
    }

    private async Task<ActionResult> ProcessPortalTravelAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        if (_realmTravelService is null)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "Realm travel service is not available."
            };
        }

        if (_worldRepository is null)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "World repository is unavailable for portal lookup."
            };
        }

        var world = await _worldRepository.GetWorldAsync(player.ActiveWorldId, ct);
        if (world is null)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = $"Current world '{player.ActiveWorldId}' was not found."
            };
        }

        var completedQuestIds = player.QuestLog
            .Where(q => q.Status == QuestStatus.Completed)
            .Select(q => q.QuestId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var eligiblePortals = world.Portals
            .Where(p => string.Equals(p.SourceWorldId, player.ActiveWorldId, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.Equals(p.SourceRoomId, player.CurrentRoomId, StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.IsAdminOnly)
            .Where(p => !p.MinLevel.HasValue || player.Level >= p.MinLevel.Value)
            .Where(p => p.RequiredCompletedQuests.All(q => completedQuestIds.Contains(q)))
            .ToList();

        if (!string.IsNullOrWhiteSpace(action.Target))
        {
            eligiblePortals = eligiblePortals
                .Where(p => string.Equals(p.DestinationWorldId, action.Target, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (eligiblePortals.Count == 0)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "No active portal here can be used right now."
            };
        }

        if (eligiblePortals.Count > 1)
        {
            var options = string.Join(", ", eligiblePortals.Select(p => p.DestinationWorldId));
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = $"Multiple portals shimmer here. Choose a destination: {options}."
            };
        }

        var portal = eligiblePortals[0];
        var transfer = await _realmTravelService.TransferPlayerAsync(
            player.Id,
            portal.DestinationWorldId,
            "room-portal",
            portal.DestinationRoomId,
            ct);
        transfer.ActionId = action.Id;
        return transfer;
    }

    // --- Action processors ---

    private async Task<ActionResult> ProcessMoveAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You are in an unknown location." };

        var direction = NormalizeDirection(action.Direction ?? "");

        // "leave" / "exit" — auto-resolve direction from available exits
        if (direction == "auto")
        {
            if (room.Exits.Count == 0)
                return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "There are no exits from this area." };
            if (room.Exits.Count == 1)
            {
                direction = room.Exits.Keys.First();
            }
            else
            {
                var exitList = string.Join(", ", room.Exits.Keys.Select(d => $"**{d}**"));
                return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"There are several ways out. Which direction? You can go: {exitList}" };
            }
        }

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
                targetRoom.WorldIds = [player.ActiveWorldId];
                // Ensure reverse exit uses simple ID
                targetRoom.Exits[OppositeDirection(direction)] = simpleSourceId;
                await _stateManager.SaveRoomAsync(targetRoom, ct);
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
                    WorldIds = [player.ActiveWorldId],
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

        var escortedNpcNames = MoveEscortedNpcs(player, room, targetRoom);
        if (escortedNpcNames.Count > 0)
            await _stateManager.SaveRoomAsync(room, ct);

        await _stateManager.SaveRoomAsync(targetRoom, ct);

        var oldRoomId = player.CurrentRoomId;
        player.CurrentRoomId = simpleTargetId;

        // Quest tracking: room entered
        var questUpdate = _questTracker is not null
            ? await _questTracker.OnRoomEnteredAsync(player, targetRoom, ct)
            : null;

        var moveSummary = BuildRoomSummary($"You move {direction} to **{targetRoom.Name}**.", targetRoom);
        if (escortedNpcNames.Count > 0)
            moveSummary += $"\n🧭 {string.Join(", ", escortedNpcNames)} keeps pace with you.";
        if (questUpdate is not null)
            moveSummary += $"\n📜 {questUpdate}";

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = moveSummary,
            NewRoom = targetRoom,
            StateChanges = [new StateChange
            {
                EntityType = "Player", EntityId = player.Id,
                Property = "CurrentRoomId", OldValue = oldRoomId, NewValue = targetRoomId
            }]
        };
    }

    private List<string> MoveEscortedNpcs(PlayerCharacter player, Room sourceRoom, Room targetRoom)
    {
        if (_registry is null)
            return [];

        var moved = new List<string>();
        foreach (var progress in player.QuestLog.Where(q => q.Status == QuestStatus.Active))
        {
            var quest = _registry.Quests.GetById(progress.QuestId);
            var stage = quest?.Stages.FirstOrDefault(s => s.Id == progress.CurrentStageId);
            if (stage is null)
                continue;

            foreach (var objective in stage.Objectives.Where(o => o.Type == ObjectiveType.Escort))
            {
                var objectiveProgress = progress.Objectives.FirstOrDefault(o => o.ObjectiveId == objective.Id);
                if (objectiveProgress?.IsComplete == true || string.IsNullOrWhiteSpace(objective.TargetId))
                    continue;

                var escortNpc = sourceRoom.Npcs.FirstOrDefault(n => n.Id.Equals(objective.TargetId, StringComparison.OrdinalIgnoreCase));
                if (escortNpc is null || (escortNpc.Hp.HasValue && escortNpc.Hp.Value <= 0))
                    continue;

                if (!targetRoom.Npcs.Any(n => n.Id.Equals(escortNpc.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    sourceRoom.Npcs.Remove(escortNpc);
                    targetRoom.Npcs.Add(escortNpc);
                    moved.Add(escortNpc.Name);
                }
            }
        }

        return moved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<ActionResult> ProcessLookAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        if (room is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "You can't see anything." };

        // Room look (no target or "look around")
        if (string.IsNullOrWhiteSpace(action.Target) || TargetReferencesRoom(room, action.Target))
        {
            var summary = BuildRoomSummary($"**{room.Name}**", room);
            if (room.Id == "spawn")
                summary += "\n\n*Type `help` for a list of commands, or `go east` to explore Thornwall.*";
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = summary };
        }

        // Look at NPC
        var npc = FindNamedEntity(room.Npcs, candidate => candidate.Name, action.Target);
        if (npc is not null)
        {
            var npcDesc = new StringBuilder($"**{npc.Name}**");
            if (!string.IsNullOrWhiteSpace(npc.Personality))
                npcDesc.Append($" — {TrimToFirstSentence(npc.Personality)}");
            if (npc.IsHostile) npcDesc.Append("\n⚠️ *Hostile*");
            if (npc.IsShopkeeper) npcDesc.Append("\n🛒 *Shopkeeper — type `shop` to browse wares*");
            if (npc.Hp.HasValue) npcDesc.Append($"\n{FormatHpBar(npc.Name, npc.Hp.Value, npc.MaxHp ?? npc.Hp.Value)}");
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = npcDesc.ToString() };
        }

        // Look at item in room
        var item = FindNamedEntity(room.Items, candidate => candidate.Name, action.Target);
        if (item is not null)
        {
            var desc = !string.IsNullOrWhiteSpace(item.Description) ? item.Description : "Nothing remarkable about it.";
            var itemText = $"**{item.Name}**{(item.Quantity > 1 ? $" (x{item.Quantity})" : "")} — {desc}";
            if (item.Value > 0) itemText += $"\nWorth about {item.Value} gold.";
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = itemText };
        }

        // Check player's inventory
        var invItem = FindNamedEntity(player.Inventory, candidate => candidate.Name, action.Target);
        if (invItem is not null)
        {
            var desc = !string.IsNullOrWhiteSpace(invItem.Description) ? invItem.Description : "A useful item.";
            var invText = $"**{invItem.Name}** (in your bag) — {desc}";
            if (invItem.Value > 0) invText += $"\nWorth about {invItem.Value} gold.";
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = invText };
        }

        // Check player's equipped items
        var equippedItems = player.Equipment.AllEquipped();
        var eqItem = FindNamedEntity(equippedItems, candidate => candidate.Name, action.Target);
        if (eqItem is not null)
        {
            var desc = !string.IsNullOrWhiteSpace(eqItem.Description) ? eqItem.Description : "Currently equipped.";
            var eqText = $"**{eqItem.Name}** (equipped) — {desc}";
            if (eqItem.DamageDice is not null) eqText += $"\nDamage: {eqItem.DamageDice}";
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = eqText };
        }

        return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"You don't see '{action.Target}' anywhere nearby." };
    }

    private static string TrimToFirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var idx = text.IndexOfAny(['.', '!', '?']);
        return idx >= 0 ? text[..(idx + 1)] : text;
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

        // Add level-based proficiency bonus to attack rolls
        int proficiencyBonus = _rules.Combat.ProficiencyBaseBonus + (player.Level / _rules.Combat.ProficiencyScaleLevel);

        // Apply special attack modifiers
        int hitBonus = 0, dmgBonus = 0;
        bool advantage = false;
        if (action.Type == ActionType.PowerAttack) { hitBonus = -3; dmgBonus = 5; }
        else if (action.Type == ActionType.AimedStrike) { advantage = true; }

        int totalAttackMod = attackMod + proficiencyBonus + hitBonus;

        DiceRoll attackRoll;
        var extraRolls = new List<DiceRoll>();
        if (advantage)
        {
            var r1 = _dice.RollAttack(totalAttackMod);
            var r2 = _dice.RollAttack(totalAttackMod);
            attackRoll = r1.Total >= r2.Total ? r1 : r2;
            extraRolls.Add(r1); extraRolls.Add(r2);
        }
        else
        {
            attackRoll = _dice.RollAttack(totalAttackMod);
        }

        int targetDefense = target.Defense ?? _rules.Combat.BaseDefense;
        var outcome = ProbabilityEngine.DetermineOutcome(attackRoll, targetDefense);
        attackRoll.Outcome = outcome;
        attackRoll.TargetNumber = targetDefense;

        var result = new ActionResult
        {
            ActionId = action.Id,
            DiceRolls = advantage ? extraRolls : [attackRoll]
        };

        int initSeed = target.Name.Length * 7 + attackRoll.Total;
        string combatOpener = $"⚔️ **You engage {target.Name}!**\n";

        if (outcome == RollOutcome.CriticalMiss || outcome == RollOutcome.Miss)
        {
            result.Success = true;
            result.MechanicalSummary = combatOpener + DescribePlayerMiss(target.Name, outcome, initSeed);

            // Even on a miss, enter combat so the enemy can fight back
            if (target.IsHostile || (target.Hp.HasValue && target.Hp.Value > 0))
            {
                var hostiles = room.Npcs.Where(n => n.IsHostile || n == target).Distinct().ToList();
                await EnterCombatAsync(player, room, hostiles, ct);
                result.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Combat, CombatStatus = "ongoing" };
                var surviveUpdate = _questTracker is not null
                    ? await _questTracker.OnCombatRoundSurvivedAsync(player, room.Id, ct)
                    : null;
                if (surviveUpdate is not null)
                    result.MechanicalSummary += $"\n📜 {surviveUpdate}";
                await _stateManager.SavePlayerAsync(player, ct);
            }
            return result;
        }

        // Hit (GlancingHit, Hit, or CriticalHit) — roll damage
        string damageDice = weapon?.DamageDice ?? "1d4";
        int damageMod = player.GetModifier(attackStat) + dmgBonus;
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

        result.Success = true;
        result.MechanicalSummary = combatOpener + DescribePlayerHit(target.Name, totalDamage, outcome, initSeed);

        // Check if NPC is dead
        if (target.Hp.HasValue && target.Hp.Value <= 0)
        {
            result.MechanicalSummary += "\n" + PickFlavor(KillVerbs, initSeed + 99).Replace("{0}", target.Name);

            // XP reward
            int xpGain = target.Level * 10;
            player.Xp += xpGain;
            result.XpGained = xpGain;
            result.MechanicalSummary += $"\n⭐ You gain {xpGain} XP!";

            // Check for level-up
            var levelUpMsg = CheckAndApplyLevelUp(player);
            if (levelUpMsg is not null)
                result.MechanicalSummary += $"\n{levelUpMsg}";

            // Gold loot drop
            if (_dice.Roll("1d100", "Loot check").Total <= (int)(_rules.Loot.EnemyDropChance * 100))
            {
                var goldDrop = _dice.Roll("1d20", "Gold drop");
                int goldAmount = goldDrop.Total + (target.Level * 2);
                player.Gold += goldAmount;
                result.GoldChange = goldAmount;
                result.MechanicalSummary += $"\n💰 You find {goldAmount} gold!";
            }

            // Drop NPC loot table items
            foreach (var lootItem in target.LootTable)
            {
                var clone = CloneInventoryItem(lootItem, Math.Max(1, lootItem.Quantity));
                player.Inventory.Add(clone);
                result.ItemsGained.Add(clone);
                result.MechanicalSummary += $"\n🎁 {lootItem.Name}!";
            }

            room.Npcs.Remove(target);
            player.Interaction.Reset();
            result.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "victory" };

            // Quest tracking: enemy killed
            var questUpdate = _questTracker is not null
                ? await _questTracker.OnEnemyKilledAsync(player, target.Id, target.Name, ct)
                : null;
            if (questUpdate is not null)
                result.MechanicalSummary += $"\n📜 {questUpdate}";

            await _stateManager.SaveRoomAsync(room, ct);
        }
        else if (target.Hp.HasValue && target.Hp.Value > 0)
        {
            // NPC survived — enter combat mode
            var hostiles = room.Npcs.Where(n => n.IsHostile || n == target).Distinct().ToList();
            await EnterCombatAsync(player, room, hostiles, ct);
            result.InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Combat, CombatStatus = "ongoing" };
            var surviveUpdate = _questTracker is not null
                ? await _questTracker.OnCombatRoundSurvivedAsync(player, room.Id, ct)
                : null;
            if (surviveUpdate is not null)
                result.MechanicalSummary += $"\n📜 {surviveUpdate}";
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

        // Load world-scoped NPC state (disposition may differ per world)
        await OverlayWorldNpcStateAsync(target, player.ActiveWorldId, player.Id, ct);

        // Decay disposition toward baseline before starting conversation
        var elapsed = DateTimeOffset.UtcNow - target.DispositionState.LastUpdated;
        target.DispositionState.DecayTowardBaseline(elapsed);
        target.Disposition = target.DispositionState.ToFlatDisposition();

        // Enter conversation or trading mode depending on NPC type
        var mode = target.IsShopkeeper ? InteractionMode.Trading : InteractionMode.Conversation;
        player.Interaction = new InteractionState
        {
            Mode = mode,
            Target = target.Name,
            NpcDisposition = target.Disposition,
            CanLeave = true,
            LeaveConsequence = "normal"
        };
        player.Interaction.AppendContext(target.IsShopkeeper
            ? $"Player initiated trading with shopkeeper {target.Name}."
            : $"Player initiated conversation with {target.Name}.");

        // Get AI narration for the greeting
        var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, target, player.Interaction, action.RawInput, ct);

        // Apply interaction update from AI
        ApplyInteractionUpdate(player, room, target, freeForm);

        // Persist world-scoped NPC state
        await PersistWorldNpcStateAsync(target, player.ActiveWorldId, player.Id, ct);

        // Quest tracking: conversation started (TalkTo + Deliver objectives)
        var questUpdate = _questTracker is not null
            ? await _questTracker.OnConversationStartedAsync(player, target, ct)
            : null;

        // Process quest updates from narrator response
        string? narratorQuestSummary = null;
        QuestReward? questReward = null;
        if (freeForm.QuestUpdates is not null && _questTracker is not null)
        {
            (narratorQuestSummary, questReward) = await _questTracker.ProcessNarratorQuestUpdatesAsync(player, freeForm.QuestUpdates, target, ct);
        }

        await _stateManager.SavePlayerAsync(player, ct);

        var mechanicalSummary = $"You approach {target.Name}.";
        if (questUpdate is not null)
            mechanicalSummary += $"\n📜 {questUpdate}";
        if (narratorQuestSummary is not null)
            mechanicalSummary += $"\n📜 {narratorQuestSummary}";
        if (questReward is not null)
        {
            if (questReward.Xp > 0) mechanicalSummary += $"\n⭐ +{questReward.Xp} XP";
            if (questReward.Gold > 0) mechanicalSummary += $"\n💰 +{questReward.Gold} gold";
        }

        var result = new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = mechanicalSummary,
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

        // Quest tracking: item collected
        var questUpdate = _questTracker is not null
            ? await _questTracker.OnItemCollectedAsync(player, item.Id, item.Name, actualQuantity, ct)
            : null;

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


        var questNote = questUpdate is not null ? $"\n📜 {questUpdate}" : "";

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"{itemLabel} added to inventory.{inventoryNote}{autoEquipNote}{questNote}",
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

        if (item.Type == ItemType.QuestItem)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "Something tells you this is important. You'd better hold onto it."
            };
        }

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
    //  CAST — Spellbook system (Option A: AI creates, engine scales)
    // ═══════════════════════════════════════════════════════════════════

    private static readonly Dictionary<int, string> LevelDamageDice = new()
    {
        {1,"1d4"},{2,"1d6"},{3,"1d8"},{4,"1d10"},{5,"2d6"},
        {6,"2d8"},{7,"2d10"},{8,"3d8"},{9,"3d10"},{10,"4d10"}
    };
    private static readonly Dictionary<int, string> LevelHealDice = new()
    {
        {1,"1d4+1"},{2,"1d6+2"},{3,"1d8+3"},{4,"2d6+2"},{5,"2d8+3"},
        {6,"3d6+3"},{7,"3d8+4"},{8,"4d6+4"},{9,"4d8+5"},{10,"5d8+5"}
    };

    private async Task<ActionResult> ProcessCastAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        room ??= new Room { Id = player.CurrentRoomId, Name = "Unknown" };

        string spellInput = action.Target ?? "";
        if (string.IsNullOrWhiteSpace(spellInput))
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "Cast what? Try: `cast fireball at skeleton`" };

        _logger.LogInformation("Cast attempt by {Player}: spell={Spell}, target={Target}",
            player.Name, spellInput, action.Parameters.GetValueOrDefault("target") ?? "(none)");

        // Check if spell is already known (fuzzy match)
        var known = player.Spellbook.FirstOrDefault(s =>
            s.Name.Equals(spellInput, StringComparison.OrdinalIgnoreCase) ||
            spellInput.Contains(s.Name, StringComparison.OrdinalIgnoreCase));

        if (known is not null)
            return await CastKnownSpellAsync(player, known, action, room, ct);

        // New spell - vet with AI
        SpellVetResponse? vet = null;
        try
        {
            vet = await _narrator.VetSpellAsync(player, spellInput, room, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spell vetting failed");
        }

        if (vet is null)
        {
            // AI unavailable - create a basic damage spell mechanically
            vet = new SpellVetResponse
            {
                Approved = true,
                SpellName = spellInput.Length > 30 ? spellInput[..30] : spellInput,
                Description = "A burst of raw magical energy.",
                Category = "damage",
                TargetType = "enemy",
                BasePower = Math.Min(3, player.Level),
                MpCost = 4,
                Narration = "You channel raw magic, shaping it by force of will alone."
            };
        }

        if (!vet.Approved)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"The magic fizzles... {vet.RejectionReason}" };

        // Level-scale the spell
        var newSpell = CreateLevelScaledSpell(vet, player);

        // Check MP
        if (player.Mp < newSpell.MpCost)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Not enough MP to cast {newSpell.Name}. Need {newSpell.MpCost} MP, have {player.Mp}." };

        // Add to spellbook
        player.Spellbook.Add(newSpell);

        // Cast it
        return await ExecuteSpellAsync(player, newSpell, action, room, vet.Narration, isNewSpell: true, ct);
    }

    private static LearnedSpell CreateLevelScaledSpell(SpellVetResponse vet, PlayerCharacter player)
    {
        int effectivePower = Math.Clamp(Math.Min(vet.BasePower, player.Level + 1), 1, 10);
        var category = Enum.TryParse<SpellCategory>(vet.Category, true, out var cat) ? cat : SpellCategory.Damage;

        string dice = category == SpellCategory.Healing
            ? LevelHealDice.GetValueOrDefault(effectivePower, "1d4+1")
            : LevelDamageDice.GetValueOrDefault(effectivePower, "1d4");

        int mpCost = Math.Max(2, effectivePower * 2);

        return new LearnedSpell
        {
            Name = vet.SpellName,
            Description = vet.Description,
            DamageDice = dice,
            DamageStat = "int",
            Category = category,
            MpCost = mpCost,
            BasePower = vet.BasePower,
            LearnedAtLevel = player.Level,
            TargetType = vet.TargetType
        };
    }

    private async Task<ActionResult> CastKnownSpellAsync(PlayerCharacter player, LearnedSpell spell, GameAction action, Room room, CancellationToken ct)
    {
        if (player.Mp < spell.MpCost)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"Not enough MP for {spell.Name}. Need {spell.MpCost}, have {player.Mp}." };

        return await ExecuteSpellAsync(player, spell, action, room, null, isNewSpell: false, ct);
    }

    private async Task<ActionResult> ExecuteSpellAsync(PlayerCharacter player, LearnedSpell spell, GameAction action, Room room, string? narration, bool isNewSpell, CancellationToken ct)
    {
        player.Mp -= spell.MpCost;
        var mech = new List<string>();
        var dice = new List<DiceRoll>();
        var stateChanges = new List<StateChange>();

        if (isNewSpell)
            mech.Add($"**New spell learned: {spell.Name}!** ({spell.DamageDice}, {spell.MpCost} MP)");

        stateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "mp", OldValue = (player.Mp + spell.MpCost).ToString(), NewValue = player.Mp.ToString() });

        string? combatStatus = null;

        switch (spell.Category)
        {
            case SpellCategory.Damage:
            {
                // Find target - use action parameter or first hostile NPC
                string? castTarget = action.Parameters.GetValueOrDefault("target");
                var target = !string.IsNullOrEmpty(castTarget)
                    ? room.Npcs.FirstOrDefault(n => n.Name.Contains(castTarget, StringComparison.OrdinalIgnoreCase) && n.IsHostile && n.Hp > 0)
                    : room.Npcs.FirstOrDefault(n => n.IsHostile && n.Hp > 0);

                if (target is null)
                {
                    player.Mp += spell.MpCost; // refund
                    return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "No valid target found. The spell fizzles." };
                }

                // Roll spell attack (INT-based)
                int intMod = player.GetModifier("int");
                int profBonus = 2 + (player.Level / 4);
                var attackRoll = _dice.RollAttack(intMod + profBonus);
                int targetDefense = target.Defense ?? 10;
                var outcome = ProbabilityEngine.DetermineOutcome(attackRoll, targetDefense);
                attackRoll.Outcome = outcome;
                attackRoll.TargetNumber = targetDefense;
                dice.Add(attackRoll);

                if (outcome == RollOutcome.Hit || outcome == RollOutcome.CriticalHit)
                {
                    var dmgRoll = _dice.RollDamage(spell.DamageDice, intMod);
                    dice.Add(dmgRoll);
                    int totalDmg = outcome == RollOutcome.CriticalHit ? dmgRoll.Total * 2 : dmgRoll.Total;
                    totalDmg = Math.Max(1, totalDmg);

                    target.Hp = Math.Max(0, (target.Hp ?? 0) - totalDmg);
                    stateChanges.Add(new StateChange { EntityType = "npc", EntityId = target.Id, Property = "hp", OldValue = ((target.Hp ?? 0) + totalDmg).ToString(), NewValue = (target.Hp ?? 0).ToString() });

                    bool isCrit = outcome == RollOutcome.CriticalHit;
                    mech.Add(isCrit
                        ? $"**CRITICAL!** {spell.Name} devastates {target.Name}!"
                        : $"{spell.Name} strikes {target.Name}!");

                    if ((target.Hp ?? 0) <= 0)
                    {
                        mech.Add($"**{target.Name} falls!**");
                        // Award XP and gold
                        int xpReward = target.Level * 10 + 20;
                        int goldReward = _dice.Roll($"1d{target.Level * 5 + 10}", "Spell kill gold").Total;
                        player.Xp += xpReward;
                        player.Gold += goldReward;
                        mech.Add($"\n**Rewards:** +{xpReward} XP | +{goldReward} gold");
                        var lvlUp = CheckAndApplyLevelUp(player);
                        if (lvlUp is not null) mech.Add(lvlUp);

                        // Check if combat over
                        bool allDead = !room.Npcs.Any(n => n.IsHostile && (n.Hp ?? 0) > 0);
                        if (allDead)
                        {
                            player.Interaction.Reset();
                            combatStatus = "victory";
                        }
                    }
                }
                else
                {
                    mech.Add($"{spell.Name} misses {target.Name}!");
                }
                break;
            }
            case SpellCategory.Healing:
            {
                int intMod = player.GetModifier("int");
                var healRoll = _dice.RollDamage(spell.DamageDice, intMod);
                dice.Add(healRoll);
                int healed = Math.Max(1, healRoll.Total);
                int oldHp = player.Hp;
                player.Hp = Math.Min(player.MaxHp, player.Hp + healed);
                int actualHeal = player.Hp - oldHp;
                stateChanges.Add(new StateChange { EntityType = "player", EntityId = player.Id, Property = "hp", OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() });
                mech.Add($"{spell.Name} restores {actualHeal} HP! (HP: {player.Hp}/{player.MaxHp})");
                break;
            }
            default: // Buff, Debuff, Utility
            {
                mech.Add($"You cast {spell.Name}! {spell.Description}");
                break;
            }
        }

        mech.Add($"\n*-{spell.MpCost} MP ({player.Mp}/{player.MaxMp} remaining)*");

        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);

        var result = new ActionResult
        {
            ActionId = action.Id,
            RawInput = action.RawInput,
            Success = true,
            MechanicalSummary = string.Join("\n", mech),
            Narration = narration,
            DiceRolls = dice,
            StateChanges = stateChanges,
            InteractionUpdate = combatStatus is not null
                ? new InteractionUpdate { Mode = combatStatus == "victory" ? InteractionMode.Explore : InteractionMode.Combat, CombatStatus = combatStatus }
                : null
        };

        await PersistInteractionStoryEntry(player, action, result, ct);
        return result;
    }

    private static ActionResult ProcessSpellbook(PlayerCharacter player, GameAction action)
    {
        if (player.Spellbook.Count == 0)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = "Your spellbook is empty. Try `cast <spell name>` to learn one!" };

        const int w = 52; // inner width
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine($"\u250c\u2500 SPELLBOOK {new string('\u2500', w - 11)}\u2510");
        sb.AppendLine($"\u2502 {"Spell",-22} {"Dice",-10} {"Cost",-8} {"Type",-8} \u2502");
        sb.AppendLine($"\u251c{new string('\u2500', w)}\u2524");
        foreach (var s in player.Spellbook)
        {
            var cat = s.Category.ToString()[..3].ToLower();
            var name = s.Name.Length > 22 ? s.Name[..22] : s.Name;
            var dice = string.IsNullOrEmpty(s.DamageDice) ? "\u2014" : s.DamageDice;
            sb.AppendLine($"\u2502 {name,-22} {dice,-10} {s.MpCost + "mp",-8} {cat,-8} \u2502");
        }
        var mpLine = $"MP: {player.Mp}/{player.MaxMp}";
        sb.AppendLine($"\u251c{new string('\u2500', w)}\u2524");
        sb.AppendLine($"\u2502 {mpLine.PadRight(w - 2)} \u2502");
        sb.AppendLine($"\u2514{new string('\u2500', w)}\u2518");
        sb.Append("```");

        return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = sb.ToString() };
    }

    private async Task<ActionResult> ProcessFreeFormActionAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        _logger.LogInformation("Routing free-form action for {PlayerId} in room {RoomId}: {RawInput}", player.Id, player.CurrentRoomId, action.RawInput);

        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        room ??= new Room { Id = player.CurrentRoomId, Name = "Unknown" };

        var recentStory = await _stateManager.GetRecentStoryForRoomAsync(player.CurrentRoomId, player.ActiveWorldId, 5, ct);
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
                    // Check for level-up after free-form XP gain
                    if (delta > 0)
                    {
                        var freeFormLevelUp = CheckAndApplyLevelUp(player);
                        if (freeFormLevelUp is not null)
                            result.MechanicalSummary += $"\n{freeFormLevelUp}";
                    }
                    break;
                default:
                    // Block arbitrary stat modifications (str, dex, etc.) from free-form
                    _logger.LogWarning("Anti-cheat: blocked stat change '{Stat}' = {Delta} from free-form for player {PlayerId}", stat, delta, player.Id);
                    break;
            }
        }

        // Apply inventory changes — prefer known items from room/shop, allow RP-created items if level-appropriate
        foreach (var change in freeForm.InventoryChanges)
        {
            if (string.Equals(change.Action, "add", StringComparison.OrdinalIgnoreCase))
            {
                // First try to resolve from a known source (room floor, shopkeeper)
                var resolved = TryResolveKnownItem(change.ItemName, room);
                if (resolved is not null)
                {
                    // Known item — use it as-is
                    resolved.Quantity = Math.Max(1, change.Quantity);
                }
                else
                {
                    // RP-created item — allow it but level-gate to player level + 1
                    resolved = new InventoryItem
                    {
                        Name = change.ItemName,
                        Type = ItemType.Misc,
                        Quantity = Math.Max(1, change.Quantity),
                        Level = player.Level, // RP items default to current player level
                        Description = $"Acquired through roleplay interaction."
                    };
                    _logger.LogInformation("RP item created: '{ItemName}' (Level {Level}) for player {PlayerId}",
                        change.ItemName, resolved.Level, player.Id);
                }

                // Level gate: block items more than 1 level above the player
                if (resolved.Level > player.Level + 1)
                {
                    _logger.LogWarning("Anti-cheat: blocked item '{ItemName}' (Level {ItemLevel}) — too high for player level {PlayerLevel}",
                        resolved.Name, resolved.Level, player.Level);
                    continue;
                }

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

        // Process quest updates from narrator response
        string? narratorQuestSummary = null;
        QuestReward? questReward = null;
        if (freeForm.QuestUpdates is not null && _questTracker is not null)
        {
            (narratorQuestSummary, questReward) = await _questTracker.ProcessNarratorQuestUpdatesAsync(player, freeForm.QuestUpdates, currentNpc: null, ct);
        }

        if (narratorQuestSummary is not null)
            result.MechanicalSummary += $"\n📜 {narratorQuestSummary}";
        if (questReward is not null)
        {
            if (questReward.Xp > 0) result.MechanicalSummary += $"\n⭐ +{questReward.Xp} XP";
            if (questReward.Gold > 0) result.MechanicalSummary += $"\n💰 +{questReward.Gold} gold";
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
            WorldId = player.ActiveWorldId,
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
        var roomItem = room.Items.FirstOrDefault(i => MatchesItemLookup(i, itemName));
        if (roomItem is not null)
            return CloneInventoryItem(roomItem, 1);

        // Check shopkeeper inventories in the room
        foreach (var npc in room.Npcs.Where(n => n.IsShopkeeper))
        {
            var shopItem = npc.ShopInventory.FirstOrDefault(i => MatchesItemLookup(i, itemName));
            if (shopItem is not null)
                return CloneInventoryItem(shopItem, 1);
        }

        return null;
    }

    private static bool MatchesItemLookup(InventoryItem item, string itemLookup)
    {
        var normalizedLookup = NormalizeLookupText(itemLookup);
        return NormalizeLookupText(item.Name) == normalizedLookup
            || NormalizeLookupText(item.Id) == normalizedLookup;
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

        // Fuzzy match: allow up to 2 edits for names >= 4 chars
        if (query.Length >= 4 && candidate.Length >= 4)
        {
            var dist = LevenshteinDistance(normalizedQuery, normalizedCandidate);
            var maxLen = Math.Max(normalizedQuery.Length, normalizedCandidate.Length);
            if (dist <= 1) return 45;
            if (dist <= 2 && maxLen >= 5) return 35;
        }

        return 0;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
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

            // Use the procedural generator — creates entire multi-floor dungeon upfront
            // Pulls items and enemies from the content registry, scaling by player level
            var generator = _registry is not null
                ? new DungeonGenerator(_registry, _logger)
                : throw new InvalidOperationException("Content registry is required for dungeon generation");
            var entrance = await generator.GenerateFullDungeonAsync(
                dungeonId, player.Level, room, _stateManager, player.ActiveWorldId, ct);

            // Add exit from current room to the dungeon
            var exitDirection = PickDungeonExitDirection(room);
            room.Exits[exitDirection] = dungeonId;
            await _stateManager.SaveRoomAsync(room, ct);

            _logger.LogInformation(
                "Dungeon '{DungeonName}' ({DungeonId}) created for player {PlayerId} (level {Level}). Exit: {Direction}",
                entrance.Name, dungeonId, player.Id, player.Level, exitDirection);

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

        // Load world-scoped NPC state for this conversation
        await OverlayWorldNpcStateAsync(npc, player.ActiveWorldId, player.Id, ct);

        // Movement mid-conversation: exit with disposition penalty
        if (action.Type == ActionType.Move)
        {
            var dispositionPenalty = ShiftDisposition(npc.Disposition, -1);
            npc.Disposition = dispositionPenalty;
            player.Interaction.AppendContext($"Player walked away mid-conversation. {npc.Name}'s disposition shifted to {dispositionPenalty}.");

            var freeForm = await _narrator.ProcessConversationTurnAsync(player, room, npc, player.Interaction, "[Player walks away mid-sentence]", ct);
            ApplyInteractionUpdate(player, room, npc, freeForm);

            player.Interaction.Reset();
            await PersistWorldNpcStateAsync(npc, player.ActiveWorldId, player.Id, ct);
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
            await PersistWorldNpcStateAsync(npc, player.ActiveWorldId, player.Id, ct);
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
            player.Interaction.AdvancePlayerTurn();

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

            await PersistWorldNpcStateAsync(npc, player.ActiveWorldId, player.Id, ct);
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

            var mechanicalLine = $"[Conversation with {npc.Name}, turn {player.Interaction.CurrentTurnNumber}]";
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
        if (room is null) { player.Interaction.Reset(); return null; }

        var combat = await _stateManager.GetCombatStateAsync(room.Id, player.ActiveWorldId, ct);
        var enemies = room.Npcs.Where(n => combat?.TurnOrder.Any(t => !t.IsPlayer && t.Id == n.Id) == true).ToList();
        if (combat is null || enemies.Count == 0)
        {
            var singleEnemy = FindNamedEntity(room.Npcs, n => n.Name, player.Interaction.Target);
            if (singleEnemy is null)
            {
                player.Interaction.Reset();
                await _stateManager.SavePlayerAsync(player, ct);
                return new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = true,
                    MechanicalSummary = "Your opponent is no longer here. Combat ends.",
                    InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore } };
            }
            enemies = [singleEnemy];
        }

        // ─── Flee ───
        if (action.Type == ActionType.Flee)
        {
            int totalOppDamage = 0; var diceRolls = new List<DiceRoll>();
            foreach (var enemy in enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0))
                if (enemy.DamageDice is not null)
                { var r = _dice.Roll(enemy.DamageDice, $"{enemy.Name} opportunity attack"); totalOppDamage += Math.Max(1, r.Total); diceRolls.Add(r); }
            player.Hp = Math.Max(0, player.Hp - totalOppDamage);
            player.Interaction.Reset();
            var escDir = room.Exits.Keys.FirstOrDefault();
            if (escDir is not null) player.CurrentRoomId = room.Exits[escDir];
            if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, player.ActiveWorldId, ct);
            await _stateManager.SavePlayerAsync(player, ct);
            var fleeResult = new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = true,
                MechanicalSummary = totalOppDamage > 0 ? $"You flee! Enemies strike for {totalOppDamage} as you escape." : "You flee from combat!",
                DiceRolls = diceRolls, InteractionUpdate = new InteractionUpdate { Mode = InteractionMode.Explore, CombatStatus = "fled" } };
            await PersistInteractionStoryEntry(player, action, fleeResult, ct);
            return fleeResult;
        }

        // ─── Use item during combat ───
        if (action.Type == ActionType.Use)
        {
            var useResult = await ProcessUseAsync(player, action, ct);
            var useMech = new List<string> { useResult.MechanicalSummary };
            var useDice = new List<DiceRoll>(useResult.DiceRolls);
            var useSC = new List<StateChange>(useResult.StateChanges);

            // Enemies get 1 turn after item use (success or fail)
            RunEnemyAttacks(player, enemies, room, useMech, useDice, useSC, defenseBonus: 0);

            string useCombatStatus = player.Hp <= 0 ? "defeat" : "ongoing";
            if (player.Hp <= 0) { player.Interaction.Reset(); if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, player.ActiveWorldId, ct); }

            // HP bar after item use
            if (useCombatStatus == "ongoing")
                AppendHpBars(useMech, player, enemies, room);

            await _stateManager.SavePlayerAsync(player, ct);
            await _stateManager.SaveRoomAsync(room, ct);
            var itemResult = new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = useResult.Success,
                MechanicalSummary = string.Join("\n", useMech), DiceRolls = useDice, StateChanges = useSC, ItemsLost = useResult.ItemsLost,
                InteractionUpdate = new InteractionUpdate { Mode = useCombatStatus == "ongoing" ? InteractionMode.Combat : InteractionMode.Explore, CombatStatus = useCombatStatus } };
            await PersistInteractionStoryEntry(player, action, itemResult, ct);
            return itemResult;
        }

        // ─── Defend (skip attacks, gain +4 defense for this round) ───
        if (action.Type == ActionType.Defend)
        {
            var defMech = new List<string> { "You raise your guard and brace for impact! (+4 defense this round)" };
            var defDice = new List<DiceRoll>();
            var defSC = new List<StateChange>();
            RunEnemyAttacks(player, enemies, room, defMech, defDice, defSC, defenseBonus: 4);
            string defStatus = player.Hp <= 0 ? "defeat" : "ongoing";
            if (player.Hp <= 0) { player.Interaction.Reset(); if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, player.ActiveWorldId, ct); }
            if (defStatus == "ongoing") AppendHpBars(defMech, player, enemies, room);
            await _stateManager.SavePlayerAsync(player, ct);
            await _stateManager.SaveRoomAsync(room, ct);
            var defResult = new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = true,
                MechanicalSummary = string.Join("\n", defMech), DiceRolls = defDice, StateChanges = defSC,
                InteractionUpdate = new InteractionUpdate { Mode = defStatus == "ongoing" ? InteractionMode.Combat : InteractionMode.Explore, CombatStatus = defStatus } };
            await PersistInteractionStoryEntry(player, action, defResult, ct);
            return defResult;
        }

        // ─── Cast spell during combat ───
        if (action.Type == ActionType.Cast)
        {
            var castResult = await ProcessCastAsync(player, action, ct);
            // If cast succeeded and player is still in combat, enemies get a turn
            if (castResult.Success && player.Interaction.Mode == InteractionMode.Combat)
            {
                var aliveEnemies = enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)).ToList();
                if (aliveEnemies.Count > 0)
                {
                    var castMech = new List<string> { castResult.MechanicalSummary };
                    var castDice = new List<DiceRoll>(castResult.DiceRolls);
                    var castSC = new List<StateChange>(castResult.StateChanges);
                    RunEnemyAttacks(player, aliveEnemies, room, castMech, castDice, castSC, defenseBonus: 0);

                    string castCombatStatus = player.Hp <= 0 ? "defeat" : "ongoing";
                    if (player.Hp <= 0) { player.Interaction.Reset(); if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, player.ActiveWorldId, ct); }
                    if (castCombatStatus == "ongoing") AppendHpBars(castMech, player, aliveEnemies, room);

                    await _stateManager.SavePlayerAsync(player, ct);
                    await _stateManager.SaveRoomAsync(room, ct);
                    var combatCastResult = new ActionResult { ActionId = action.Id, RawInput = action.RawInput, Success = true,
                        MechanicalSummary = string.Join("\n", castMech), DiceRolls = castDice, StateChanges = castSC,
                        Narration = castResult.Narration,
                        InteractionUpdate = new InteractionUpdate { Mode = castCombatStatus == "ongoing" ? InteractionMode.Combat : InteractionMode.Explore, CombatStatus = castCombatStatus } };
                    await PersistInteractionStoryEntry(player, action, combatCastResult, ct);
                    return combatCastResult;
                }
            }
            return castResult;
        }

        // ─── Resolve attack modifiers for special moves ───
        int attackHitBonus = 0;
        int attackDmgBonus = 0;
        bool hasAdvantage = false;

        if (action.Type == ActionType.PowerAttack)
        {
            attackHitBonus = -3;
            attackDmgBonus = 5;
        }
        else if (action.Type == ActionType.AimedStrike)
        {
            hasAdvantage = true;
        }

        // ─── Multi-exchange combat (3 exchanges per turn) ───
        const int ExchangesPerTurn = 3;
        var allDice = new List<DiceRoll>();
        var allSC = new List<StateChange>();
        var mech = new List<string>();
        int totalXp = 0, totalGold = 0;
        var totalLoot = new List<InventoryItem>();
        bool combatOver = false;

        var weapon = player.Equipment.Weapon;
        string attackStat = weapon?.DamageStat ?? _rules.Combat.MeleeStat;
        int baseAttackMod = player.GetModifier(attackStat);
        int proficiencyBonus = _rules.Combat.ProficiencyBaseBonus + (player.Level / _rules.Combat.ProficiencyScaleLevel);

        // Round header
        var primaryTarget = enemies.FirstOrDefault(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e));
        int roundNum = (combat?.RoundNumber ?? 0) + 1;
        mech.Add($"**Round {roundNum}** vs {primaryTarget?.Name ?? "???"}");
        mech.Add("");

        for (int exchange = 0; exchange < ExchangesPerTurn && !combatOver; exchange++)
        {
            if (exchange > 0) mech.Add(""); // blank line between exchanges

            // Pick target (first alive enemy)
            Npc? target = null;
            if (!string.IsNullOrWhiteSpace(action.Target))
                target = FindNamedEntity(enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)), n => n.Name, action.Target);
            target ??= enemies.FirstOrDefault(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e));

            if (target is null) { combatOver = true; break; }

            // ── Player attacks ──
            int totalAttackMod = baseAttackMod + proficiencyBonus + attackHitBonus;

            DiceRoll attackRoll;
            if (hasAdvantage && exchange == 0) // advantage only on first exchange for aimed strike
            {
                var roll1 = _dice.RollAttack(totalAttackMod);
                var roll2 = _dice.RollAttack(totalAttackMod);
                attackRoll = roll1.Total >= roll2.Total ? roll1 : roll2;
                mech.Add($"[Aimed] Rolled {roll1.Total} and {roll2.Total}, taking {attackRoll.Total}.");
                allDice.Add(roll1);
                allDice.Add(roll2);
            }
            else
            {
                attackRoll = _dice.RollAttack(totalAttackMod);
                allDice.Add(attackRoll);
            }

            int targetDefense = target.Defense ?? _rules.Combat.BaseDefense;
            var outcome = ProbabilityEngine.DetermineOutcome(attackRoll, targetDefense);
            attackRoll.Outcome = outcome;
            attackRoll.TargetNumber = targetDefense;

            int flavorSeed = exchange * 7 + (target.Name.Length * 13) + (combat?.RoundNumber ?? 0) * 3;

            if (outcome == RollOutcome.CriticalMiss || outcome == RollOutcome.Miss)
            {
                mech.Add(DescribePlayerMiss(target.Name, outcome, flavorSeed));
            }
            else
            {
                string damageDice = weapon?.DamageDice ?? "1d4";
                int damageMod = player.GetModifier(attackStat) + attackDmgBonus;
                var damageRoll = _dice.RollDamage(damageDice, damageMod);
                int totalDamage = outcome switch
                {
                    RollOutcome.CriticalHit => damageRoll.Total * _rules.Combat.CriticalMultiplier,
                    RollOutcome.GlancingHit => Math.Max(1, damageRoll.Total / 2),
                    _ => damageRoll.Total
                };
                totalDamage = Math.Max(1, totalDamage);
                allDice.Add(damageRoll);

                if (target.Hp.HasValue)
                {
                    var oldHp = target.Hp.Value;
                    target.Hp = Math.Max(0, target.Hp.Value - totalDamage);
                    allSC.Add(new StateChange { EntityType = "Npc", EntityId = target.Id, Property = "Hp", OldValue = oldHp.ToString(), NewValue = target.Hp.Value.ToString() });
                }

                mech.Add(DescribePlayerHit(target.Name, totalDamage, outcome, flavorSeed));

                // Target killed?
                if (target.Hp.HasValue && target.Hp.Value <= 0)
                {
                    mech.Add(PickFlavor(KillVerbs, flavorSeed + 99).Replace("{0}", target.Name));
                    totalXp += target.Level * 10;
                    if (_dice.Roll("1d100", "Loot").Total <= (int)(_rules.Loot.EnemyDropChance * 100))
                        totalGold += _dice.Roll("1d20", "Gold").Total + (target.Level * 2);
                    foreach (var lootItem in target.LootTable)
                    {
                        var clone = CloneInventoryItem(lootItem, Math.Max(1, lootItem.Quantity));
                        player.Inventory.Add(clone); totalLoot.Add(clone);
                        mech.Add($"  🎁 {lootItem.Name}!");
                    }
                    room.Npcs.Remove(target);
                    combat?.TurnOrder.RemoveAll(t => t.Id == target.Id);

                    // Check if all enemies dead
                    if (!enemies.Any(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)))
                    { combatOver = true; continue; }
                    continue; // skip enemy turn for this exchange (target is dead)
                }
            }

            // ── Enemy counterattacks ──
            foreach (var enemy in enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)))
            {
                int eSeed = flavorSeed + enemy.Name.Length * 5;
                int atkBonus = enemy.AttackBonus ?? 0;
                var eRoll = _dice.RollAttack(atkBonus);
                allDice.Add(eRoll);

                if (eRoll.IsFumble) { mech.Add($"{enemy.Name} stumbles — fumble! 💀"); continue; }
                if (eRoll.Total < player.Defense && !eRoll.IsCritical) { mech.Add(DescribeEnemyMiss(enemy.Name, eSeed)); continue; }

                string eDmgDice = enemy.DamageDice ?? "1d4";
                var eDmgRoll = _dice.Roll(eDmgDice, $"{enemy.Name} dmg");
                int eDmg = eRoll.IsCritical ? eDmgRoll.Total * _rules.Combat.CriticalMultiplier : eDmgRoll.Total;
                eDmg = Math.Max(1, eDmg);
                allDice.Add(eDmgRoll);

                var oldPHp = player.Hp;
                player.Hp = Math.Max(0, player.Hp - eDmg);
                allSC.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp", OldValue = oldPHp.ToString(), NewValue = player.Hp.ToString() });
                mech.Add(DescribeEnemyHit(enemy.Name, eDmg, eRoll.IsCritical, eSeed));

                if (player.Hp <= 0) { mech.Add(PickFlavor(DefeatVerbs, eSeed)); combatOver = true; break; }
            }
        }

        // ─── Post-combat resolution ───
        var allEnemiesAlive = enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)).ToList();
        string combatStatus = "ongoing";

        if (player.Hp <= 0)
        {
            combatStatus = "defeat";
            player.Interaction.Reset();
            if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, player.ActiveWorldId, ct);
            // Auto-respawn at tavern with penalties
            var killer = enemies.FirstOrDefault(e => e.Hp.HasValue && e.Hp.Value > 0)?.Name ?? "the enemy";
            var (deathMsg, deathChanges) = await HandlePlayerDeathAsync(player, room, killer, ct);
            mech.Add("");
            mech.Add(deathMsg);
            allSC.AddRange(deathChanges);
        }
        else if (allEnemiesAlive.Count == 0)
        {
            combatStatus = "victory";
            player.Xp += totalXp;
            player.Gold += totalGold;
            player.Interaction.Reset();
            if (combat is not null) await _stateManager.RemoveCombatStateAsync(room.Id, player.ActiveWorldId, ct);
            // Rewards line
            var rewards = new List<string>();
            if (totalXp > 0) rewards.Add($"+{totalXp} XP");
            if (totalGold > 0) rewards.Add($"+{totalGold} gold");
            if (rewards.Count > 0) mech.Add($"\n**Rewards:** {string.Join(" | ", rewards)}");
            var lvlUp = CheckAndApplyLevelUp(player);
            if (lvlUp is not null) mech.Add(lvlUp);
        }
        else
        {
            // Momentum indicator + HP bars
            var primaryEnemy = allEnemiesAlive.FirstOrDefault();
            if (primaryEnemy?.Hp.HasValue == true)
            {
                var momentum = GetMomentumText(player.Hp, player.MaxHp, primaryEnemy.Hp.Value, primaryEnemy.MaxHp ?? primaryEnemy.Hp.Value);
                if (!string.IsNullOrEmpty(momentum)) mech.Add(momentum);
            }
            AppendHpBars(mech, player, enemies, room);
            mech.Add("> attack | power attack | defend | aimed strike | use <potion> | flee");

            if (combat is not null)
            {
                combat.RoundNumber++;
                var pp = combat.TurnOrder.FirstOrDefault(t => t.IsPlayer);
                if (pp is not null) pp.Hp = player.Hp;
                foreach (var ep in combat.TurnOrder.Where(t => !t.IsPlayer))
                { var npc = enemies.FirstOrDefault(e => e.Id == ep.Id); if (npc?.Hp.HasValue == true) ep.Hp = npc.Hp.Value; }
                await _stateManager.SaveCombatStateAsync(combat, ct);
            }
        }

        var summary = string.Join("\n", mech);
        var combatResult = new ActionResult
        {
            ActionId = action.Id, RawInput = action.RawInput, Success = true,
            MechanicalSummary = summary, DiceRolls = allDice, StateChanges = allSC,
            XpGained = totalXp, GoldChange = totalGold, ItemsGained = totalLoot,
            InteractionUpdate = new InteractionUpdate
            { Mode = combatStatus == "ongoing" ? InteractionMode.Combat : InteractionMode.Explore, CombatStatus = combatStatus }
        };

        await _stateManager.SavePlayerAsync(player, ct);
        await _stateManager.SaveRoomAsync(room, ct);
        await PersistInteractionStoryEntry(player, action, combatResult, ct);
        return combatResult;
    }

    /// <summary>Handles player death: respawn at tavern with full HP/MP, no penalties beyond the trip back.</summary>
    private async Task<(string summary, List<StateChange> changes)> HandlePlayerDeathAsync(
        PlayerCharacter player, Room room, string defeatedBy, CancellationToken ct)
    {
        var oldHp = player.Hp;
        var oldMp = player.Mp;
        var oldRoom = player.CurrentRoomId;

        // Respawn at full HP/MP — death is a setback, not a punishment
        player.Hp = player.MaxHp;
        player.Mp = player.MaxMp;

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
            new() { EntityType = "Player", EntityId = player.Id, Property = "CurrentRoomId", OldValue = oldRoom, NewValue = "spawn" }
        };

        var summary = $"You have been defeated by {defeatedBy}! Everything goes dark...\n" +
            $"You awaken in The Rusted Flagon, battered but alive. A kindly stranger must have dragged you to safety.\n" +
            $"\u2764\uFE0F {player.Hp}/{player.MaxHp}  \u2728 {player.Mp}/{player.MaxMp}  \U0001F4B0 {player.Gold}g\n" +
            $"Your gear and experience are intact — but that foe still lurks out there...";

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
            WorldId = player.ActiveWorldId,
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

    /// <summary>
    /// Overlays world-scoped NPC state onto the canonical NPC before a conversation.
    /// If a WorldNpcState exists for this NPC/world/player, it overrides disposition.
    /// </summary>
    private async Task OverlayWorldNpcStateAsync(Npc npc, string worldId, string? playerId, CancellationToken ct)
    {
        if (_worldRepository is null) return;

        var worldState = await _worldRepository.GetWorldNpcStateAsync(npc.Id, worldId, playerId, ct);
        if (worldState is null) return;

        npc.DispositionState = worldState.DispositionState;
        npc.Disposition = npc.DispositionState.ToFlatDisposition();
        if (worldState.KnowledgeScopeOverrides is { Count: > 0 })
            npc.KnowledgeScopes = worldState.KnowledgeScopeOverrides;
    }

    /// <summary>
    /// Persists the NPC's current disposition into world-scoped state after an interaction.
    /// </summary>
    private async Task PersistWorldNpcStateAsync(Npc npc, string worldId, string? playerId, CancellationToken ct)
    {
        if (_worldRepository is null) return;

        var worldState = await _worldRepository.GetWorldNpcStateAsync(npc.Id, worldId, playerId, ct)
                          ?? new WorldNpcState { NpcId = npc.Id, WorldId = worldId, PlayerId = playerId };

        worldState.DispositionState = npc.DispositionState;
        await _worldRepository.SaveWorldNpcStateAsync(worldState, ct);
    }

    private void ApplyFreeFormStatChanges(PlayerCharacter player, FreeFormResponse freeForm, ActionResult? result)
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
                    // Check for level-up
                    if (delta > 0)
                    {
                        var lvlUp = CheckAndApplyLevelUp(player);
                        if (lvlUp is not null && result is not null)
                            result.MechanicalSummary += $"\n{lvlUp}";
                    }
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
            WorldId = player.ActiveWorldId,
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

    private ActionResult ProcessJournal(PlayerCharacter player, GameAction action)
    {
        if (_questEngine is null)
            return new ActionResult { ActionId = action.Id, Success = true, MechanicalSummary = "Quest system not available." };

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = _questEngine.FormatJournal(player)
        };
    }

    private ActionResult ProcessCompletedQuests(PlayerCharacter player, GameAction action)
    {
        var completed = player.QuestLog.Where(q => q.Status == QuestStatus.Completed).ToList();
        if (_registry is null || completed.Count == 0)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = true,
                MechanicalSummary = "You have not completed any quests yet."
            };
        }

        var lines = completed
            .Select(q => $"  ✅ {_registry.Quests.GetById(q.QuestId)?.Name ?? q.QuestId}")
            .ToList();

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"📕 **Completed Quests** ({completed.Count})\n" + string.Join("\n", lines)
        };
    }

    private ActionResult ProcessQuestInfo(PlayerCharacter player, GameAction action)
    {
        if (_questEngine is null || string.IsNullOrWhiteSpace(action.Target))
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "Usage: quest <name>" };

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = _questEngine.FormatQuestInfo(player, action.Target)
        };
    }

    private async Task<ActionResult> ProcessAcceptQuestAsync(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        if (_questEngine is null || string.IsNullOrWhiteSpace(action.Target))
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "Usage: accept <quest name>" };

        // Find the quest by name or ID
        var quest = FindQuestByNameOrId(action.Target);
        if (quest is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"No quest matching '{action.Target}' found." };

        // Check if the quest giver is in the current room
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId, ct);
        var giver = room?.Npcs.FirstOrDefault(n => n.Id.Equals(quest.GiverId, StringComparison.OrdinalIgnoreCase));
        if (giver is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "The quest giver is not in this room." };

        var (canAccept, reason) = _questEngine.CanAcceptQuest(player, quest.Id, giver);
        if (!canAccept)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = reason ?? "Cannot accept this quest." };

        var progress = await _questEngine.AcceptQuestAsync(player, quest.Id, narratorDescription: null, ct);
        if (progress is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "Failed to accept quest." };

        await _stateManager.SavePlayerAsync(player, ct);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"📜 Quest accepted: **{quest.Name}**\n{quest.Description}",
            StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "QuestLog", NewValue = $"accepted {quest.Name}" }]
        };
    }

    private async Task<ActionResult> ProcessAbandonQuest(PlayerCharacter player, GameAction action, CancellationToken ct)
    {
        if (_questEngine is null || string.IsNullOrWhiteSpace(action.Target))
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = "Usage: abandon <quest name>" };

        // Find by name or ID in the player's active quests
        var progress = player.QuestLog.FirstOrDefault(q =>
            q.Status == QuestStatus.Active &&
            (q.QuestId.Equals(action.Target, StringComparison.OrdinalIgnoreCase) ||
             (_registry?.Quests.GetById(q.QuestId)?.Name.Contains(action.Target, StringComparison.OrdinalIgnoreCase) == true)));

        if (progress is null)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"No active quest matching '{action.Target}'." };

        var quest = _registry?.Quests.GetById(progress.QuestId);
        await _questEngine.AbandonQuestAsync(player, progress.QuestId, ct);

        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = $"📜 Quest abandoned: **{quest?.Name ?? progress.QuestId}**",
            StateChanges = [new StateChange { EntityType = "Player", EntityId = player.Id, Property = "QuestLog", NewValue = $"abandoned {quest?.Name ?? progress.QuestId}" }]
        };
    }

    private QuestDefinition? FindQuestByNameOrId(string nameOrId)
    {
        if (_registry is null) return null;

        // Try by ID first
        var quest = _registry.Quests.GetById(nameOrId);
        if (quest is not null) return quest;

        // Try by name (partial match)
        return _registry.Quests.GetAll()
            .FirstOrDefault(q => q.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));
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
                `attack <target>` -- Attack an NPC (3 exchanges per round)
                `power attack` -- Reckless swing: -3 accuracy, +5 damage
                `aimed strike` -- Focus your aim: advantage on first hit
                `defend` -- Brace for impact: +4 defense, skip your attacks
                `flee` -- Run from combat (enemies get a free hit)
                `talk to <target>` -- Talk to an NPC
                `take <item>` -- Pick up an item
                `drop <item>` -- Drop an item from inventory
                `use <item>` -- Use a consumable item
                `shop` / `browse` -- View a shopkeeper's wares and prices
                `buy <item>` -- Buy from a shopkeeper
                `sell <item>` -- Sell an item to a shopkeeper
                `equip <item>` -- Equip an item
                `unequip <item>` -- Unequip an item
                `cast <spell>` / `cast <spell> at <target>` -- Cast a spell (learns new spells!)
                `spellbook` / `spells` -- View your learned spells
                `rest` / `short rest` / `long rest` -- Rest and recover
                `inventory` -- View your inventory
                `stats` -- View your character stats
                `map` -- View ASCII world map of discovered rooms
                `journal` / `quests` -- View your quest journal
                `quest <name>` -- View details for a specific quest
                `accept <quest>` -- Accept a quest
                `abandon <quest>` -- Abandon an active quest
                `travel to world <world-id>` -- Transfer to another world
                `enter portal` -- Use an active portal in the current room
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

    // Matches both flat numbers ("Restores 25 HP") and dice expressions ("Restores 2d4+2 HP")
    private static readonly System.Text.RegularExpressions.Regex EffectHpRegex = new(@"[Rr]estores?\s+((?:\d+d\d+(?:[+\-]\d+)?|\d+))\s*(?:HP|hit\s*points?|health)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex EffectMpRegex = new(@"[Rr]estores?\s+((?:\d+d\d+(?:[+\-]\d+)?|\d+))\s*(?:MP|mana|magic)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex EffectDamageRegex = new(@"[Dd]eals?\s+((?:\d+d\d+(?:[+\-]\d+)?|\d+))\s*(?:damage|HP\s*damage)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Parses an item's Effect string into mechanical HP/MP deltas.
    /// Supports both flat numbers ("Restores 25 HP") and dice expressions ("Restores 2d4+2 HP").
    /// Dice expressions are rolled via the dice service; flat numbers are used directly.
    /// </summary>
    private (int HpDelta, int MpDelta) ParseItemEffect(string? effect)
    {
        if (string.IsNullOrWhiteSpace(effect))
            return (0, 0);

        int hp = 0, mp = 0;

        var hpMatch = EffectHpRegex.Match(effect);
        if (hpMatch.Success)
            hp = ResolveEffectValue(hpMatch.Groups[1].Value);

        var mpMatch = EffectMpRegex.Match(effect);
        if (mpMatch.Success)
            mp = ResolveEffectValue(mpMatch.Groups[1].Value);

        var dmgMatch = EffectDamageRegex.Match(effect);
        if (dmgMatch.Success)
            hp = -ResolveEffectValue(dmgMatch.Groups[1].Value);

        return (hp, mp);
    }

    /// <summary>Resolves a value that may be a flat integer or a dice expression like "2d4+2".</summary>
    private int ResolveEffectValue(string value)
    {
        if (int.TryParse(value, out var flat))
            return flat;

        // It's a dice expression — roll it
        var roll = _dice.Roll(value, "Item effect");
        _logger.LogInformation("Rolled item effect '{Expression}' = {Total}", value, roll.Total);
        return Math.Max(1, roll.Total);
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

        // Level gate: block items more than 1 level above the player
        if (shopItem.Level > player.Level + 1)
            return new ActionResult { ActionId = action.Id, Success = false, MechanicalSummary = $"{shopItem.Name} requires level {shopItem.Level - 1} to purchase, but you're only level {player.Level}." };

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

        // Auto-equip if the item is equippable and the relevant slot is empty
        string? equipNote = null;
        if (boughtItem.IsEquippable && existingStack is null)
        {
            var equipped = TryAutoEquip(player, boughtItem);
            if (equipped)
                equipNote = $" Equipped {boughtItem.Name}.";
        }

        await _stateManager.SaveRoomAsync(room, ct);

        var mechMsg = $"Bought {shopItem.Name} for {price} gold. You have {player.Gold} gold remaining.{equipNote ?? ""}";
        return new ActionResult
        {
            ActionId = action.Id,
            Success = true,
            MechanicalSummary = mechMsg,
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

        if (item.Type == ItemType.QuestItem)
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "Something tells you this is important. You'd better hold onto it."
            };
        }

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

        await PersistWorldNpcStateAsync(shopkeeper, player.ActiveWorldId, player.Id, ct);
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

    // ─── Room Description Helper ─────────────────────────────────────────

    private static string BuildRoomSummary(string header, Room room)
    {
        var sb = new StringBuilder();
        sb.AppendLine(header);
        if (!string.IsNullOrWhiteSpace(room.Description))
        {
            sb.AppendLine();
            sb.AppendLine(room.Description.Trim());
        }
        if (room.Npcs.Count > 0)
        {
            sb.AppendLine();
            foreach (var npc in room.Npcs)
            {
                var icon = npc.IsHostile ? "!" : npc.IsShopkeeper ? "$" : "-";
                sb.AppendLine($"  {icon} {npc.Name}");
            }
        }
        if (room.Items.Count > 0)
        {
            foreach (var item in room.Items)
                sb.AppendLine($"  * {item.Name}{(item.Quantity > 1 ? $" (x{item.Quantity})" : "")}");
        }
        if (room.Exits.Count > 0)
        {
            sb.AppendLine();
            sb.Append("**Exits:** ");
            sb.AppendLine(string.Join(", ", room.Exits.Keys));
        }
        return sb.ToString().TrimEnd();
    }

    // ─── Combat Flavor (no damage numbers — HP bars tell the story) ─────

    private static readonly string[] PlayerHitVerbs = [
        "You slash {0}!",
        "You strike {0}!",
        "Your blade bites into {0}!",
        "You land a solid hit on {0}!",
        "A clean strike catches {0}!",
        "You drive your weapon into {0}!",
        "You lash out at {0} — a direct hit!",
        "Your weapon finds its mark on {0}!"
    ];
    private static readonly string[] PlayerCritVerbs = [
        "**CRITICAL!** You devastate {0}!",
        "**CRITICAL!** You find a weak point in {0}'s defenses!",
        "**CRIT!** A bone-crushing blow smashes into {0}!",
        "**CRITICAL!** Your weapon tears through {0}!",
        "**CRIT!** A devastating blow lands on {0}!"
    ];
    private static readonly string[] PlayerGlanceVerbs = [
        "You barely nick {0}.",
        "A glancing blow scrapes {0}.",
        "Your strike grazes {0}.",
        "You clip {0} — barely a scratch."
    ];
    private static readonly string[] PlayerMissVerbs = [
        "{0} sidesteps your attack!",
        "You swing wide — {0} dodges!",
        "Your strike glances off {0}'s guard!",
        "{0} ducks under your swing!",
        "You lunge at {0}, but miss!",
        "Your weapon cuts nothing but air!"
    ];
    private static readonly string[] PlayerFumbleVerbs = [
        "You stumble, leaving yourself exposed!",
        "Your grip slips mid-swing — fumble!",
        "You overcommit and nearly lose your footing!",
        "Your weapon catches on something — fumble!"
    ];
    private static readonly string[] EnemyHitVerbs = [
        "{0} strikes you!",
        "{0} lands a blow!",
        "{0} connects hard!",
        "{0} catches you off-guard!"
    ];
    private static readonly string[] EnemyMissVerbs = [
        "{0} swings and misses!",
        "You dodge {0}'s attack!",
        "{0} lunges but you sidestep!",
        "{0}'s strike goes wide!"
    ];
    private static readonly string[] KillVerbs = [
        "**{0} falls!** The fight is over.",
        "**{0} crumbles!** Your weapon finds its mark one last time.",
        "**{0} staggers... and collapses.** It's done.",
        "**{0} has been defeated!** The killing blow lands true."
    ];
    private static readonly string[] DefeatVerbs = [
        "**You collapse!** Everything goes dark...",
        "**You fall!** The world fades to black...",
        "**Defeated!** Your vision tunnels as you hit the ground..."
    ];

    private static string PickFlavor(string[] pool, int seed)
        => pool[Math.Abs(seed) % pool.Length];

    private static string DescribePlayerHit(string target, int damage, RollOutcome outcome, int seed)
    {
        var template = outcome switch
        {
            RollOutcome.CriticalHit => PickFlavor(PlayerCritVerbs, seed),
            RollOutcome.GlancingHit => PickFlavor(PlayerGlanceVerbs, seed),
            _ => PickFlavor(PlayerHitVerbs, seed)
        };
        return string.Format(template, target);
    }

    private static string DescribePlayerMiss(string target, RollOutcome outcome, int seed)
        => outcome == RollOutcome.CriticalMiss
            ? PickFlavor(PlayerFumbleVerbs, seed)
            : string.Format(PickFlavor(PlayerMissVerbs, seed), target);

    private static string DescribeEnemyHit(string enemyName, int damage, bool isCrit, int seed)
    {
        var verb = GetEnemyVerb(enemyName, seed);
        return isCrit ? $"**CRIT!** {verb}" : verb;
    }

    private static string GetEnemyVerb(string name, int seed)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("wolf") || lower.Contains("fang"))
        {
            string[] pool = [$"{name} sinks its fangs into you!", $"{name} lunges with snapping jaws!", $"{name} rakes you with claws!"];
            return PickFlavor(pool, seed);
        }
        if (lower.Contains("skeleton") || lower.Contains("hollow"))
        {
            string[] pool = [$"{name} swings a rusty blade!", $"{name} slashes with bony claws!", $"{name} strikes with a corroded weapon!"];
            return PickFlavor(pool, seed);
        }
        if (lower.Contains("boar") || lower.Contains("tusk") || lower.Contains("goretusk"))
        {
            string[] pool = [$"{name} gores you with massive tusks!", $"{name} charges, slamming into you!", $"{name} rams you with terrifying force!"];
            return PickFlavor(pool, seed);
        }
        if (lower.Contains("cultist") || lower.Contains("acolyte"))
        {
            string[] pool = [$"{name} slashes at you with dark energy!", $"{name} channels malice into a strike!", $"{name} lashes out with a cruel blow!"];
            return PickFlavor(pool, seed);
        }
        return string.Format(PickFlavor(EnemyHitVerbs, seed), name);
    }

    private static string DescribeEnemyMiss(string enemyName, int seed)
    {
        var lower = enemyName.ToLowerInvariant();
        if (lower.Contains("wolf") || lower.Contains("fang"))
        {
            string[] pool = [$"{enemyName} snaps at you but bites air!", $"You leap back from {enemyName}'s jaws!", $"{enemyName} pounces — you roll aside!"];
            return PickFlavor(pool, seed);
        }
        return string.Format(PickFlavor(EnemyMissVerbs, seed), enemyName);
    }

    private static string GetMomentumText(int playerHp, int playerMaxHp, int enemyHp, int enemyMaxHp)
    {
        double playerPct = (double)playerHp / playerMaxHp;
        double enemyPct = (double)enemyHp / enemyMaxHp;
        if (enemyPct <= 0.25 && playerPct > 0.3) return "*You sense victory is close!*";
        if (playerPct <= 0.25 && enemyPct > 0.3) return "*Things are looking grim... heal or flee!*";
        if (playerPct <= 0.15) return "*You're barely standing!*";
        return "";
    }

    // ─── Combat Helpers ────────────────────────────────────────────────────

    /// <summary>Runs a single round of enemy attacks against the player.</summary>
    private void RunEnemyAttacks(PlayerCharacter player, List<Npc> enemies, Room room,
        List<string> mech, List<DiceRoll> dice, List<StateChange> sc, int defenseBonus)
    {
        int effectiveDefense = player.Defense + defenseBonus;
        int idx = 0;
        foreach (var enemy in enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)))
        {
            int eSeed = idx * 11 + enemy.Name.Length * 3;
            int atkBonus = enemy.AttackBonus ?? 0;
            var eRoll = _dice.RollAttack(atkBonus);
            dice.Add(eRoll);

            if (eRoll.IsFumble) { mech.Add($"{enemy.Name} stumbles — fumble! 💀"); idx++; continue; }
            if (eRoll.Total < effectiveDefense && !eRoll.IsCritical)
            { mech.Add(DescribeEnemyMiss(enemy.Name, eSeed)); idx++; continue; }

            string eDmgDice = enemy.DamageDice ?? "1d4";
            var eDmgRoll = _dice.Roll(eDmgDice, $"{enemy.Name} dmg");
            int eDmg = eRoll.IsCritical ? eDmgRoll.Total * _rules.Combat.CriticalMultiplier : eDmgRoll.Total;
            eDmg = Math.Max(1, eDmg);
            dice.Add(eDmgRoll);

            var oldHp = player.Hp;
            player.Hp = Math.Max(0, player.Hp - eDmg);
            sc.Add(new StateChange { EntityType = "Player", EntityId = player.Id, Property = "Hp",
                OldValue = oldHp.ToString(), NewValue = player.Hp.ToString() });
            mech.Add(DescribeEnemyHit(enemy.Name, eDmg, eRoll.IsCritical, eSeed));

            if (player.Hp <= 0) { mech.Add(PickFlavor(DefeatVerbs, eSeed)); break; }
            idx++;
        }
    }

    /// <summary>Generates a code-block HP bar panel for all combatants (console-style).</summary>
    private static void AppendHpBars(List<string> mech, PlayerCharacter player, List<Npc> enemies, Room room)
    {
        var sb = new StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine(FormatHpBar(player.Name, player.Hp, player.MaxHp));
        foreach (var enemy in enemies.Where(e => e.Hp.HasValue && e.Hp.Value > 0 && room.Npcs.Contains(e)))
            sb.AppendLine(FormatHpBar(enemy.Name, enemy.Hp!.Value, enemy.MaxHp ?? enemy.Hp!.Value));
        sb.Append("```");
        mech.Add(sb.ToString());
    }

    private static string FormatHpBar(string name, int hp, int maxHp)
    {
        const int BarWidth = 20;
        int filled = maxHp > 0 ? (int)Math.Round((double)hp / maxHp * BarWidth) : 0;
        filled = Math.Clamp(filled, 0, BarWidth);
        var bar = new string('#', filled) + new string('-', BarWidth - filled);
        return $"  {name,-18} [{bar}] {hp,3}/{maxHp} HP";
    }

    // ─── Auto-Equip Helper ────────────────────────────────────────────────

    /// <summary>
    /// Tries to auto-equip an item if its slot is empty. Returns true if equipped.
    /// Removes the item from inventory on success.
    /// </summary>
    private static bool TryAutoEquip(PlayerCharacter player, InventoryItem item)
    {
        if (!item.IsEquippable) return false;

        bool slotEmpty = item.Type switch
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

        if (!slotEmpty) return false;

        player.Equipment.Equip(item, out _);
        player.Inventory.Remove(item);
        return true;
    }

    // ─── Level Scaling ───────────────────────────────────────────────────

    /// <summary>
    /// Recalculates MaxHp and MaxMp based on current level and stats.
    /// Formula: base * (1 + scalePerLevel * (level - 1)) + statMod.
    /// Called on player load (to fix existing characters) and on level-up.
    /// Does NOT touch current HP/MP — callers decide whether to heal.
    /// </summary>
    private void RecalculateMaxHpMp(PlayerCharacter player)
    {
        // Enforce stat caps (protects against inflated values from free-form RP)
        int statMax = _rules.Stats.GetValueOrDefault("str")?.Max ?? 20;
        player.Str = Math.Clamp(player.Str, 1, statMax);
        player.Dex = Math.Clamp(player.Dex, 1, statMax);
        player.Con = Math.Clamp(player.Con, 1, statMax);
        player.Int = Math.Clamp(player.Int, 1, statMax);
        player.Wis = Math.Clamp(player.Wis, 1, statMax);
        player.Cha = Math.Clamp(player.Cha, 1, statMax);
        player.Luck = Math.Clamp(player.Luck, 1, statMax);

        int hpBase = _rules.Stats.GetValueOrDefault("hp")?.Base ?? 20;
        int mpBase = _rules.Stats.GetValueOrDefault("mp")?.Base ?? 10;
        int conMod = PlayerCharacter.GetStatModifier(player.Con);
        int intMod = PlayerCharacter.GetStatModifier(player.Int);
        double hpScale = _rules.Leveling.HpScalePerLevel;
        double mpScale = _rules.Leveling.MpScalePerLevel;
        int bonusLevels = Math.Max(0, player.Level - 1);

        // Base + stat modifier, then scale up by percentage per bonus level
        int baseHp = hpBase + conMod;
        int baseMp = mpBase + intMod;
        player.MaxHp = Math.Max(1, (int)(baseHp * (1.0 + hpScale * bonusLevels)));
        player.MaxMp = Math.Max(0, (int)(baseMp * (1.0 + mpScale * bonusLevels)));

        // Clamp current values
        player.Hp = Math.Min(player.Hp, player.MaxHp);
        player.Mp = Math.Min(player.Mp, player.MaxMp);
    }

    /// <summary>
    /// Returns the XP required to advance from the given level to the next.
    /// Formula: BaseXpPerLevel * level.
    /// </summary>
    private int XpRequiredForLevel(int currentLevel)
        => _rules.Leveling.BaseXpPerLevel * currentLevel;

    /// <summary>
    /// Checks if the player has enough XP to level up. May trigger multiple level-ups.
    /// Returns a description of level-ups that occurred, or null if none.
    /// Heals the player to full on level-up.
    /// </summary>
    private string? CheckAndApplyLevelUp(PlayerCharacter player)
    {
        int maxLevel = _rules.Leveling.MaxLevel;
        if (player.Level >= maxLevel) return null;

        var levelUps = new List<string>();
        while (player.Level < maxLevel)
        {
            int xpNeeded = XpRequiredForLevel(player.Level);
            if (player.Xp < xpNeeded) break;

            player.Xp -= xpNeeded;
            player.Level++;

            int oldMaxHp = player.MaxHp;
            int oldMaxMp = player.MaxMp;
            RecalculateMaxHpMp(player);

            // Full heal on level-up
            player.Hp = player.MaxHp;
            player.Mp = player.MaxMp;

            levelUps.Add($"LEVEL UP! You are now level {player.Level}! " +
                         $"MaxHP: {oldMaxHp} → {player.MaxHp}, MaxMP: {oldMaxMp} → {player.MaxMp}. " +
                         $"You feel refreshed — HP and MP fully restored!");
        }

        return levelUps.Count > 0 ? string.Join("\n", levelUps) : null;
    }
}
