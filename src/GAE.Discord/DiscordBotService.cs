using Discord;
using Discord.Net;
using Discord.WebSocket;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GAE.Discord;

/// <summary>
/// Discord bot service — manages player threads, embeds, slash commands,
/// AI character creation, conversation mode, admin commands, and narrator fallback.
/// </summary>
public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly IGameEngine _engine;
    private readonly IStateManager _stateManager;
    private readonly INarratorService _narrator;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly string _token;

    // Creation sessions keyed by Discord user ID
    private readonly Dictionary<ulong, AiCreationSession> _creationSessions = [];

    // Narrator health tracking
    private readonly List<DateTimeOffset> _narratorFailures = [];
    private bool _narratorWarningPosted;

    // Prevent concurrent narrator requests from flooding LM Studio
    private readonly SemaphoreSlim _narratorLock = new(1, 1);

    // Configuration — channel names
    private const string MainChannelName = "the-tavern";
    private const string AdminChannelName = "dm-room";

    public DiscordBotService(
        DiscordSocketClient client,
        IGameEngine engine,
        IStateManager stateManager,
        INarratorService narrator,
        ILogger<DiscordBotService> logger,
        string token)
    {
        _client = client;
        _engine = engine;
        _stateManager = stateManager;
        _narrator = narrator;
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
        _client.SlashCommandExecuted += HandleSlashCommandAsync;
        _client.Ready += OnReadyAsync;

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
    }

    // ==================== Ready / Slash Command Registration ====================

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Discord bot connected as {User}", _client.CurrentUser?.Username);

        try
        {
            var commands = new[]
            {
                new SlashCommandBuilder().WithName("create").WithDescription("Start character creation"),
                new SlashCommandBuilder().WithName("restart").WithDescription("Reset your character and start over"),
                new SlashCommandBuilder().WithName("stats").WithDescription("View your character sheet"),
                new SlashCommandBuilder().WithName("inventory").WithDescription("View inventory and equipment"),
                new SlashCommandBuilder().WithName("help").WithDescription("Show all commands"),
                new SlashCommandBuilder().WithName("map").WithDescription("Show discovered rooms as ASCII map"),
            };

            foreach (var cmd in commands)
                await _client.CreateGlobalApplicationCommandAsync(cmd.Build());

            _logger.LogInformation("Slash commands registered");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register slash commands");
        }
    }

    // ==================== Slash Command Handler ====================

    private async Task HandleSlashCommandAsync(SocketSlashCommand command)
    {
        var discordId = command.User.Id.ToString();
        try
        {
            switch (command.Data.Name)
            {
                case "create":
                    await command.DeferAsync(ephemeral: true);
                    await HandleCreateSlashAsync(command, discordId);
                    break;
                case "restart":
                    await command.DeferAsync(ephemeral: true);
                    await HandleRestartSlashAsync(command, discordId);
                    break;
                case "stats":
                    await command.DeferAsync(ephemeral: true);
                    await HandleStatsSlashAsync(command, discordId);
                    break;
                case "inventory":
                    await command.DeferAsync(ephemeral: true);
                    await HandleInventorySlashAsync(command, discordId);
                    break;
                case "help":
                    await command.DeferAsync(ephemeral: true);
                    await HandleHelpSlashAsync(command);
                    break;
                case "map":
                    await command.DeferAsync(ephemeral: true);
                    await HandleMapSlashAsync(command, discordId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slash command error: {Command}", command.Data.Name);
            try { await command.FollowupAsync("Something went wrong. Try again.", ephemeral: true); }
            catch { /* best effort */ }
        }
    }

    private async Task HandleCreateSlashAsync(SocketSlashCommand command, string discordId)
    {
        var existing = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (existing is not null)
        {
            var threadLink = existing.ThreadId.HasValue ? $"<#{existing.ThreadId}>" : "your adventure thread";
            await command.FollowupAsync($"You already have a character: **{existing.Name}**. Head to {threadLink} to play!", ephemeral: true);
            return;
        }

        var thread = await CreatePlayerThreadAsync(command.Channel, command.User);
        if (thread is null)
        {
            await command.FollowupAsync("Couldn't create your adventure thread. Make sure I have Thread permissions.", ephemeral: true);
            return;
        }

        await command.FollowupAsync($"Your adventure begins! Head to <#{thread.Id}>", ephemeral: true);
        await StartAiCharacterCreation(thread, command.User.Id, discordId);
    }

    private async Task HandleRestartSlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            // No character — check if they have a creation session they want to restart
            if (_creationSessions.ContainsKey(command.User.Id))
            {
                _creationSessions.Remove(command.User.Id);
                await command.FollowupAsync("Character creation reset. Use `/create` to start fresh.", ephemeral: true);
                return;
            }
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        await command.FollowupAsync("Are you sure? Type `yes` in your adventure thread to confirm. Your character will be wiped.", ephemeral: true);
        _creationSessions[command.User.Id] = new AiCreationSession(discordId) { AwaitingRestartConfirmation = true };
    }

    private async Task HandleStatsSlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        var embed = BuildCharacterEmbed(player);
        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private async Task HandleInventorySlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        var embed = BuildInventoryEmbed(player);
        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private static async Task HandleHelpSlashAsync(SocketSlashCommand command)
    {
        var embed = BuildHelpEmbed();
        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    private async Task HandleMapSlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        var map = await BuildAsciiMapAsync(player);
        await command.FollowupAsync($"```\n{map}\n```", ephemeral: true);
    }

    // ==================== Message Handler ====================

    private async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage userMessage) return;

        var discordId = message.Author.Id.ToString();
        var content = message.Content.Trim();
        if (string.IsNullOrEmpty(content)) return;

        try
        {
            // Admin commands in #dm-room
            if (message.Channel is SocketTextChannel textChannel &&
                textChannel.Name == AdminChannelName)
            {
                await HandleAdminCommandAsync(userMessage, discordId, content);
                return;
            }

            // Main channel — redirect to threads
            if (message.Channel is SocketTextChannel mainChannel &&
                mainChannel.Name == MainChannelName)
            {
                await HandleMainChannelMessageAsync(userMessage, discordId, content);
                return;
            }

            // Thread — game commands
            if (message.Channel is SocketThreadChannel thread)
            {
                await HandleThreadMessageAsync(userMessage, thread, discordId, content);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message from {User}: {Content}",
                message.Author.Username, content[..Math.Min(100, content.Length)]);
            try { await message.Channel.SendMessageAsync("Something went wrong. Try again."); }
            catch { /* best effort */ }
        }
    }

    private async Task HandleMainChannelMessageAsync(SocketUserMessage message, string discordId, string content)
    {
        // Only respond in the main channel if the bot is @mentioned
        var botUser = _client.CurrentUser;
        if (botUser is null) return;

        bool isMentioned = message.MentionedUsers.Any(u => u.Id == botUser.Id);
        if (!isMentioned) return;

        // Bot was @mentioned — respond helpfully with commands and status
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is not null)
        {
            var link = player.ThreadId.HasValue ? $"<#{player.ThreadId}>" : "your adventure thread";
            await message.Channel.SendMessageAsync(
                $"Hey {message.Author.Mention}! You're playing as **{player.Name}** ({player.Race} {player.Class}). " +
                $"Head to {link} to continue your adventure!\n\n" +
                "**Commands:**\n" +
                "> `/stats` — View your character sheet\n" +
                "> `/inventory` — Check your gear\n" +
                "> `/map` — See discovered rooms\n" +
                "> `/restart` — Start over with a new character");
        }
        else
        {
            await message.Channel.SendMessageAsync(
                $"Hey {message.Author.Mention}! Welcome to the Grand Adventure Engine! ⚔️\n\n" +
                "**Getting Started:**\n" +
                "> `/create` — Begin your adventure! I'll help you create a character.\n\n" +
                "**Other Commands:**\n" +
                "> `/stats` — View your character sheet\n" +
                "> `/inventory` — Check your gear\n" +
                "> `/map` — See discovered rooms\n" +
                "> `/help` — Full command list");
        }
    }

    private async Task HandleThreadMessageAsync(SocketUserMessage message, SocketThreadChannel thread, string discordId, string content)
    {
        // Unarchive thread if needed
        if (thread.IsArchived)
        {
            try { await thread.ModifyAsync(props => props.Archived = false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to unarchive thread {ThreadId}", thread.Id); }
        }

        // Only the thread owner (or someone mid-creation in this thread) can issue commands.
        // Everyone else can read but not play.
        var threadOwner = await _stateManager.GetAllPlayersAsync();
        var ownerPlayer = threadOwner.FirstOrDefault(p => p.ThreadId == thread.Id);
        bool isCreating = _creationSessions.TryGetValue(message.Author.Id, out var pendingSession);

        if (ownerPlayer is not null && ownerPlayer.DiscordId != discordId)
        {
            // Not the owner — silently ignore (they can spectate but not play)
            return;
        }
        if (!isCreating && ownerPlayer is null)
        {
            // No owner yet and not in creation — ignore
            return;
        }

        // Quick restart keywords — works anytime in thread
        var contentLower = content.ToLowerInvariant();
        if (contentLower is "restart" or "start over" or "!restart" or "!start over")
        {
            var existingPlayer = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
            if (existingPlayer is not null || _creationSessions.ContainsKey(message.Author.Id))
            {
                // If still in creation, skip confirmation — just wipe and restart
                if (_creationSessions.ContainsKey(message.Author.Id) && existingPlayer is null)
                {
                    _creationSessions.Remove(message.Author.Id);
                    await thread.SendMessageAsync("🗑️ Character creation reset. Let's start fresh.\n");
                    await StartAiCharacterCreation(thread, message.Author.Id, discordId);
                    return;
                }
                // Existing character — confirm then wipe
                await HandleRestartConfirmedAsync(message, thread, discordId);
                return;
            }
            await message.Channel.SendMessageAsync("You don't have a character yet! Use `/create` to begin.");
            return;
        }

        // Check for active creation session
        if (_creationSessions.TryGetValue(message.Author.Id, out var session))
        {
            // Restart confirmation
            if (session.AwaitingRestartConfirmation)
            {
                if (content.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleRestartConfirmedAsync(message, thread, discordId);
                }
                else
                {
                    _creationSessions.Remove(message.Author.Id);
                    await message.Channel.SendMessageAsync("Restart cancelled.");
                }
                return;
            }

            // AI character creation flow
            await HandleAiCreationInputAsync(message, thread, session, discordId);
            return;
        }

        // Ensure player exists
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await message.Channel.SendMessageAsync("You don't have a character yet! Use `/create` in the main channel.");
            return;
        }

        // Update thread ID if needed
        if (player.ThreadId != thread.Id)
        {
            player.ThreadId = thread.Id;
            await _stateManager.SavePlayerAsync(player);
        }

        // Update activity timestamp
        player.LastActiveAt = DateTimeOffset.UtcNow;

        // No prefix required — thread is gated to owner only, so everything is a game command.
        // Strip leading ! if present (for habit/compatibility) but don't require it.
        var command = content.StartsWith('!') ? content[1..].Trim() : content;

        if (string.IsNullOrEmpty(command)) return;

        // Show typing indicator while processing
        await thread.TriggerTypingAsync();

        // Parse and process
        var action = _engine.ParseCommand(player.Id, command);
        var result = await ProcessWithNarratorFallbackAsync(player, action, thread);

        // Refresh player state so status bar reflects changes from this action
        player = await _stateManager.GetPlayerByDiscordIdAsync(discordId) ?? player;

        // Format and send response
        await SendGameResponseAsync(thread, player, action, result);
    }

    // ==================== Thread Management ====================

    private async Task<SocketThreadChannel?> CreatePlayerThreadAsync(ISocketMessageChannel channel, SocketUser user)
    {
        if (channel is not SocketTextChannel textChannel) return null;

        try
        {
            var threadName = $"\u2694\uFE0F {user.Username}'s Adventure";

            // Public threads need a starter message in the parent channel so they show up in the sidebar
            var starterMessage = await textChannel.SendMessageAsync(
                $"⚔️ **{user.Username}** has begun a new adventure! Click the thread below to spectate. " +
                $"(Right-click the thread → **Follow** to get notified of updates)");

            var thread = await textChannel.CreateThreadAsync(
                threadName,
                ThreadType.PublicThread,
                autoArchiveDuration: ThreadArchiveDuration.OneDay,
                message: starterMessage);

            _logger.LogInformation("Created thread {ThreadId} for user {User}", thread.Id, user.Username);
            return thread;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create thread for {User}", user.Username);
            return null;
        }
    }

    // ==================== AI Character Creation ====================

    private async Task StartAiCharacterCreation(SocketThreadChannel thread, ulong userId, string discordId)
    {
        var session = new AiCreationSession(discordId);
        _creationSessions[userId] = session;

        await thread.SendMessageAsync("""
            Welcome, traveler. I am Sir Thaddeus, keeper of fates.
            Tell me about yourself. Who are you? What are you?
            Speak freely — I'll shape your destiny from your words.

            *(Describe yourself however you like: "I'm a sneaky halfling who picks pockets" or "I'm a massive orc who solves problems with fists" — or just tell me your name and I'll ask questions.)*
            """);
    }

    private async Task HandleAiCreationInputAsync(SocketUserMessage message, SocketThreadChannel thread,
        AiCreationSession session, string discordId)
    {
        var input = message.Content.Trim();

        // Check for finalization phrases
        var lower = input.ToLowerInvariant();
        if (session.HasSheet && IsFinalizationPhrase(lower))
        {
            await FinalizeCharacterCreationAsync(message, thread, session, discordId);
            return;
        }

        // Send to AI narrator for character concept — single attempt, serialized to avoid flooding LM Studio
        await thread.TriggerTypingAsync();
        CharacterCreationAiResponse? aiResponse = null;
        await _narratorLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            aiResponse = await _narrator.CreateCharacterFromDescriptionAsync(
                input, session.LastSheetJson, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI character creation failed: {Message}", ex.Message);
        }
        finally
        {
            _narratorLock.Release();
        }

        if (aiResponse is null)
        {
            _logger.LogWarning("AI character creation returned null — LM Studio may not be running at {Endpoint}",
                "http://host.docker.internal:1234");

            // Fallback to rigid wizard
            if (session.FallbackSession is null)
            {
                session.FallbackSession = new CharacterCreation.CharacterCreationSession(discordId);
                await message.Channel.SendMessageAsync(
                    "⚠️ The storyteller is resting (AI narrator unreachable). Let's do this the old-fashioned way.\n" +
                    "*(Make sure LM Studio is running on your machine if you want AI-driven creation. Type `restart` to try again.)*\n\n" +
                    "**Describe your character in one message** — name, race, class, backstory — anything goes!\n" +
                    "Example: *\"My name is Yuric. I am a Duck Hitman who likes cats and running from oncoming cars.\"*");
                return;
            }
            var response = session.FallbackSession.ProcessInput(input);
            if (session.FallbackSession.IsComplete)
            {
                var concept = session.FallbackSession.ToConcept();
                var player = await _engine.CreateCharacterFromConceptAsync(concept);
                player.DiscordId = discordId;
                player.ThreadId = thread.Id;
                await _stateManager.SavePlayerAsync(player);
                _creationSessions.Remove(message.Author.Id);

                await thread.ModifyAsync(props => props.Name = $"\u2694\uFE0F {player.Name}'s Adventure");
                await SendCharacterCreatedEmbed(thread, player);
                await SendRoomEntryAsync(thread, player);
            }
            else
            {
                await message.Channel.SendMessageAsync(response);
            }
            return;
        }

        // If the player explicitly asked to change a field, force it even if the AI didn't comply
        if (session.HasSheet && session.LastAiResponse is not null)
        {
            CharacterCreation.SheetOverrides.Apply(input, aiResponse, session.LastAiResponse);
        }

        // Store the AI response
        session.LastAiResponse = aiResponse;
        session.HasSheet = true;

        var standardArray = new[] { 15, 14, 13, 12, 10, 8 };
        var stats = AssignStatsFromOrder(aiResponse.StatOrder, standardArray);

        session.LastSheetJson = System.Text.Json.JsonSerializer.Serialize(aiResponse);

        var embed = new EmbedBuilder()
            .WithTitle("\u2694\uFE0F CHARACTER SHEET")
            .WithColor(Color.Gold)
            .AddField("Name", aiResponse.Name ?? "???", inline: true)
            .AddField("Race", aiResponse.Race, inline: true)
            .AddField("Class", aiResponse.Class, inline: true)
            .AddField("Stats",
                $"STR: {stats["str"]} ({FormatMod(stats["str"])})  DEX: {stats["dex"]} ({FormatMod(stats["dex"])})\n" +
                $"CON: {stats["con"]} ({FormatMod(stats["con"])})  INT: {stats["int"]} ({FormatMod(stats["int"])})\n" +
                $"WIS: {stats["wis"]} ({FormatMod(stats["wis"])})  CHA: {stats["cha"]} ({FormatMod(stats["cha"])})")
            .AddField("Backstory", aiResponse.Backstory)
            .WithFooter("Say \"looks good\" to start, or describe changes you want.");

        await thread.SendMessageAsync(embed: embed.Build());

        if (aiResponse.FollowUpQuestion is not null)
            await thread.SendMessageAsync(aiResponse.FollowUpQuestion);
    }

    private async Task FinalizeCharacterCreationAsync(SocketUserMessage message, SocketThreadChannel thread,
        AiCreationSession session, string discordId)
    {
        var ai = session.LastAiResponse!;
        var standardArray = new[] { 15, 14, 13, 12, 10, 8 };
        var stats = AssignStatsFromOrder(ai.StatOrder, standardArray);

        var concept = new CharacterConcept
        {
            PlayerDiscordId = discordId,
            Name = ai.Name ?? message.Author.Username,
            Race = ai.Race,
            Class = ai.Class,
            Backstory = ai.Backstory,
            StatMethod = StatAllocationMethod.Manual,
            ManualStats = stats
        };

        var player = await _engine.CreateCharacterFromConceptAsync(concept);
        player.DiscordId = discordId;
        player.ThreadId = thread.Id;
        await _stateManager.SavePlayerAsync(player);
        _creationSessions.Remove(message.Author.Id);

        await thread.ModifyAsync(props => props.Name = $"\u2694\uFE0F {player.Name}'s Adventure");
        await SendCharacterCreatedEmbed(thread, player);
        await SendRoomEntryAsync(thread, player);

        // Notify admin channel
        await PostToAdminChannelAsync($"\U0001F4E5 **{player.Name}** ({message.Author.Username}) created a character — {player.Race} {player.Class}");
    }

    // ==================== Restart ====================

    private async Task HandleRestartConfirmedAsync(SocketUserMessage message, SocketThreadChannel thread, string discordId)
    {
        _creationSessions.Remove(message.Author.Id);

        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is not null)
        {
            await _stateManager.RemovePlayerRoomsAsync(player.Id);
            await _stateManager.RemovePlayerAsync(player.Id);
            await PostToAdminChannelAsync($"\U0001F504 **{player.Name}** ({message.Author.Username}) restarted their character.");
        }

        await thread.SendMessageAsync("\U0001F5D1\uFE0F Character wiped. Let's start fresh.\n");
        await StartAiCharacterCreation(thread, message.Author.Id, discordId);
    }

    // ==================== Game Response Formatting ====================

    private async Task SendGameResponseAsync(SocketThreadChannel thread, PlayerCharacter player, GameAction action, ActionResult result)
    {
        // Room entry — embed + narration (movement or plain "look" at room)
        if (result.NewRoom is not null)
        {
            await SendRoomEntryAsync(thread, player, result);
            return;
        }

        // "look" with no target shows the room card
        if (action.Type == Core.Models.ActionType.Look && string.IsNullOrWhiteSpace(action.Target) && result.Success)
        {
            await SendRoomEntryAsync(thread, player, result);
            return;
        }

        // Inventory — show as proper embed
        if (action.Type == Core.Models.ActionType.Inventory)
        {
            await thread.SendMessageAsync(embed: BuildInventoryEmbed(player).Build());
            return;
        }

        // Stats — show as proper embed
        if (action.Type == Core.Models.ActionType.Stats)
        {
            await thread.SendMessageAsync(embed: BuildCharacterEmbed(player).Build());
            return;
        }

        // Help — show as proper embed
        if (action.Type == Core.Models.ActionType.Help)
        {
            await thread.SendMessageAsync(embed: BuildHelpEmbed().Build());
            return;
        }

        // Combat result
        if (result.InteractionUpdate?.CombatStatus is not null)
        {
            await SendCombatResultAsync(thread, player, result);
            return;
        }

        // Victory announcement
        if (result.IsVictory)
        {
            await SendVictoryAnnouncementAsync(thread, player);
        }

        // Build an embed for consistent styling
        var embed = new EmbedBuilder();

        // Determine color based on result
        if (!result.Success)
            embed.WithColor(Color.Red);
        else if (result.GoldChange != 0 || result.ItemsGained.Count > 0 || result.ItemsLost.Count > 0)
            embed.WithColor(Color.Green);
        else
            embed.WithColor(new Color(0x2f, 0x31, 0x36)); // Dark grey — blends with Discord

        // Narration or mechanical summary as description
        var narration = result.Narration ?? result.MechanicalSummary;
        if (!result.Success)
            narration = $"❌ {narration}";
        embed.WithDescription(narration);

        // Dice rolls as a field
        if (result.DiceRolls.Count > 0)
        {
            var rollLines = result.DiceRolls.Select(roll =>
            {
                var rollStr = $"🎲 [{string.Join(", ", roll.IndividualRolls)}]";
                if (roll.Modifier != 0)
                    rollStr += $" {(roll.Modifier > 0 ? "+" : "")}{roll.Modifier}";
                rollStr += $" = **{roll.Total}** ({roll.Purpose})";
                if (roll.IsCritical) rollStr += " 💥 **CRITICAL!**";
                if (roll.IsFumble) rollStr += " 💀 **FUMBLE!**";
                return rollStr;
            });
            embed.AddField("Rolls", string.Join("\n", rollLines));
        }

        // Rewards & changes inline
        var changes = new List<string>();
        if (result.GoldChange > 0) changes.Add($"💰 +{result.GoldChange} gold");
        if (result.GoldChange < 0) changes.Add($"💰 {result.GoldChange} gold");
        if (result.XpGained > 0) changes.Add($"⭐ +{result.XpGained} XP");
        foreach (var item in result.ItemsGained)
            changes.Add($"📥 +{item.Name}{(item.Quantity > 1 ? $" (x{item.Quantity})" : "")}");
        foreach (var item in result.ItemsLost)
            changes.Add($"📤 -{item.Name}{(item.Quantity > 1 ? $" (x{item.Quantity})" : "")}");

        if (changes.Count > 0)
            embed.AddField("Changes", string.Join("\n", changes), inline: true);

        // Status bar footer
        embed.WithFooter(FormatStatusBar(player));

        await thread.SendMessageAsync(embed: embed.Build());
    }

    private async Task SendRoomEntryAsync(SocketThreadChannel thread, PlayerCharacter player, ActionResult? result = null)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId);
        if (room is null) return;

        var embed = new EmbedBuilder()
            .WithTitle($"📍 {room.Name}")
            .WithDescription(room.Description)
            .WithColor(Color.Orange);

        // NPCs
        if (room.Npcs.Count > 0)
        {
            var npcLines = room.Npcs.Select(n =>
            {
                var prefix = n.IsHostile ? "⚠️" : "🧑";
                return $"{prefix} {n.Name}";
            });
            embed.AddField("NPCs", string.Join("\n", npcLines), inline: true);
        }

        // Items
        if (room.Items.Count > 0)
        {
            var itemLines = room.Items.Select(i =>
                i.Quantity > 1 ? $"📦 {i.Name} (x{i.Quantity})" : $"📦 {i.Name}");
            embed.AddField("Items", string.Join("\n", itemLines), inline: true);
        }

        // Exits — resolve target room names where possible
        var exitLines = new List<string>();
        foreach (var e in room.Exits)
        {
            var dirEmoji = e.Key.ToLowerInvariant() switch
            {
                "north" => "⬆️",
                "south" => "⬇️",
                "east" => "➡️",
                "west" => "⬅️",
                "up" => "🔼",
                "down" => "🔽",
                _ => "↪️"
            };
            // Try to resolve the target room name
            var targetRoom = await _stateManager.GetPlayerRoomAsync(player.Id, e.Value);
            var targetName = targetRoom?.Name ?? e.Value.Replace("_", " ");
            exitLines.Add($"{dirEmoji} **{e.Key}** → {targetName}");
        }
        if (exitLines.Count > 0)
            embed.AddField("Exits", string.Join("\n", exitLines));

        embed.WithFooter(FormatStatusBar(player));

        // Send narration text above the embed (if there is any)
        var narration = result?.Narration ?? result?.MechanicalSummary ?? "";
        if (!string.IsNullOrWhiteSpace(narration))
            await thread.SendMessageAsync(text: narration, embed: embed.Build());
        else
            await thread.SendMessageAsync(embed: embed.Build());
    }

    private async Task SendCombatResultAsync(SocketThreadChannel thread, PlayerCharacter player, ActionResult result)
    {
        var combatStatus = result.InteractionUpdate?.CombatStatus ?? "ongoing";
        var embedColor = combatStatus switch
        {
            "victory" => Color.Gold,
            "defeat" => Color.DarkRed,
            "fled" => Color.LightGrey,
            _ => new Color(0xcc, 0x33, 0x33) // Combat red
        };

        var embed = new EmbedBuilder()
            .WithTitle(combatStatus switch
            {
                "victory" => "⚔️ Victory!",
                "defeat" => "💀 Defeated...",
                "fled" => "🏃 Fled!",
                _ => "⚔️ Combat"
            })
            .WithColor(embedColor);

        // Narration as description
        embed.WithDescription(result.Narration ?? result.MechanicalSummary);

        // Dice rolls
        if (result.DiceRolls.Count > 0)
        {
            var rollLines = result.DiceRolls.Select(roll =>
            {
                var rollStr = $"🎲 [{string.Join(", ", roll.IndividualRolls)}]";
                if (roll.Modifier != 0)
                    rollStr += $" {(roll.Modifier > 0 ? "+" : "")}{roll.Modifier}";
                rollStr += $" = **{roll.Total}** ({roll.Purpose})";
                if (roll.IsCritical) rollStr += " 💥 **CRITICAL!**";
                if (roll.IsFumble) rollStr += " 💀 **FUMBLE!**";
                return rollStr;
            });
            embed.AddField("Rolls", string.Join("\n", rollLines));
        }

        // Rewards
        var rewards = new List<string>();
        if (result.XpGained > 0) rewards.Add($"⭐ +{result.XpGained} XP");
        if (result.GoldChange > 0) rewards.Add($"💰 +{result.GoldChange} gold");
        foreach (var item in result.ItemsGained)
            rewards.Add($"🎁 {item.Name}");
        if (rewards.Count > 0)
            embed.AddField("Rewards", string.Join("\n", rewards), inline: true);

        embed.WithFooter(FormatStatusBar(player));

        await thread.SendMessageAsync(embed: embed.Build());

        // Victory check
        if (result.IsVictory)
            await SendVictoryAnnouncementAsync(thread, player);
    }

    private async Task SendVictoryAnnouncementAsync(SocketThreadChannel thread, PlayerCharacter player)
    {
        // Post in player thread
        var victoryEmbed = new EmbedBuilder()
            .WithTitle("\U0001F3C6 VICTORY!")
            .WithDescription($"**{player.Name}** has defeated Goretusk the Undying!\nThe demon-boar of the Dread Hollow has been slain.\nThornwall is safe... for now.")
            .WithColor(Color.Gold);
        await thread.SendMessageAsync(embed: victoryEmbed.Build());

        // Post in main channel
        if (thread.ParentChannel is SocketTextChannel mainChannel)
        {
            var guild = mainChannel.Guild;
            var gaeChannel = guild.TextChannels.FirstOrDefault(c => c.Name == MainChannelName) ?? mainChannel;
            await gaeChannel.SendMessageAsync(
                $"\U0001F3C6 **{player.Name} has defeated Goretusk the Undying!**\nThe demon-boar of the Dread Hollow has been slain. Thornwall is safe... for now.");
        }

        // Notify admin
        await PostToAdminChannelAsync($"\U0001F3C6 **{player.Name}** defeated Goretusk!");
    }

    // ==================== Narrator Fallback ====================

    private async Task<ActionResult> ProcessWithNarratorFallbackAsync(
        PlayerCharacter player, GameAction action, SocketThreadChannel thread)
    {
        try
        {
            await _narratorLock.WaitAsync();
            ActionResult result;
            try
            {
                // Movement may generate a new room + narrate (2 narrator calls), so allow more time
                var timeout = action.Type == Core.Models.ActionType.Move
                    ? TimeSpan.FromSeconds(90)
                    : TimeSpan.FromSeconds(45);
                using var cts = new CancellationTokenSource(timeout);
                result = await _engine.ProcessActionAsync(player.Id, action, cts.Token);
            }
            finally
            {
                _narratorLock.Release();
            }

            // Narrator recovered — clear warnings
            if (_narratorWarningPosted && _narratorFailures.Count > 0)
            {
                _narratorFailures.Clear();
                _narratorWarningPosted = false;
                await thread.SendMessageAsync("\u2728 The storyteller has returned.");
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return await HandleNarratorFailureAsync(player, action, thread, "Narrator timeout");
        }
        catch (HttpRequestException ex)
        {
            return await HandleNarratorFailureAsync(player, action, thread, ex.Message);
        }
    }

    private async Task<ActionResult> HandleNarratorFailureAsync(
        PlayerCharacter player, GameAction action, SocketThreadChannel thread, string reason)
    {
        _logger.LogWarning("Narrator failure: {Reason}", reason);
        _narratorFailures.Add(DateTimeOffset.UtcNow);

        // Clean old failures (5 minute window)
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        _narratorFailures.RemoveAll(f => f < cutoff);

        // Post warning if 3+ failures in 5 minutes
        if (_narratorFailures.Count >= 3 && !_narratorWarningPosted)
        {
            _narratorWarningPosted = true;
            await thread.SendMessageAsync("\u26A0\uFE0F The storyteller is resting. Commands still work, but descriptions will be brief until it recovers.");
            await PostToAdminChannelAsync($"\u26A0\uFE0F Narrator down: {reason}");
        }

        // Return mechanical-only result
        try
        {
            var result = await _engine.ProcessActionAsync(player.Id, action);
            result.Narration = null; // Strip any narration that may have partially generated
            return result;
        }
        catch
        {
            return new ActionResult
            {
                ActionId = action.Id,
                Success = false,
                MechanicalSummary = "The narrator is unavailable. Action noted but not narrated."
            };
        }
    }

    // ==================== Admin Commands ====================

    private async Task HandleAdminCommandAsync(SocketUserMessage message, string discordId, string content)
    {
        if (!content.StartsWith("!dm ", StringComparison.OrdinalIgnoreCase)) return;
        var cmd = content[4..].Trim();

        // !dm status
        if (cmd.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var players = await _stateManager.GetAllPlayersAsync();
            var sb = new StringBuilder("**Player Status:**\n");
            foreach (var p in players)
            {
                var threadStatus = p.ThreadId.HasValue ? $"<#{p.ThreadId}>" : "no thread";
                sb.AppendLine($"- **{p.Name}** ({p.Race} {p.Class}) — {p.CurrentRoomId} — HP: {p.Hp}/{p.MaxHp} — {threadStatus}");
            }
            await message.Channel.SendMessageAsync(sb.ToString());
            return;
        }

        // !dm heal @player
        if (cmd.StartsWith("heal ", StringComparison.OrdinalIgnoreCase))
        {
            var targetId = ExtractMentionId(cmd[5..].Trim());
            if (targetId is null) { await message.Channel.SendMessageAsync("Usage: `!dm heal @player`"); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }
            player.Hp = player.MaxHp;
            player.Mp = player.MaxMp;
            await _stateManager.SavePlayerAsync(player);
            await message.Channel.SendMessageAsync($"\u2764\uFE0F {player.Name} healed to full.");
            return;
        }

        // !dm teleport @player room_id
        if (cmd.StartsWith("teleport ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cmd[9..].Trim().Split(' ', 2);
            if (parts.Length < 2) { await message.Channel.SendMessageAsync("Usage: `!dm teleport @player room_id`"); return; }
            var targetId = ExtractMentionId(parts[0]);
            if (targetId is null) { await message.Channel.SendMessageAsync("Invalid player mention."); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }
            player.CurrentRoomId = parts[1].Trim();
            await _stateManager.SavePlayerAsync(player);
            await message.Channel.SendMessageAsync($"\U0001F3C3 {player.Name} teleported to {parts[1]}.");
            return;
        }

        // !dm grant @player item_name
        if (cmd.StartsWith("grant ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cmd[6..].Trim().Split(' ', 2);
            if (parts.Length < 2) { await message.Channel.SendMessageAsync("Usage: `!dm grant @player item_name`"); return; }
            var targetId = ExtractMentionId(parts[0]);
            if (targetId is null) { await message.Channel.SendMessageAsync("Invalid player mention."); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }
            player.Inventory.Add(new InventoryItem { Name = parts[1].Trim(), Type = ItemType.Misc, Quantity = 1 });
            await _stateManager.SavePlayerAsync(player);
            await message.Channel.SendMessageAsync($"\U0001F381 Granted {parts[1]} to {player.Name}.");
            return;
        }

        // !dm say room_id "message"
        if (cmd.StartsWith("say ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = cmd[4..].Trim();
            var spaceIdx = rest.IndexOf(' ');
            if (spaceIdx < 0) { await message.Channel.SendMessageAsync("Usage: `!dm say room_id \"message\"`"); return; }
            var roomId = rest[..spaceIdx];
            var dmMessage = rest[(spaceIdx + 1)..].Trim().Trim('"');

            var players = await _stateManager.GetAllPlayersAsync();
            foreach (var p in players.Where(p => p.CurrentRoomId == roomId && p.ThreadId.HasValue))
            {
                var guild = (message.Channel as SocketGuildChannel)?.Guild;
                if (guild is null) continue;
                var playerThread = guild.GetChannel(p.ThreadId!.Value) as SocketThreadChannel;
                if (playerThread is not null)
                    await playerThread.SendMessageAsync($"\U0001F4DC *{dmMessage}*");
            }
            await message.Channel.SendMessageAsync($"DM message sent to players in {roomId}.");
            return;
        }

        // !dm kill npc_id player_mention
        if (cmd.StartsWith("kill ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cmd[5..].Trim().Split(' ', 2);
            if (parts.Length < 2) { await message.Channel.SendMessageAsync("Usage: `!dm kill npc_id @player`"); return; }
            var targetId = ExtractMentionId(parts[1]);
            if (targetId is null) { await message.Channel.SendMessageAsync("Invalid player mention."); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }
            var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId);
            if (room is null) { await message.Channel.SendMessageAsync("Room not found."); return; }
            var npc = room.Npcs.FirstOrDefault(n => n.Id == parts[0] || n.Name.Contains(parts[0], StringComparison.OrdinalIgnoreCase));
            if (npc is null) { await message.Channel.SendMessageAsync($"NPC '{parts[0]}' not found in {room.Name}."); return; }
            room.Npcs.Remove(npc);
            await _stateManager.SaveRoomAsync(room);
            await message.Channel.SendMessageAsync($"\U0001F480 {npc.Name} removed from {room.Name} for {player.Name}.");
            return;
        }

        // !dm spawn npc_name room_id player_mention
        if (cmd.StartsWith("spawn ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = cmd[6..].Trim().Split(' ', 3);
            if (parts.Length < 3) { await message.Channel.SendMessageAsync("Usage: `!dm spawn npc_name room_id @player`"); return; }
            var targetId = ExtractMentionId(parts[2]);
            if (targetId is null) { await message.Channel.SendMessageAsync("Invalid player mention."); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }
            var room = await _stateManager.GetPlayerRoomAsync(player.Id, parts[1]);
            if (room is null) { await message.Channel.SendMessageAsync("Room not found."); return; }
            room.Npcs.Add(new Npc { Id = Guid.NewGuid().ToString(), Name = parts[0] });
            await _stateManager.SaveRoomAsync(room);
            await message.Channel.SendMessageAsync($"\u2728 Spawned {parts[0]} in {room.Name} for {player.Name}.");
            return;
        }

        // !dm reset-world
        if (cmd.Equals("reset-world", StringComparison.OrdinalIgnoreCase))
        {
            await message.Channel.SendMessageAsync("\u26A0\uFE0F This will delete ALL player data and reseed the world. Type `!dm confirm-reset` to confirm.");
            return;
        }
        if (cmd.Equals("confirm-reset", StringComparison.OrdinalIgnoreCase))
        {
            await _stateManager.RemoveAllRoomsAsync();
            await _stateManager.RemoveAllCombatStatesAsync();
            await _stateManager.ClearStoryAsync();
            var allPlayers = await _stateManager.GetAllPlayersAsync();
            foreach (var p in allPlayers)
                await _stateManager.RemovePlayerAsync(p.Id);
            await message.Channel.SendMessageAsync("\U0001F4A5 World reset complete. All data cleared.");
            return;
        }

        // !dm announce "message"
        if (cmd.StartsWith("announce ", StringComparison.OrdinalIgnoreCase))
        {
            var announcement = cmd[9..].Trim().Trim('"');
            var guild = (message.Channel as SocketGuildChannel)?.Guild;
            var mainChannel = guild?.TextChannels.FirstOrDefault(c => c.Name == MainChannelName);
            if (mainChannel is not null)
                await mainChannel.SendMessageAsync(announcement);
            else
                await message.Channel.SendMessageAsync("Could not find the main channel.");
            return;
        }

        await message.Channel.SendMessageAsync("Unknown DM command. Available: `status`, `heal`, `teleport`, `grant`, `say`, `kill`, `spawn`, `reset-world`, `announce`");
    }

    // ==================== Map ====================

    private async Task<string> BuildAsciiMapAsync(PlayerCharacter player)
    {
        // Collect all rooms the player has visited
        var allTemplateRooms = await _stateManager.GetAllRoomsAsync();
        var visitedRooms = new Dictionary<string, Room>();
        var connectedUnvisited = new HashSet<string>();

        foreach (var template in allTemplateRooms)
        {
            var playerRoom = await _stateManager.GetPlayerRoomAsync(player.Id, template.Id);
            if (playerRoom is not null && playerRoom.IsDiscovered)
            {
                visitedRooms[template.Id] = playerRoom;
                foreach (var (_, targetId) in playerRoom.Exits)
                {
                    if (!visitedRooms.ContainsKey(targetId))
                        connectedUnvisited.Add(targetId);
                }
            }
        }

        // Remove from unvisited if actually visited
        foreach (var id in visitedRooms.Keys)
            connectedUnvisited.Remove(id);

        // Build simple text map
        var sb = new StringBuilder();
        sb.AppendLine("\U0001F4CD YOUR MAP (discovered rooms)\n");

        foreach (var (id, room) in visitedRooms.OrderBy(r => r.Key))
        {
            var marker = id == player.CurrentRoomId ? "\u2B50" : "  ";
            var exits = string.Join(", ", room.Exits.Select(e =>
            {
                var targetName = visitedRooms.TryGetValue(e.Value, out var t)
                    ? t.Name
                    : (connectedUnvisited.Contains(e.Value) ? "???" : e.Value);
                return $"{e.Key} \u2192 {targetName}";
            }));
            sb.AppendLine($"{marker} [{room.Name}]");
            sb.AppendLine($"   Exits: {exits}");
        }

        if (connectedUnvisited.Count > 0)
            sb.AppendLine($"\n??? = {connectedUnvisited.Count} unexplored area(s)");

        sb.AppendLine("\n\u2B50 = You are here");
        return sb.ToString();
    }

    // ==================== Embed Builders ====================

    private static EmbedBuilder BuildCharacterEmbed(PlayerCharacter player)
    {
        return new EmbedBuilder()
            .WithTitle($"\u2694\uFE0F {player.Name}")
            .WithColor(Color.Blue)
            .AddField("Race", player.Race, inline: true)
            .AddField("Class", player.Class, inline: true)
            .AddField("Level", player.Level.ToString(), inline: true)
            .AddField("Stats", player.FormatStatsDetailed("\n"))
            .AddField("Resources",
                $"\u2764\uFE0F HP: {player.Hp}/{player.MaxHp}\n" +
                $"\u2728 MP: {player.Mp}/{player.MaxMp}\n" +
                $"\U0001F4B0 Gold: {player.Gold}\n" +
                $"\u2B50 XP: {player.Xp}")
            .WithFooter(player.Backstory.Length > 200 ? player.Backstory[..200] + "..." : player.Backstory);
    }

    private static EmbedBuilder BuildInventoryEmbed(PlayerCharacter player)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"\U0001F392 {player.Name}'s Inventory")
            .WithColor(Color.Green);

        // Equipment
        var eqParts = new List<string>();
        if (player.Equipment.Weapon is not null) eqParts.Add($"\u2694\uFE0F Weapon: {player.Equipment.Weapon.Name}");
        if (player.Equipment.Armor is not null) eqParts.Add($"\U0001F6E1\uFE0F Armor: {player.Equipment.Armor.Name}");
        if (player.Equipment.Shield is not null) eqParts.Add($"\U0001F6E1\uFE0F Shield: {player.Equipment.Shield.Name}");
        if (player.Equipment.Helmet is not null) eqParts.Add($"\u26D1\uFE0F Helmet: {player.Equipment.Helmet.Name}");
        embed.AddField("Equipment", eqParts.Count > 0 ? string.Join("\n", eqParts) : "Nothing equipped");

        // Inventory
        if (player.Inventory.Count > 0)
        {
            var items = player.Inventory.Select(i => i.Quantity > 1 ? $"{i.Name} (x{i.Quantity})" : i.Name);
            embed.AddField("Backpack", string.Join("\n", items));
        }
        else
        {
            embed.AddField("Backpack", "Empty");
        }

        embed.WithFooter($"\U0001F4B0 {player.Gold}g");
        return embed;
    }

    private static EmbedBuilder BuildHelpEmbed()
    {
        return new EmbedBuilder()
            .WithTitle("Grand Adventure Engine — Commands")
            .WithColor(Color.Teal)
            .AddField("Movement", "`go <direction>` / `north` `south` `east` `west`")
            .AddField("Observation", "`look` / `look at <target>`")
            .AddField("Combat", "`attack <target>`")
            .AddField("Social", "`talk to <target>`")
            .AddField("Items", "`take <item>` / `drop <item>` / `use <item>` / `equip <item>` / `unequip <item>`")
            .AddField("Commerce", "`buy <item>` / `sell <item>`")
            .AddField("Rest", "`rest` / `short rest` / `long rest`")
            .AddField("Info", "`stats` / `inv` / `map` / `help`")
            .AddField("Free-form", "*Just type naturally! \"I search the bookshelf\" or \"I flex at the bartender\"*")
            .AddField("Other", "`restart` — reset character")
            .WithFooter("Slash commands: /create, /restart, /stats, /inventory, /help, /map");
    }

    private async Task SendCharacterCreatedEmbed(SocketThreadChannel thread, PlayerCharacter player)
    {
        var embed = BuildCharacterEmbed(player)
            .WithDescription("Your character has been created! Type `look` to see your surroundings.");
        await thread.SendMessageAsync(embed: embed.Build());
    }

    // ==================== Helpers ====================

    private static string FormatStatusBar(PlayerCharacter player) =>
        $"❤️ {player.Hp}/{player.MaxHp}  ✨ {player.Mp}/{player.MaxMp}  💰 {player.Gold}g  ⭐ Lv.{player.Level} ({player.Xp} XP)";

    private static bool IsExitKeyword(string text)
    {
        var exits = new[] { "leave", "bye", "walk away", "stop talking", "goodbye", "farewell" };
        if (exits.Any(e => text.Contains(e))) return true;
        if (text.StartsWith("go ")) return true;
        return false;
    }

    private static bool IsFinalizationPhrase(string text)
    {
        var phrases = new[] { "looks good", "done", "let's go", "perfect", "i'm done", "im done",
            "start", "begin", "that's good", "that works", "accepted", "confirm", "yes", "ok",
            "good enough", "fine", "go", "ready" };
        return phrases.Any(p => text.Contains(p));
    }

    private static Dictionary<string, int> AssignStatsFromOrder(List<string> order, int[] array)
    {
        var stats = new Dictionary<string, int>
        {
            ["str"] = 10, ["dex"] = 10, ["con"] = 10, ["int"] = 10, ["wis"] = 10, ["cha"] = 10
        };

        var validStats = new[] { "str", "dex", "con", "int", "wis", "cha" };
        var normalizedOrder = order
            .Select(s => s.ToLowerInvariant())
            .Where(s => validStats.Contains(s))
            .Distinct()
            .ToList();

        // Fill missing stats
        foreach (var s in validStats)
            if (!normalizedOrder.Contains(s))
                normalizedOrder.Add(s);

        for (int i = 0; i < Math.Min(normalizedOrder.Count, array.Length); i++)
            stats[normalizedOrder[i]] = array[i];

        return stats;
    }

    private static string FormatMod(int stat)
    {
        var mod = (stat - 10) / 2;
        return mod >= 0 ? $"+{mod}" : mod.ToString();
    }

    private static string? ExtractMentionId(string input)
    {
        // Discord mention format: <@123456> or <@!123456>
        var cleaned = input.Trim().TrimStart('<').TrimStart('@').TrimStart('!').TrimEnd('>');
        return ulong.TryParse(cleaned, out _) ? cleaned : null;
    }

    private static IEnumerable<string> SummarizeEntities(IEnumerable<string> names)
    {
        return names.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} (x{g.Count()})" : g.Key);
    }

    private async Task PostToAdminChannelAsync(string message)
    {
        try
        {
            foreach (var guild in _client.Guilds)
            {
                var channel = guild.TextChannels.FirstOrDefault(c => c.Name == AdminChannelName);
                if (channel is not null)
                {
                    await channel.SendMessageAsync(message);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post to admin channel");
        }
    }

    private static async Task SendChunkedAsync(IMessageChannel channel, string text)
    {
        const int maxLength = 2000;
        for (int i = 0; i < text.Length; i += maxLength)
        {
            var chunk = text.Substring(i, Math.Min(maxLength, text.Length - i));
            await channel.SendMessageAsync(chunk);
        }
    }
}

/// <summary>Tracks the AI-driven character creation conversation state.</summary>
internal class AiCreationSession
{
    public string DiscordId { get; }
    public bool HasSheet { get; set; }
    public CharacterCreationAiResponse? LastAiResponse { get; set; }
    public string? LastSheetJson { get; set; }
    public bool AwaitingRestartConfirmation { get; set; }

    /// <summary>Fallback to the rigid 5-step wizard if narrator is down.</summary>
    public CharacterCreation.CharacterCreationSession? FallbackSession { get; set; }

    public AiCreationSession(string discordId) => DiscordId = discordId;
}