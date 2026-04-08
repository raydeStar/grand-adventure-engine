using System.Text.RegularExpressions;
using GAE.Core.Models;
using Microsoft.Extensions.Logging;

namespace GAE.Engine;

public partial class CommandParser
{
    private readonly ILogger<CommandParser> _logger;

    public CommandParser(ILogger<CommandParser> logger)
    {
        _logger = logger;
    }

    public GameAction Parse(string playerId, string rawInput)
    {
        var input = rawInput.Trim();
        var action = new GameAction
        {
            PlayerId = playerId,
            RawInput = input
        };

        if (string.IsNullOrWhiteSpace(input))
        {
            action.Type = ActionType.Unknown;
            return action;
        }

        var travelWorldMatch = TravelWorldRegex().Match(input);
        if (travelWorldMatch.Success)
        {
            action.Type = ActionType.TravelWorld;
            action.Target = travelWorldMatch.Groups["world"].Value.Trim();
            return action;
        }

        var portalTravelMatch = PortalTravelRegex().Match(input);
        if (portalTravelMatch.Success)
        {
            action.Type = ActionType.TravelWorld;
            if (portalTravelMatch.Groups["world"].Success)
                action.Target = portalTravelMatch.Groups["world"].Value.Trim();
            action.Parameters["travelMode"] = "portal";
            return action;
        }

        // Movement — "go north", "head south", or just bare "north", "n", etc.
        var moveMatch = MoveRegex().Match(input);
        if (moveMatch.Success)
        {
            action.Type = ActionType.Move;
            action.Direction = NormalizeDirection(moveMatch.Groups["dir"].Value);
            return action;
        }
        var bareMove = BareDirectionRegex().Match(input);
        if (bareMove.Success)
        {
            action.Type = ActionType.Move;
            action.Direction = NormalizeDirection(bareMove.Groups["dir"].Value);
            return action;
        }
        // "leave", "exit", "leave tavern", "go outside", "depart", etc. — auto-resolve direction
        if (LeaveRegex().IsMatch(input))
        {
            action.Type = ActionType.Move;
            action.Direction = "auto";
            return action;
        }

        // Look / examine
        if (LookRegex().IsMatch(input))
        {
            action.Type = ActionType.Look;
            if (RoomLookAliasRegex().IsMatch(input))
                return action;

            var lookTarget = LookTargetRegex().Match(input);
            if (lookTarget.Success)
            {
                var target = lookTarget.Groups["target"].Value.Trim();
                if (!RoomTargetAliasRegex().IsMatch(target))
                    action.Target = target;
            }
            return action;
        }

        // Combat special moves (must be checked before generic Attack)
        var powerAttackMatch = PowerAttackRegex().Match(input);
        if (powerAttackMatch.Success)
        {
            action.Type = ActionType.PowerAttack;
            if (powerAttackMatch.Groups["target"].Success)
                action.Target = powerAttackMatch.Groups["target"].Value.Trim();
            return action;
        }
        if (DefendRegex().IsMatch(input))
        {
            action.Type = ActionType.Defend;
            return action;
        }
        var aimedMatch = AimedStrikeRegex().Match(input);
        if (aimedMatch.Success)
        {
            action.Type = ActionType.AimedStrike;
            if (aimedMatch.Groups["target"].Success)
                action.Target = aimedMatch.Groups["target"].Value.Trim();
            return action;
        }
        if (FleeRegex().IsMatch(input))
        {
            action.Type = ActionType.Flee;
            return action;
        }

        // Attack
        var attackMatch = AttackRegex().Match(input);
        if (attackMatch.Success)
        {
            action.Type = ActionType.Attack;
            action.Target = attackMatch.Groups["target"].Value.Trim();
            return action;
        }
        // Bare "attack" / "fight" without a target — auto-target in engine
        if (BareAttackRegex().IsMatch(input))
        {
            action.Type = ActionType.Attack;
            return action;
        }

        // Talk
        var talkMatch = TalkRegex().Match(input);
        if (talkMatch.Success)
        {
            action.Type = ActionType.Talk;
            action.Target = talkMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Journal / Quest log (before take/drop to avoid conflicts)
        if (JournalRegex().IsMatch(input))
        {
            action.Type = ActionType.Journal;
            return action;
        }

        if (CompletedQuestsRegex().IsMatch(input))
        {
            action.Type = ActionType.CompletedQuests;
            return action;
        }

        // Quest info — "quest <name>"
        var questInfoMatch = QuestInfoRegex().Match(input);
        if (questInfoMatch.Success)
        {
            action.Type = ActionType.QuestInfo;
            action.Target = questInfoMatch.Groups["quest"].Value.Trim();
            return action;
        }

        // Accept quest — before Take so "take quest <name>" routes here
        var acceptMatch = AcceptQuestRegex().Match(input);
        if (acceptMatch.Success)
        {
            action.Type = ActionType.AcceptQuest;
            action.Target = acceptMatch.Groups["quest"].Value.Trim();
            return action;
        }

        // Turn in quest — "turn in <name>", "complete quest <name>", "finish quest <name>"
        var turnInMatch = TurnInQuestRegex().Match(input);
        if (turnInMatch.Success)
        {
            action.Type = ActionType.TurnInQuest;
            action.Target = turnInMatch.Groups["quest"].Value.Trim();
            return action;
        }

        // Abandon quest — before Drop so "drop quest <name>" routes here
        var abandonMatch = AbandonQuestRegex().Match(input);
        if (abandonMatch.Success)
        {
            action.Type = ActionType.AbandonQuest;
            action.Target = abandonMatch.Groups["quest"].Value.Trim();
            return action;
        }

        // Take / pick up
        var takeMatch = TakeRegex().Match(input);
        if (takeMatch.Success)
        {
            action.Type = ActionType.Take;
            action.Target = takeMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Drop
        var dropMatch = DropRegex().Match(input);
        if (dropMatch.Success)
        {
            action.Type = ActionType.Drop;
            action.Target = dropMatch.Groups["target"].Value.Trim();
            return action;
        }

        var groundDropMatch = GroundDropRegex().Match(input);
        if (groundDropMatch.Success)
        {
            action.Type = ActionType.Drop;
            action.Target = groundDropMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Use
        var useMatch = UseRegex().Match(input);
        if (useMatch.Success)
        {
            action.Type = ActionType.Use;
            action.Target = useMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Shop / browse
        if (ShopRegex().IsMatch(input))
        {
            action.Type = ActionType.Shop;
            return action;
        }

        // Buy
        var buyMatch = BuyRegex().Match(input);
        if (buyMatch.Success)
        {
            action.Type = ActionType.Buy;
            action.Target = buyMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Sell
        var sellMatch = SellRegex().Match(input);
        if (sellMatch.Success)
        {
            action.Type = ActionType.Sell;
            action.Target = sellMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Equip
        var equipMatch = EquipRegex().Match(input);
        if (equipMatch.Success)
        {
            action.Type = ActionType.Equip;
            action.Target = equipMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Unequip
        var unequipMatch = UnequipRegex().Match(input);
        if (unequipMatch.Success)
        {
            action.Type = ActionType.Unequip;
            action.Target = unequipMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Rest
        if (LongRestRegex().IsMatch(input))
        {
            action.Type = ActionType.LongRest;
            return action;
        }
        if (ShortRestRegex().IsMatch(input))
        {
            action.Type = ActionType.ShortRest;
            return action;
        }
        if (RestRegex().IsMatch(input))
        {
            action.Type = ActionType.Rest;
            return action;
        }

        // Inventory
        if (InventoryRegex().IsMatch(input))
        {
            action.Type = ActionType.Inventory;
            return action;
        }

        // Stats
        if (StatsRegex().IsMatch(input))
        {
            action.Type = ActionType.Stats;
            return action;
        }

        // Cast
        var castMatch = CastRegex().Match(input);
        if (castMatch.Success)
        {
            action.Type = ActionType.Cast;
            action.Target = castMatch.Groups["spell"].Value.Trim();
            if (castMatch.Groups["target"].Success)
                action.Parameters["target"] = castMatch.Groups["target"].Value.Trim();
            return action;
        }

        // Map
        if (MapRegex().IsMatch(input))
        {
            action.Type = ActionType.Map;
            return action;
        }

        // Spellbook
        if (SpellbookRegex().IsMatch(input))
        {
            action.Type = ActionType.Spellbook;
            return action;
        }

        // Lorebook — "lorebook", "lore", "knowledge"
        if (LorebookRegex().IsMatch(input))
        {
            action.Type = ActionType.Lorebook;
            return action;
        }

        // Lore info — "lore <name>" to read specific lore
        var loreInfoMatch = LoreInfoRegex().Match(input);
        if (loreInfoMatch.Success)
        {
            action.Type = ActionType.LoreInfo;
            action.Target = loreInfoMatch.Groups["topic"].Value.Trim();
            return action;
        }

        // Narrator — "narrator", "narrators", "voice", "personality"
        if (NarratorListRegex().IsMatch(input))
        {
            action.Type = ActionType.Narrator;
            return action;
        }

        // Set narrator — "narrator <name>", "set narrator <name>"
        var narratorMatch = SetNarratorRegex().Match(input);
        if (narratorMatch.Success)
        {
            action.Type = ActionType.SetNarrator;
            action.Target = narratorMatch.Groups["name"].Value.Trim();
            return action;
        }

        // Help
        if (HelpRegex().IsMatch(input))
        {
            action.Type = ActionType.Help;
            return action;
        }

        action.Type = ActionType.Unknown;
        _logger.LogDebug("Could not parse command: {Input}", input);
        return action;
    }

    [GeneratedRegex(@"^(?:go|move|walk|head|travel)\s+(?<dir>north|south|east|west|northeast|northwest|southeast|southwest|up|down|ne|nw|se|sw|n|s|e|w|u|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex MoveRegex();

    [GeneratedRegex(@"^(?:(?:travel|shift|jump)\s+(?:to\s+)?(?:world|realm)\s+|(?:world|realm)\s+(?:travel|shift|jump)\s+)(?<world>[a-zA-Z0-9][a-zA-Z0-9_-]{1,63})$", RegexOptions.IgnoreCase)]
    private static partial Regex TravelWorldRegex();

    [GeneratedRegex(@"^(?:enter|use|take|step\s+through)\s+(?:the\s+)?(?:portal|gate|rift)(?:\s+to\s+(?<world>[a-zA-Z0-9][a-zA-Z0-9_-]{1,63}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PortalTravelRegex();

    [GeneratedRegex(@"^(?<dir>north|south|east|west|northeast|northwest|southeast|southwest|up|down|ne|nw|se|sw|n|s|e|w|u|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex BareDirectionRegex();

    [GeneratedRegex(@"^(?:leave|exit|depart|go\s+out(?:side)?|step\s+out(?:side)?|get\s+out)(?:\s+.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LeaveRegex();

    private static string NormalizeDirection(string dir) => dir.ToLowerInvariant() switch
    {
        "n" => "north",
        "s" => "south",
        "e" => "east",
        "w" => "west",
        "ne" => "northeast",
        "nw" => "northwest",
        "se" => "southeast",
        "sw" => "southwest",
        "u" => "up",
        "d" => "down",
        _ => dir.ToLowerInvariant()
    };

    [GeneratedRegex(@"^(?:look|l|examine|inspect|search)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LookRegex();

    [GeneratedRegex(@"^(?:l|look|look\s+around|look\s+at\s+(?:the\s+)?room|look\s+here|look\s+surroundings|examine\s+(?:the\s+)?room|inspect\s+(?:the\s+)?room|search|search\s+(?:the\s+)?room)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoomLookAliasRegex();

    [GeneratedRegex(@"^(?:room|the\s+room|around|here|surroundings)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoomTargetAliasRegex();

    [GeneratedRegex(@"(?:look|examine|inspect|search)\s+(?:(?:at|for)\s+)?(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LookTargetRegex();

    [GeneratedRegex(@"^(?:power\s+attack|heavy\s+(?:attack|strike|hit)|smash)(?:\s+(?<target>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PowerAttackRegex();

    [GeneratedRegex(@"^(?:defend|block|brace|guard|defensive\s+stance|raise\s+shield)$", RegexOptions.IgnoreCase)]
    private static partial Regex DefendRegex();

    [GeneratedRegex(@"^(?:aimed?\s+(?:strike|attack|shot)|focus\s+(?:attack|strike)|precise\s+(?:strike|attack|hit))(?:\s+(?<target>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AimedStrikeRegex();

    [GeneratedRegex(@"^(?:flee|run|escape|run\s+away|retreat|bail)$", RegexOptions.IgnoreCase)]
    private static partial Regex FleeRegex();

    [GeneratedRegex(@"^(?:attack|hit|strike|fight|slash)$", RegexOptions.IgnoreCase)]
    private static partial Regex BareAttackRegex();

    [GeneratedRegex(@"^(?:attack|hit|strike|fight|slash)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AttackRegex();

    [GeneratedRegex(@"^(?:talk|speak|chat)\s+(?:to\s+|with\s+)?(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TalkRegex();

    [GeneratedRegex(@"^(?:take|pick\s+up|grab|get)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TakeRegex();

    [GeneratedRegex(@"^(?:drop|discard|throw\s+away)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DropRegex();

    [GeneratedRegex(@"^(?:throw|toss|place|put|leave)\s+(?<target>.+?)\s+(?:on|onto|to|at)\s+(?:the\s+)?(?:ground|floor|dirt)$", RegexOptions.IgnoreCase)]
    private static partial Regex GroundDropRegex();

    [GeneratedRegex(@"^(?:use|consume|drink|eat)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UseRegex();

    [GeneratedRegex(@"^(?:shop|browse|wares|merchandise|what(?:'s| is) for sale|show me (?:your |the )?(?:wares|goods|inventory|merchandise|stock|shop))$", RegexOptions.IgnoreCase)]
    private static partial Regex ShopRegex();

    [GeneratedRegex(@"^(?:buy|purchase)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BuyRegex();

    [GeneratedRegex(@"^(?:sell)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SellRegex();

    [GeneratedRegex(@"^(?:equip|wear|wield)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex EquipRegex();

    [GeneratedRegex(@"^(?:unequip|remove|take\s+off)\s+(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UnequipRegex();

    [GeneratedRegex(@"^(?:long\s+rest|camp|sleep)$", RegexOptions.IgnoreCase)]
    private static partial Regex LongRestRegex();

    [GeneratedRegex(@"^(?:short\s+rest|breather|rest\s+briefly)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShortRestRegex();

    [GeneratedRegex(@"^rest$", RegexOptions.IgnoreCase)]
    private static partial Regex RestRegex();

    [GeneratedRegex(@"^(?:inventory|inv|i|bag|backpack)$", RegexOptions.IgnoreCase)]
    private static partial Regex InventoryRegex();

    [GeneratedRegex(@"^(?:stats|status|character|char|me|whoami|who\s+am\s+i)$", RegexOptions.IgnoreCase)]
    private static partial Regex StatsRegex();

    [GeneratedRegex(@"^(?:cast|channel|invoke|conjure)\s+(?<spell>.+?)(?:\s+(?:at|on|toward|against)\s+(?<target>.+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CastRegex();

    [GeneratedRegex(@"^(?:map|look\s+(?:at\s+)?map|show\s+map|view\s+map|world\s+map|check\s+map)$", RegexOptions.IgnoreCase)]
    private static partial Regex MapRegex();

    [GeneratedRegex(@"^(?:spellbook|spells|known\s+spells|my\s+spells)$", RegexOptions.IgnoreCase)]
    private static partial Regex SpellbookRegex();

    [GeneratedRegex(@"^(?:journal|quests|quest\s+log|my\s+quests|quest\s+journal|log)$", RegexOptions.IgnoreCase)]
    private static partial Regex JournalRegex();

    [GeneratedRegex(@"^(?:completed|done|finished|completed\s+quests)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompletedQuestsRegex();

    [GeneratedRegex(@"^(?:quest\s+info|check\s+quest|quest)\s+(?<quest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuestInfoRegex();

    [GeneratedRegex(@"^(?:accept\s+(?:quest\s+)?|take\s+quest\s+)(?<quest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptQuestRegex();

    [GeneratedRegex(@"^(?:turn\s+in\s+(?:quest\s+)?|complete\s+(?:quest\s+)?|finish\s+(?:quest\s+)?|hand\s+in\s+(?:quest\s+)?)(?<quest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TurnInQuestRegex();

    [GeneratedRegex(@"^(?:abandon\s+(?:quest\s+)?|drop\s+quest\s+|cancel\s+(?:quest\s+)?)(?<quest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AbandonQuestRegex();

    [GeneratedRegex(@"^(?:help|h|\?)$", RegexOptions.IgnoreCase)]
    private static partial Regex HelpRegex();

    [GeneratedRegex(@"^(?:lorebook|lore\s*book|knowledge|discoveries|discovered\s+lore)$", RegexOptions.IgnoreCase)]
    private static partial Regex LorebookRegex();

    [GeneratedRegex(@"^(?:lore|lore\s+(?:about|info|entry|on))\s+(?<topic>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LoreInfoRegex();

    [GeneratedRegex(@"^(?:narrator|narrators|voice|personality|voices)$", RegexOptions.IgnoreCase)]
    private static partial Regex NarratorListRegex();

    [GeneratedRegex(@"^(?:(?:set\s+)?narrator|(?:set\s+)?voice|(?:change\s+)?narrator)\s+(?<name>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SetNarratorRegex();
}
