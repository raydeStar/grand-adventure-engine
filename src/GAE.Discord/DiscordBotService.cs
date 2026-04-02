using Discord;
using Discord.WebSocket;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GAE.Discord;

public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly IGameEngine _engine;
    private readonly IStateManager _stateManager;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly string _token;
    private readonly Dictionary<ulong, CharacterCreation.CharacterCreationSession> _creationSessions = [];

    public DiscordBotService(
        DiscordSocketClient client,
        IGameEngine engine,
        IStateManager stateManager,
        ILogger<DiscordBotService> logger,
        string token)
    {
        _client = client;
        _engine = engine;
        _stateManager = stateManager;
        _logger = logger;
        _token = token;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += msg =>
        {
            _logger.LogInformation("[Discord] {Message}", msg.Message);
            return Task.CompletedTask;
        };

        _client.MessageReceived += HandleMessageAsync;
        _client.Ready += () =>
        {
            _logger.LogInformation("Discord bot connected as {User}", _client.CurrentUser?.Username);
            return Task.CompletedTask;
        };

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
    }

    private async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage userMessage) return;

        var discordId = message.Author.Id.ToString();
        var content = message.Content.Trim();

        // Check for character creation commands
        if (content.StartsWith("!create", StringComparison.OrdinalIgnoreCase))
        {
            await HandleCreateCharacterAsync(userMessage, discordId);
            return;
        }

        // Check if in creation session
        if (_creationSessions.TryGetValue(message.Author.Id, out var session))
        {
            await HandleCreationSessionInputAsync(userMessage, session, discordId);
            return;
        }

        // Check for game commands (prefixed with !)
        if (!content.StartsWith('!')) return;
        var command = content[1..].Trim();

        // Ensure player exists
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await message.Channel.SendMessageAsync(
                "You don't have a character yet! Use `!create` to start character creation.");
            return;
        }

        // Parse and process the command
        var action = _engine.ParseCommand(player.Id, command);
        var result = await _engine.ProcessActionAsync(player.Id, action);

        // Format and send response
        var response = FormatResponse(result);
        await SendChunkedAsync(message.Channel, response);
    }

    private async Task HandleCreateCharacterAsync(SocketUserMessage message, string discordId)
    {
        var existing = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (existing is not null)
        {
            await message.Channel.SendMessageAsync($"You already have a character: **{existing.Name}** (Level {existing.Level} {existing.Race} {existing.Class})");
            return;
        }

        var session = new CharacterCreation.CharacterCreationSession(discordId);
        _creationSessions[message.Author.Id] = session;

        await message.Channel.SendMessageAsync("""
            **Character Creation**
            Welcome, adventurer! Let's create your character.

            **Step 1: What is your character's name?**
            """);
    }

    private async Task HandleCreationSessionInputAsync(SocketUserMessage message, CharacterCreation.CharacterCreationSession session, string discordId)
    {
        var input = message.Content.Trim();
        var response = session.ProcessInput(input);

        if (session.IsComplete)
        {
            _creationSessions.Remove(message.Author.Id);

            var concept = session.ToConcept();
            var player = await _engine.CreateCharacterFromConceptAsync(concept);

            await message.Channel.SendMessageAsync($"""
                **Character Created!**
                **{player.Name}** — Level {player.Level} {player.Race} {player.Class}
                HP: {player.Hp}/{player.MaxHp} | MP: {player.Mp}/{player.MaxMp} | Gold: {player.Gold}
                {player.FormatStatsCompact()}

                {player.Backstory}

                Type `!look` to see your surroundings!
                """);
        }
        else
        {
            await message.Channel.SendMessageAsync(response);
        }
    }

    private static string FormatResponse(ActionResult result)
    {
        var parts = new List<string>();

        if (!result.Success)
        {
            parts.Add($"❌ {result.MechanicalSummary}");
            return string.Join("\n", parts);
        }

        // Add dice roll details
        foreach (var roll in result.DiceRolls)
        {
            var rollStr = $"🎲 [{string.Join(", ", roll.IndividualRolls)}]";
            if (roll.Modifier != 0)
                rollStr += $" {(roll.Modifier > 0 ? "+" : "")}{roll.Modifier}";
            rollStr += $" = **{roll.Total}** ({roll.Purpose})";
            if (roll.IsCritical) rollStr += " 💥 **CRITICAL!**";
            if (roll.IsFumble) rollStr += " 💀 **FUMBLE!**";
            parts.Add(rollStr);
        }

        // Add narration or mechanical summary
        parts.Add(result.Narration ?? result.MechanicalSummary);

        // Add rewards
        if (result.GoldChange > 0)
            parts.Add($"💰 +{result.GoldChange} gold");
        if (result.XpGained > 0)
            parts.Add($"⭐ +{result.XpGained} XP");

        return string.Join("\n", parts);
    }

    private static async Task SendChunkedAsync(ISocketMessageChannel channel, string text)
    {
        const int maxLength = 2000;
        for (int i = 0; i < text.Length; i += maxLength)
        {
            var chunk = text.Substring(i, Math.Min(maxLength, text.Length - i));
            await channel.SendMessageAsync(chunk);
        }
    }
}
