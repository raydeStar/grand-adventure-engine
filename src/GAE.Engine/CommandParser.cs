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

        // Movement
        var moveMatch = MoveRegex().Match(input);
        if (moveMatch.Success)
        {
            action.Type = ActionType.Move;
            action.Direction = moveMatch.Groups["dir"].Value.ToLowerInvariant();
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

        // Attack
        var attackMatch = AttackRegex().Match(input);
        if (attackMatch.Success)
        {
            action.Type = ActionType.Attack;
            action.Target = attackMatch.Groups["target"].Value.Trim();
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

    [GeneratedRegex(@"^(?:go|move|walk|head|travel)\s+(?<dir>north|south|east|west|up|down|n|s|e|w|u|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex MoveRegex();

    [GeneratedRegex(@"^(?:look|l|examine|inspect|search)(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LookRegex();

    [GeneratedRegex(@"^(?:l|look|look\s+around|look\s+at\s+(?:the\s+)?room|look\s+here|look\s+surroundings|examine\s+(?:the\s+)?room|inspect\s+(?:the\s+)?room|search|search\s+(?:the\s+)?room)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoomLookAliasRegex();

    [GeneratedRegex(@"^(?:room|the\s+room|around|here|surroundings)$", RegexOptions.IgnoreCase)]
    private static partial Regex RoomTargetAliasRegex();

    [GeneratedRegex(@"(?:look|examine|inspect|search)\s+(?:(?:at|for)\s+)?(?<target>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LookTargetRegex();

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

    [GeneratedRegex(@"^(?:stats|status|character|char|me)$", RegexOptions.IgnoreCase)]
    private static partial Regex StatsRegex();

    [GeneratedRegex(@"^(?:help|h|\?)$", RegexOptions.IgnoreCase)]
    private static partial Regex HelpRegex();
}
