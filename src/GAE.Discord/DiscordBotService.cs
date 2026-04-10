using Discord;
using Discord.Net;
using Discord.WebSocket;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.Configuration;
using GAE.Engine.Worlds;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace GAE.Discord;

/// <summary>
/// Discord bot service — manages player threads, embeds, slash commands,
/// AI character creation, conversation mode, admin commands, and narrator fallback.
/// </summary>
public class DiscordBotService : IHostedService, IDiscordNotifier
{
    private readonly DiscordSocketClient _client;
    private readonly IGameEngine _engine;
    private readonly IStateManager _stateManager;
    private readonly INarratorService _narrator;
    private readonly IWorldRepository _worldRepository;
    private readonly IContentRegistryService _registry;
    private readonly GameRulesConfig _rules;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly string _token;
    private readonly string _sessionsPath;

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
        IWorldRepository worldRepository,
        IContentRegistryService registry,
        GameRulesConfig rules,
        ILogger<DiscordBotService> logger,
        string token,
        string dataDir = "")
    {
        _client = client;
        _engine = engine;
        _stateManager = stateManager;
        _narrator = narrator;
        _worldRepository = worldRepository;
        _registry = registry;
        _rules = rules;
        _logger = logger;
        _token = token;
        _sessionsPath = string.IsNullOrEmpty(dataDir) ? "" : Path.Combine(dataDir, "pending-creations.json");

        // Restore any creation sessions that survived a container restart
        RestoreCreationSessions();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += msg =>
        {
            _logger.LogInformation("[Discord] {Message}", msg.Message);
            return Task.CompletedTask;
        };

        // Fire-and-forget wrappers so handlers don't block the Discord gateway task
        _client.MessageReceived += msg => { _ = Task.Run(() => HandleMessageAsync(msg)); return Task.CompletedTask; };
        _client.SlashCommandExecuted += cmd => { _ = Task.Run(() => HandleSlashCommandAsync(cmd)); return Task.CompletedTask; };
        _client.Ready += () => { _ = Task.Run(OnReadyAsync); return Task.CompletedTask; };

        await _client.LoginAsync(TokenType.Bot, _token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Discord bot shutting down — persisting {Count} creation session(s)", _creationSessions.Count);
        PersistCreationSessions();
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
                new SlashCommandBuilder().WithName("cyoa").WithDescription("Start a Choose Your Own Adventure story")
                    .AddOption("theme", ApplicationCommandOptionType.String, "Optional theme (e.g. 'a heist in a floating city')", isRequired: false),
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
                case "cyoa":
                    await command.DeferAsync(ephemeral: true);
                    await HandleCyoaSlashAsync(command, discordId);
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
            // Verify the thread still exists — if deleted/lost, recreate it
            SocketThreadChannel? existingThread = null;
            if (existing.ThreadId.HasValue && command.Channel is SocketTextChannel parentChannel)
            {
                existingThread = parentChannel.Guild.GetChannel(existing.ThreadId.Value) as SocketThreadChannel;
            }

            if (existingThread is null)
            {
                // Thread is gone — recreate it and re-link the player
                _logger.LogWarning("Thread {ThreadId} for player {Name} no longer exists, recreating",
                    existing.ThreadId, existing.Name);

                var newThread = await CreatePlayerThreadAsync(command.Channel, command.User);
                if (newThread is null)
                {
                    await command.FollowupAsync("Your adventure thread was lost and I couldn't create a new one. Make sure I have Thread permissions.", ephemeral: true);
                    return;
                }

                existing.ThreadId = newThread.Id;
                await _stateManager.SavePlayerAsync(existing);

                await command.FollowupAsync(
                    $"Welcome back, **{existing.Name}**! Your old thread was lost, so I created a new one. Head to <#{newThread.Id}> to continue your adventure!\n" +
                    $"*(If you'd rather start over with a new character, use `/restart`.)*", ephemeral: true);

                // Send a welcome-back message in the new thread
                await newThread.SendMessageAsync(
                    $"⚔️ **{existing.Name}** returns! Your adventure thread was recreated.\n" +
                    $"You can pick up where you left off — just type a command like `look` or `stats`.");
                return;
            }

            // Thread exists — unarchive if needed
            if (existingThread.IsArchived)
            {
                try { await existingThread.ModifyAsync(props => props.Archived = false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to unarchive thread {ThreadId}", existingThread.Id); }
            }

            await command.FollowupAsync(
                $"You already have a character: **{existing.Name}**. Head to <#{existingThread.Id}> to play!\n" +
                $"*(If you need to start over, use `/restart`.)*", ephemeral: true);
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
                PersistCreationSessions();
                await command.FollowupAsync("Character creation reset. Use `/create` to start fresh.", ephemeral: true);
                return;
            }
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        await command.FollowupAsync("Are you sure? Type `yes` in your adventure thread to confirm. Your character will be wiped.", ephemeral: true);
        _creationSessions[command.User.Id] = new AiCreationSession(discordId) { AwaitingRestartConfirmation = true };
        PersistCreationSessions();
    }

    private async Task HandleStatsSlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        await command.FollowupAsync(BuildCharacterStatsText(player), ephemeral: true);
    }

    private async Task HandleInventorySlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }
        await command.FollowupAsync(BuildInventoryText(player), ephemeral: true);
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

    private async Task HandleCyoaSlashAsync(SocketSlashCommand command, string discordId)
    {
        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
        if (player is null)
        {
            await command.FollowupAsync("You don't have a character. Use `/create` first.", ephemeral: true);
            return;
        }

        if (player.GameMode == Core.Models.GameMode.ChooseYourOwnAdventure)
        {
            await command.FollowupAsync("You're already in a CYOA adventure! Type choices or `cyoa end` to quit.", ephemeral: true);
            return;
        }

        // Find the player's thread to send the CYOA response
        SocketThreadChannel? thread = null;
        if (player.ThreadId.HasValue && command.Channel is SocketTextChannel parentChannel)
            thread = parentChannel.Guild.GetChannel(player.ThreadId.Value) as SocketThreadChannel;

        if (thread is null)
        {
            await command.FollowupAsync("I can't find your adventure thread. Try sending a message in your thread first.", ephemeral: true);
            return;
        }

        // Build the cyoa start command, optionally with a theme
        var theme = command.Data.Options.FirstOrDefault(o => o.Name == "theme")?.Value as string;
        var rawInput = string.IsNullOrWhiteSpace(theme) ? "cyoa start" : $"cyoa start {theme}";

        await command.FollowupAsync("📖 Starting your adventure...", ephemeral: true);

        await thread.TriggerTypingAsync();
        var action = _engine.ParseCommand(player.Id, rawInput);
        var result = await ProcessWithNarratorFallbackAsync(player, action, thread);

        // Refresh player state after CYOA start
        player = await _stateManager.GetPlayerByDiscordIdAsync(discordId) ?? player;

        // Rename thread for CYOA mode
        await RenameCyoaThreadAsync(thread, player, isCyoaActive: true);

        // Send the opening scene as a CYOA embed
        await SendGameResponseAsync(thread, player, action, result);
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

        // Strip the @mention to get the player's actual question
        var question = content
            .Replace($"<@{botUser.Id}>", "")
            .Replace($"<@!{botUser.Id}>", "")
            .Trim();

        var player = await _stateManager.GetPlayerByDiscordIdAsync(discordId);

        // No character yet — welcome message with getting-started info
        if (player is null)
        {
            if (string.IsNullOrWhiteSpace(question) || question.Length < 3)
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
                return;
            }

            // Even without a character, let them chat with the guide
            await HandleGuideChat(message, null, null, question);
            return;
        }

        // Player exists — if it's just a bare @mention with no question, show status
        if (string.IsNullOrWhiteSpace(question) || question.Length < 3)
        {
            var link = player.ThreadId.HasValue ? $"<#{player.ThreadId}>" : "your adventure thread";
            await message.Channel.SendMessageAsync(
                $"Hey {message.Author.Mention}! You're playing as **{player.Name}** ({player.Race} {player.Class}). " +
                $"Head to {link} to continue your adventure!\n\n" +
                "**Commands:**\n" +
                "> `/stats` — View your character sheet\n" +
                "> `/inventory` — Check your gear\n" +
                "> `/map` — See discovered rooms\n" +
                "> `/restart` — Start over with a new character\n\n" +
                "*Or just @mention me with a question and I'll answer in character!*");
            return;
        }

        // Player asked a question — guide chat!
        await HandleGuideChat(message, player, player.ActiveWorldId, question);
    }

    private async Task HandleGuideChat(SocketUserMessage message, PlayerCharacter? player, string? worldId, string question)
    {
        // Show typing indicator while the AI thinks
        using var typing = message.Channel.EnterTypingState();

        try
        {
            // Resolve world — use player's world, or find the Discord default
            World? resolvedWorld = null;
            if (worldId is not null)
                resolvedWorld = await _worldRepository.GetWorldAsync(worldId);
            resolvedWorld ??= await GetDiscordDefaultWorldAsync();
            worldId ??= resolvedWorld?.Id;

            // Resolve the narrator voice
            NarratorPreset? narrator = null;
            if (player?.NarratorPresetId is not null)
                narrator = _registry.NarratorPresets.GetById(player.NarratorPresetId);

            if (narrator is null && resolvedWorld?.DefaultNarratorPresetId is not null)
                narrator = _registry.NarratorPresets.GetById(resolvedWorld.DefaultNarratorPresetId);

            // Fall back to first selectable narrator if none set
            narrator ??= _registry.NarratorPresets.GetAll()
                .Where(n => n.IsSelectable)
                .OrderBy(n => n.SortOrder)
                .FirstOrDefault();

            // Build discovered lore context for the player
            var loreContext = "";
            if (player is not null && player.DiscoveredLore.Count > 0)
            {
                var discoveredEntries = player.DiscoveredLore
                    .Select(id => _registry.LoreEntries.GetById(id))
                    .Where(e => e is not null)
                    .Take(15) // Cap to avoid prompt bloat
                    .Select(e => $"- **{e!.Name}**: {e.Content}")
                    .ToList();

                if (discoveredEntries.Count > 0)
                    loreContext = "\n\nLore the player has discovered (use this to inform your answers):\n" + string.Join("\n", discoveredEntries);
            }

            // Build world context
            var worldContext = resolvedWorld is not null
                ? $"\nWorld: {resolvedWorld.Name} — {resolvedWorld.Description}"
                : "";

            // Build the narrator voice block
            var voiceBlock = narrator is not null
                ? $"You are **{narrator.Name}**, the player's guide.\n" +
                  $"Archetype: {narrator.Archetype}\n" +
                  $"Personality: {narrator.PersonalityPrompt}\n" +
                  $"Lore delivery style: {narrator.LoreDeliveryStyle ?? "engaging and in-character"}\n"
                : "You are a mysterious, world-weary guide in a fantasy RPG. Speak with personality and flavor.\n";

            var playerContext = player is not null
                ? $"You are speaking with **{player.Name}**, a Lv.{player.Level} {player.Race} {player.Class}."
                : "You are speaking with a newcomer who hasn't created a character yet.";

            var prompt = $"""
                {voiceBlock}
                {playerContext}
                {worldContext}
                {loreContext}

                This is a casual, in-character conversation in the tavern (the public channel).
                You are NOT narrating gameplay — no game state changes, no combat, no movement.
                Just chat as the guide/narrator. Be helpful, entertaining, and stay in character.
                If they ask about game lore, draw from what they've discovered.
                If they ask about mechanics, answer helpfully but in character.
                If they haven't started yet, encourage them to use /create.

                Keep your response under 300 words. Use Discord markdown.

                The player says: "{question}"
                """;

            await _narratorLock.WaitAsync();
            string response;
            try
            {
                response = await _narrator.GenerateContentAsync("text", prompt, null);
            }
            finally
            {
                _narratorLock.Release();
            }

            // Discord has a 2000 char limit — truncate if needed
            if (response.Length > 1900)
                response = response[..1900] + "\n\n*...the guide trails off, lost in thought.*";

            await message.Channel.SendMessageAsync($"{message.Author.Mention} {response}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guide chat failed for {User}", message.Author.Username);
            await message.Channel.SendMessageAsync(
                $"{message.Author.Mention} *The guide opens their mouth to speak, but the words fade to silence.* " +
                "(The AI narrator is unavailable right now. Try again in a moment!)");
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
            // Thread has no owner — check if this user has a player record we can re-link,
            // or if the thread looks like an adventure thread we can recover.
            var orphanPlayer = await _stateManager.GetPlayerByDiscordIdAsync(discordId);
            if (orphanPlayer is not null)
            {
                // Re-link the player to this thread
                orphanPlayer.ThreadId = thread.Id;
                await _stateManager.SavePlayerAsync(orphanPlayer);
                _logger.LogInformation("Re-linked player {Name} to thread {ThreadId} after state loss", orphanPlayer.Name, thread.Id);
                // Fall through to normal game processing below
            }
            else
            {
                // No player record — server was restarted and state was lost.
                // Auto-start character creation in this thread.
                await thread.SendMessageAsync(
                    "⚔️ Looks like the server was restarted and your character data was lost. " +
                    "Let's get you back in the game — starting character creation!");
                await StartAiCharacterCreation(thread, message.Author.Id, discordId);
                return;
            }
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
                    CleanupPersistedSessions();
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

        // !dm <message> — talk directly to Sir Thaddeus (the narrator) out-of-character
        if (content.StartsWith("!dm ", StringComparison.OrdinalIgnoreCase))
        {
            var dmMessage = content[4..].Trim();
            if (!string.IsNullOrEmpty(dmMessage))
            {
                await thread.TriggerTypingAsync();
                var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId);
                room ??= new Room { Id = player.CurrentRoomId, Name = "Unknown" };
                try
                {
                    await _narratorLock.WaitAsync();
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        var response = await _narrator.ProvideGuidanceAsync(player, room, dmMessage, cts.Token);
                        await thread.SendMessageAsync($"\U0001F3AD **Sir Thaddeus:** {response}");
                    }
                    finally { _narratorLock.Release(); }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "!dm narrator chat failed");
                    await thread.SendMessageAsync("\U0001F3AD **Sir Thaddeus:** *adjusts monocle* Forgive me — my thoughts wandered for a moment. Do try again.");
                }
            }
            return;
        }

        // No prefix required — thread is gated to owner only, so everything is a game command.
        // Strip leading ! if present (for habit/compatibility) but don't require it.
        var command = content.StartsWith('!') ? content[1..].Trim() : content;

        if (string.IsNullOrEmpty(command)) return;

        // Show typing indicator while processing
        await thread.TriggerTypingAsync();

        // Parse and process
        var action = _engine.ParseCommand(player.Id, command);
        var wasCyoa = player.GameMode == Core.Models.GameMode.ChooseYourOwnAdventure;
        var result = await ProcessWithNarratorFallbackAsync(player, action, thread);

        // Refresh player state so status bar reflects changes from this action
        player = await _stateManager.GetPlayerByDiscordIdAsync(discordId) ?? player;

        // Detect CYOA mode transitions and rename thread
        var isCyoaNow = player.GameMode == Core.Models.GameMode.ChooseYourOwnAdventure;
        if (!wasCyoa && isCyoaNow)
            await RenameCyoaThreadAsync(thread, player, isCyoaActive: true);

        // Format and send response
        await SendGameResponseAsync(thread, player, action, result);
    }

    // ==================== World Resolution ====================

    /// <summary>
    /// Find the world tagged 'discord-default', falling back to the hardcoded default,
    /// then to the first active world.
    /// </summary>
    private async Task<World?> GetDiscordDefaultWorldAsync()
    {
        var worlds = await _worldRepository.GetAllWorldsAsync();

        // Prefer the world tagged as Discord default
        var tagged = worlds.FirstOrDefault(w => w.Tags.Contains("discord-default", StringComparer.OrdinalIgnoreCase));
        if (tagged is not null) return tagged;

        // Fallback: hardcoded default world ID
        var hardcoded = worlds.FirstOrDefault(w => string.Equals(w.Id, WorldDefaults.DefaultWorldId, StringComparison.OrdinalIgnoreCase));
        if (hardcoded is not null) return hardcoded;

        // Last resort: first active world
        return worlds.FirstOrDefault(w => w.IsActive);
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

    private static readonly string FallbackCharacterCreationIntro = """
        A voice emerges from the ether — dry, amused, and impossibly old.

        *"Ah. Another soul stumbles through my door. I am the Narrator — the voice behind the curtain, the pen that writes your story as you live it. Adventures await, and I need someone to narrate. That's where you come in."*

        *"But before we get to the heroics — who exactly am I narrating? Tell me about yourself."*

        *(Describe yourself however you like: "I'm a sneaky halfling who picks pockets" or "I'm a massive orc who solves problems with fists" — or just tell me your name and I'll ask questions.)*
        """;

    private async Task StartAiCharacterCreation(SocketThreadChannel thread, ulong userId, string discordId)
    {
        var session = new AiCreationSession(discordId);
        _creationSessions[userId] = session;
        PersistCreationSessions();

        // Try to load the Discord default world's saved intro first
        string? savedIntro = null;
        World? activeWorld = null;
        try
        {
            activeWorld = await GetDiscordDefaultWorldAsync();
            _logger.LogInformation("Character creation: resolved world={WorldId}, narrator={NarratorId}, hasIntro={HasIntro}",
                activeWorld?.Id ?? "null",
                activeWorld?.DefaultNarratorPresetId ?? "null",
                !string.IsNullOrWhiteSpace(activeWorld?.CharacterCreationIntro));
            if (activeWorld is not null && !string.IsNullOrWhiteSpace(activeWorld.CharacterCreationIntro))
                savedIntro = activeWorld.CharacterCreationIntro;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch world intro");
        }

        // If we have a saved intro, use it directly
        if (!string.IsNullOrWhiteSpace(savedIntro))
        {
            _logger.LogInformation("Using saved character creation intro from world {WorldId}", activeWorld?.Id);
            await thread.SendMessageAsync(savedIntro);
            return;
        }

        // No saved intro — generate one dynamically using the narrator's voice
        string intro = FallbackCharacterCreationIntro;
        try
        {
            // Resolve the world's narrator
            NarratorPreset? narrator = null;
            if (activeWorld?.DefaultNarratorPresetId is not null)
                narrator = _registry.NarratorPresets.GetById(activeWorld.DefaultNarratorPresetId);

            narrator ??= _registry.NarratorPresets.GetAll()
                .Where(n => n.IsSelectable)
                .OrderBy(n => n.SortOrder)
                .FirstOrDefault();

            _logger.LogInformation("Generating dynamic intro with narrator={NarratorName} ({NarratorId})",
                narrator?.Name ?? "none", narrator?.Id ?? "none");

            if (narrator is not null)
            {
                var worldContext = activeWorld is not null
                    ? $"World: {activeWorld.Name} — {activeWorld.Description}"
                    : "A fantasy RPG world";

                var prompt = $"""
                    You are **{narrator.Name}**, the narrator/guide for a text RPG.
                    Archetype: {narrator.Archetype}
                    Personality: {narrator.PersonalityPrompt}
                    {worldContext}

                    Write a character creation greeting for a new player arriving in their adventure thread.
                    You MUST:
                    1. Introduce yourself by name — e.g. "I am {narrator.Name}" — make it fun and memorable
                    2. Give a brief, flavorful hint about the world and its conflict (don't spoil)
                    3. Ask the player to describe who they are
                    4. End with this exact instruction in italics:
                       *(Describe yourself however you like: "I'm a sneaky halfling who picks pockets" or "I'm a massive orc who solves problems with fists" — or just tell me your name and I'll ask questions.)*

                    Keep it 3-4 paragraphs. Use Discord markdown (bold, italics). Stay fully in character.
                    Return ONLY the message text, no JSON wrapping.
                    """;

                await _narratorLock.WaitAsync();
                try
                {
                    intro = await _narrator.GenerateContentAsync("text", prompt, null);
                }
                finally
                {
                    _narratorLock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate narrator intro, using fallback");
        }

        await thread.SendMessageAsync(intro);
    }

    /// <summary>
    /// After character creation is finalized, the narrator describes the player arriving
    /// in the world — a cinematic moment before the room card appears.
    /// </summary>
    private async Task SendArrivalNarration(SocketThreadChannel thread, PlayerCharacter player)
    {
        try
        {
            await thread.TriggerTypingAsync();

            var world = await GetDiscordDefaultWorldAsync();

            // Resolve narrator
            NarratorPreset? narrator = null;
            if (player.NarratorPresetId is not null)
                narrator = _registry.NarratorPresets.GetById(player.NarratorPresetId);
            if (narrator is null && world?.DefaultNarratorPresetId is not null)
                narrator = _registry.NarratorPresets.GetById(world.DefaultNarratorPresetId);
            narrator ??= _registry.NarratorPresets.GetAll()
                .Where(n => n.IsSelectable).OrderBy(n => n.SortOrder).FirstOrDefault();

            // Get the spawn room for flavor
            var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId);

            var voiceBlock = narrator is not null
                ? $"You are **{narrator.Name}**, the narrator.\nArchetype: {narrator.Archetype}\nPersonality: {narrator.PersonalityPrompt}\n"
                : "You are a mysterious narrator in a fantasy RPG.\n";

            var worldContext = world is not null
                ? $"World: {world.Name} — {world.Description}"
                : "";

            var roomContext = room is not null
                ? $"The player's starting location is: {room.Name} — {room.Description}"
                : "The player arrives at a tavern.";

            var prompt = $"""
                {voiceBlock}
                {worldContext}
                {roomContext}

                The player just finished creating their character. Write a short, atmospheric
                arrival scene describing them materializing/arriving in the world for the first time.

                Character: **{player.Name}**, a Lv.{player.Level} {player.Race} {player.Class}.
                Backstory hint: {player.Backstory ?? "mysterious origins"}

                Guidelines:
                - Address the player in second person ("You step through...", "The mists part...")
                - Describe the sensory experience of arriving — sights, sounds, smells
                - End with a hint of what they see (the room/tavern) but don't describe the full room
                - Stay in your narrator voice
                - 2-3 short paragraphs, use Discord markdown (bold, italics)
                - Return ONLY the narration, no JSON
                """;

            await _narratorLock.WaitAsync();
            string narration;
            try
            {
                narration = await _narrator.GenerateContentAsync("text", prompt, null);
            }
            finally
            {
                _narratorLock.Release();
            }

            if (narration.Length > 1900)
                narration = narration[..1900] + "\n*...the vision sharpens.*";

            await thread.SendMessageAsync(narration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate arrival narration for {Player}", player.Name);
            // Graceful fallback — just skip the arrival narration
            await thread.SendMessageAsync(
                $"*The mists part, and **{player.Name}** steps into a new world...*");
        }
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

            // If we already have a sheet, try to apply changes mechanically (stat/name/race/class overrides)
            if (session.HasSheet && session.LastAiResponse is not null)
            {
                bool changed = CharacterCreation.SheetOverrides.ApplyDirect(input, session.LastAiResponse);
                if (changed)
                {
                    // Re-display updated sheet
                    Dictionary<string, int> updatedStats;
                    if (session.LastAiResponse.Stats is not null && session.LastAiResponse.Stats.Count >= 6)
                        updatedStats = new Dictionary<string, int>(session.LastAiResponse.Stats, StringComparer.OrdinalIgnoreCase);
                    else
                    {
                        var standardArr = new[] { 15, 14, 13, 12, 10, 8 };
                        updatedStats = AssignStatsFromOrder(session.LastAiResponse.StatOrder, standardArr);
                    }
                    session.LastSheetJson = System.Text.Json.JsonSerializer.Serialize(session.LastAiResponse);

                    var updatedEmbed = new EmbedBuilder()
                        .WithTitle("\u2694\uFE0F CHARACTER SHEET")
                        .WithColor(Color.Gold)
                        .AddField("Name", session.LastAiResponse.Name ?? "???", inline: true)
                        .AddField("Race", session.LastAiResponse.Race, inline: true)
                        .AddField("Class", session.LastAiResponse.Class, inline: true);
                    if (!string.IsNullOrWhiteSpace(session.LastAiResponse.Gender))
                        updatedEmbed.AddField("Gender", session.LastAiResponse.Gender, inline: true);
                    if (session.LastAiResponse.PersonalItems.Count > 0)
                        updatedEmbed.AddField("Personal Items", string.Join(", ", session.LastAiResponse.PersonalItems), inline: false);
                    updatedEmbed.AddField("Stats",
                            $"STR: {updatedStats.GetValueOrDefault("str", 10)} ({FormatMod(updatedStats.GetValueOrDefault("str", 10))})  DEX: {updatedStats.GetValueOrDefault("dex", 10)} ({FormatMod(updatedStats.GetValueOrDefault("dex", 10))})\n" +
                            $"CON: {updatedStats.GetValueOrDefault("con", 10)} ({FormatMod(updatedStats.GetValueOrDefault("con", 10))})  INT: {updatedStats.GetValueOrDefault("int", 10)} ({FormatMod(updatedStats.GetValueOrDefault("int", 10))})\n" +
                            $"WIS: {updatedStats.GetValueOrDefault("wis", 10)} ({FormatMod(updatedStats.GetValueOrDefault("wis", 10))})  CHA: {updatedStats.GetValueOrDefault("cha", 10)} ({FormatMod(updatedStats.GetValueOrDefault("cha", 10))})")
                        .AddField("Backstory", session.LastAiResponse.Backstory)
                        .WithFooter("Say \"looks good\" to start, or describe changes you want.");
                    await thread.SendMessageAsync(embed: updatedEmbed.Build());
                    return;
                }
                else
                {
                    // Couldn't parse the change — tell the user what we can handle
                    await message.Channel.SendMessageAsync(
                        "⚠️ The storyteller is resting, so I can only handle direct changes right now.\n" +
                        "Try: `change str to 15`, `change name to Grok`, `change race to Elf`, `change class to Ranger`\n" +
                        "Or say **\"looks good\"** to start your adventure!");
                    return;
                }
            }

            // Fallback to rigid wizard (no sheet yet)
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
                CleanupPersistedSessions();

                await thread.ModifyAsync(props => props.Name = $"\u2694\uFE0F {player.Name}'s Adventure");
                await SendCharacterCreatedEmbed(thread, player);
                await SendHeroIntroAndRoomAsync(thread, player);
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

        // Store the AI response and persist to disk so it survives container restarts
        session.LastAiResponse = aiResponse;
        session.HasSheet = true;
        PersistCreationSessions();

        // Prefer AI-assigned stats; fall back to standard array ordering
        Dictionary<string, int> stats;
        if (aiResponse.Stats is not null && aiResponse.Stats.Count >= 6)
        {
            stats = new Dictionary<string, int>(aiResponse.Stats, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var standardArray = new[] { 15, 14, 13, 12, 10, 8 };
            stats = AssignStatsFromOrder(aiResponse.StatOrder, standardArray);
        }

        session.LastSheetJson = System.Text.Json.JsonSerializer.Serialize(aiResponse);

        var sheetEmbed = new EmbedBuilder()
            .WithTitle("\u2694\uFE0F CHARACTER SHEET")
            .WithColor(Color.Gold)
            .AddField("Name", string.IsNullOrWhiteSpace(aiResponse.Name) ? "???" : aiResponse.Name, inline: true)
            .AddField("Race", string.IsNullOrWhiteSpace(aiResponse.Race) ? "???" : aiResponse.Race, inline: true)
            .AddField("Class", string.IsNullOrWhiteSpace(aiResponse.Class) ? "???" : aiResponse.Class, inline: true);
        if (!string.IsNullOrWhiteSpace(aiResponse.Gender))
            sheetEmbed.AddField("Gender", aiResponse.Gender, inline: true);
        if (aiResponse.PersonalItems.Count > 0)
            sheetEmbed.AddField("Personal Items", string.Join(", ", aiResponse.PersonalItems), inline: false);
        sheetEmbed.AddField("Stats",
                $"STR: {stats.GetValueOrDefault("str", 10)} ({FormatMod(stats.GetValueOrDefault("str", 10))})  DEX: {stats.GetValueOrDefault("dex", 10)} ({FormatMod(stats.GetValueOrDefault("dex", 10))})\n" +
                $"CON: {stats.GetValueOrDefault("con", 10)} ({FormatMod(stats.GetValueOrDefault("con", 10))})  INT: {stats.GetValueOrDefault("int", 10)} ({FormatMod(stats.GetValueOrDefault("int", 10))})\n" +
                $"WIS: {stats.GetValueOrDefault("wis", 10)} ({FormatMod(stats.GetValueOrDefault("wis", 10))})  CHA: {stats.GetValueOrDefault("cha", 10)} ({FormatMod(stats.GetValueOrDefault("cha", 10))})")
            .AddField("Backstory", string.IsNullOrWhiteSpace(aiResponse.Backstory) ? "A mysterious past yet to be revealed..." : aiResponse.Backstory)
            .WithFooter("Say \"looks good\" to start, or describe changes you want.");

        await thread.SendMessageAsync(embed: sheetEmbed.Build());

        if (aiResponse.FollowUpQuestion is not null)
            await thread.SendMessageAsync(aiResponse.FollowUpQuestion);
    }

    private async Task FinalizeCharacterCreationAsync(SocketUserMessage message, SocketThreadChannel thread,
        AiCreationSession session, string discordId)
    {
        var ai = session.LastAiResponse!;

        // Prefer AI-assigned individual stats; fall back to standard array ordering
        Dictionary<string, int> stats;
        if (ai.Stats is not null && ai.Stats.Count >= 6)
        {
            stats = new Dictionary<string, int>(ai.Stats, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var standardArray = new[] { 15, 14, 13, 12, 10, 8 };
            stats = AssignStatsFromOrder(ai.StatOrder, standardArray);
        }

        var concept = new CharacterConcept
        {
            PlayerDiscordId = discordId,
            Name = ai.Name ?? message.Author.Username,
            Gender = ai.Gender,
            Race = ai.Race,
            Class = ai.Class,
            Backstory = ai.Backstory,
            PersonalItems = ai.PersonalItems,
            StatMethod = StatAllocationMethod.Manual,
            ManualStats = stats,
            StartingGold = ai.StartingGold
        };

        var player = await _engine.CreateCharacterFromConceptAsync(concept);
        player.DiscordId = discordId;
        player.ThreadId = thread.Id;
        await _stateManager.SavePlayerAsync(player);
        _creationSessions.Remove(message.Author.Id);
        CleanupPersistedSessions();

        await thread.ModifyAsync(props => props.Name = $"\u2694\uFE0F {player.Name}'s Adventure");
        await SendCharacterCreatedEmbed(thread, player);
        await SendHeroIntroAndRoomAsync(thread, player);

        // Notify admin channel
        var genderTag = string.IsNullOrWhiteSpace(player.Gender) ? "" : $" ({player.Gender})";
        await PostToAdminChannelAsync($"\U0001F4E5 **{player.Name}**{genderTag} ({message.Author.Username}) created a character — {player.Race} {player.Class}");
    }

    // ==================== Restart ====================

    private async Task HandleRestartConfirmedAsync(SocketUserMessage message, SocketThreadChannel thread, string discordId)
    {
        _creationSessions.Remove(message.Author.Id);
        CleanupPersistedSessions();

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
        // CYOA mode — render as themed embed
        if (player.GameMode == Core.Models.GameMode.ChooseYourOwnAdventure ||
            action.Type is Core.Models.ActionType.CyoaStart or Core.Models.ActionType.CyoaEnd)
        {
            await SendCyoaResponseAsync(thread, player, action, result);
            return;
        }

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

        // Inventory — console-style text
        if (action.Type == Core.Models.ActionType.Inventory)
        {
            await thread.SendMessageAsync(BuildInventoryText(player));
            return;
        }

        // Stats — console-style text
        if (action.Type == Core.Models.ActionType.Stats)
        {
            await thread.SendMessageAsync(BuildCharacterStatsText(player));
            return;
        }

        // Spellbook — console-style text
        if (action.Type == Core.Models.ActionType.Spellbook)
        {
            await thread.SendMessageAsync(result.MechanicalSummary);
            return;
        }

        // Help — show as proper embed
        if (action.Type == Core.Models.ActionType.Help)
        {
            await thread.SendMessageAsync(embed: BuildHelpEmbed().Build());
            return;
        }

        // Map — send as a code block embed
        if (action.Type == Core.Models.ActionType.Map)
        {
            var mapEmbed = new EmbedBuilder()
                .WithTitle("World Map")
                .WithDescription(result.MechanicalSummary ?? "No map data available.")
                .WithColor(Color.Orange)
                .WithFooter(FormatStatusBar(player));
            await thread.SendMessageAsync(embed: mapEmbed.Build());
            return;
        }

        // Cast — plain text for consistency
        if (action.Type == Core.Models.ActionType.Cast)
        {
            var spellTitle = result.Success ? "✨ **Spell Cast!**" : "💨 **Spell Fizzle!**";
            var spellNarration = result.Narration ?? result.MechanicalSummary ?? "";
            var spellText = $"{spellTitle}\n{spellNarration}";
            if (!string.IsNullOrEmpty(result.MechanicalSummary) && result.Narration is not null)
                spellText += $"\n> {result.MechanicalSummary}";
            spellText += $"\n> {FormatStatusBar(player)}";
            await SendChunkedAsync(thread, spellText);
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

        // Plain text response — consistent with room/combat style
        var narrationText = result.Narration ?? result.MechanicalSummary ?? "";
        if (!result.Success && !string.IsNullOrWhiteSpace(narrationText))
            narrationText = $"❌ **Failed!** {narrationText}";
        else if (!result.Success)
            narrationText = "❌ **Failed!** That didn't work.";

        var sb = new StringBuilder(narrationText);

        // Dice rolls in spoiler
        if (result.DiceRolls.Count > 0)
        {
            var rollLines = result.DiceRolls.Select(roll =>
            {
                var rollStr = $"🎲 {roll.Total}";
                if (roll.TargetNumber.HasValue)
                    rollStr += $" vs {roll.TargetNumber}";
                rollStr += $" ({roll.Purpose})";
                rollStr += FormatOutcomeBadge(roll);
                return rollStr;
            });
            sb.Append($"\n||{string.Join(" · ", rollLines)}||");
        }

        // Compact changes line
        var changes = new List<string>();
        if (result.GoldChange > 0) changes.Add($"💰 +{result.GoldChange}g");
        if (result.GoldChange < 0) changes.Add($"💰 {result.GoldChange}g");
        if (result.XpGained > 0) changes.Add($"⭐ +{result.XpGained} XP");
        foreach (var item in result.ItemsGained)
            changes.Add($"📥 +{item.Name}{(item.Quantity > 1 ? $" x{item.Quantity}" : "")}");
        foreach (var item in result.ItemsLost)
            changes.Add($"📤 -{item.Name}{(item.Quantity > 1 ? $" x{item.Quantity}" : "")}");

        if (changes.Count > 0)
            sb.Append($"\n> {string.Join("  ", changes)}");

        // Status footer
        sb.Append($"\n> {FormatStatusBar(player)}");

        await SendChunkedAsync(thread, sb.ToString());
    }

    // ==================== CYOA Response Formatting ====================

    /// <summary>
    /// Renders a CYOA action result as a Discord embed with health-colored border,
    /// narration, numbered choices, and inventory/health in the footer.
    /// </summary>
    private async Task SendCyoaResponseAsync(SocketThreadChannel thread, PlayerCharacter player, GameAction action, ActionResult result)
    {
        var narration = result.Narration ?? result.MechanicalSummary ?? "";

        // Ending — the narration already contains scene + epilogue + summary from the engine
        if (action.Type == Core.Models.ActionType.CyoaEnd ||
            result.StateChanges.Any(sc => sc.Property == "GameMode" && sc.NewValue == nameof(Core.Models.GameMode.FullRpg)))
        {
            var endEmbed = new EmbedBuilder()
                .WithTitle("📖 The End")
                .WithDescription(TruncateForEmbed(narration))
                .WithColor(Color.Gold)
                .WithFooter("Your adventure is complete. You've returned to the world.");
            await thread.SendMessageAsync(embed: endEmbed.Build());

            // Restore thread name
            await RenameCyoaThreadAsync(thread, player, isCyoaActive: false);
            return;
        }

        // Normal CYOA turn or opening
        var cyoa = player.CyoaState;
        var healthColor = GetCyoaHealthColor(cyoa?.Health);

        var embed = new EmbedBuilder()
            .WithColor(healthColor);

        // Split narration from choices (engine formats as "narration\n\n1. choice\n2. choice...")
        var (scene, choiceBlock) = SplitCyoaNarration(narration);
        embed.WithDescription(TruncateForEmbed(scene));

        if (!string.IsNullOrWhiteSpace(choiceBlock))
            embed.AddField("Your choices", choiceBlock);

        // Footer with health + inventory
        var footerParts = new List<string>();
        if (cyoa is not null)
        {
            footerParts.Add($"Health: {cyoa.Health}");
            if (cyoa.Inventory.Count > 0)
                footerParts.Add($"Items: {string.Join(", ", cyoa.Inventory)}");
            else
                footerParts.Add("Items: none");
        }
        if (footerParts.Count > 0)
            embed.WithFooter(string.Join("  ·  ", footerParts));

        // Save point notification
        if (result.MechanicalSummary?.Contains("save point", StringComparison.OrdinalIgnoreCase) == true)
        {
            await thread.SendMessageAsync("📌 **Save point reached.**");
        }

        await thread.SendMessageAsync(embed: embed.Build());
    }

    /// <summary>
    /// Returns a Discord Color based on CYOA health level:
    /// green (Healthy), yellow (Hurt), orange (Critical), red (Dead).
    /// </summary>
    private static Color GetCyoaHealthColor(Core.Models.CyoaHealthLevel? health) => health switch
    {
        Core.Models.CyoaHealthLevel.Healthy => Color.Green,
        Core.Models.CyoaHealthLevel.Hurt => new Color(255, 204, 0), // yellow
        Core.Models.CyoaHealthLevel.Critical => Color.Orange,
        Core.Models.CyoaHealthLevel.Dead => Color.Red,
        _ => Color.Teal
    };

    /// <summary>
    /// Splits CYOA narration into (scene text, choice block).
    /// The engine formats choices as numbered lines after the narration.
    /// </summary>
    private static (string Scene, string ChoiceBlock) SplitCyoaNarration(string narration)
    {
        // Look for the numbered choice list pattern that FormatCyoaNarration produces
        var choicePattern = System.Text.RegularExpressions.Regex.Match(narration,
            @"\n\n(\d+\.\s.+(?:\n\d+\.\s.+)*)$");

        if (choicePattern.Success)
        {
            var scene = narration[..choicePattern.Index].TrimEnd();
            var choices = choicePattern.Groups[1].Value.Trim();
            return (scene, choices);
        }

        return (narration, string.Empty);
    }

    /// <summary>
    /// Renames a player's thread to indicate CYOA mode (📖) or restore the original (⚔️).
    /// </summary>
    private async Task RenameCyoaThreadAsync(SocketThreadChannel thread, PlayerCharacter player, bool isCyoaActive)
    {
        try
        {
            var baseName = player.Name ?? "Adventurer";
            var newName = isCyoaActive
                ? $"📖 {baseName}'s Adventure"
                : $"⚔️ {baseName}'s Adventure";

            if (thread.Name != newName)
                await thread.ModifyAsync(props => props.Name = newName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to rename thread {ThreadId} for CYOA mode change", thread.Id);
        }
    }

    /// <summary>
    /// Truncates text to fit within Discord embed description limit (4096 chars).
    /// </summary>
    private static string TruncateForEmbed(string text, int max = 4000)
        => text.Length <= max ? text : text[..(max - 3)] + "...";

    /// <summary>
    /// Generates the hero intro narration, sends it, then shows the room card.
    /// Used only after first character creation — not for normal room transitions.
    /// </summary>
    private async Task SendHeroIntroAndRoomAsync(SocketThreadChannel thread, PlayerCharacter player)
    {
        try
        {
            var intro = await _engine.GenerateHeroIntroAsync(player.Id);
            if (!string.IsNullOrWhiteSpace(intro))
                await SendChunkedAsync(thread, intro);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hero intro failed for {PlayerId}, sending room directly", player.Id);
        }

        await SendRoomEntryAsync(thread, player);
    }

    private async Task SendRoomEntryAsync(SocketThreadChannel thread, PlayerCharacter player, ActionResult? result = null)
    {
        var room = await _stateManager.GetPlayerRoomAsync(player.Id, player.CurrentRoomId);
        if (room is null) return;

        // Send narration text first (above the room card)
        var narration = result?.Narration ?? result?.MechanicalSummary ?? "";
        if (!string.IsNullOrWhiteSpace(narration))
            await SendChunkedAsync(thread, narration);

        // Build console-style room card
        await thread.SendMessageAsync(await BuildRoomCardText(player, room));
    }

    private async Task<string> BuildRoomCardText(PlayerCharacter player, Room room)
    {
        // Inner width = characters between │ and │ (not counting borders)
        const int IW = 40;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```");

        // Title bar:  ┌── Room Name ──────────────────────────┐
        var titlePad = room.Name.Length > IW - 4 ? room.Name[..(IW - 4)] : room.Name;
        var dashesAfter = Math.Max(1, IW - titlePad.Length - 3);
        sb.AppendLine($"┌── {titlePad} {new string('─', dashesAfter)}┐");

        // Room description — word-wrap
        if (!string.IsNullOrWhiteSpace(room.Description))
        {
            foreach (var line in WordWrap(room.Description, IW - 2))
                sb.AppendLine($"│ {Pad(line, IW)}│");
        }

        // NPCs
        if (room.Npcs.Count > 0)
        {
            sb.AppendLine($"├{new string('─', IW + 1)}┤");
            sb.AppendLine($"│ {Pad("NPCs", IW)}│");
            foreach (var npc in room.Npcs)
            {
                var prefix = npc.IsHostile ? "!" : " ";
                var npcStr = $" {prefix} {npc.Name}";
                sb.AppendLine($"│ {Pad(npcStr, IW)}│");
            }
        }

        // Items
        if (room.Items.Count > 0)
        {
            sb.AppendLine($"├{new string('─', IW + 1)}┤");
            sb.AppendLine($"│ {Pad("Items", IW)}│");
            foreach (var item in room.Items)
            {
                var qty = item.Quantity > 1 ? $" x{item.Quantity}" : "";
                var itemStr = $"  {item.Name}{qty}";
                sb.AppendLine($"│ {Pad(itemStr, IW)}│");
            }
        }

        // Exits
        if (room.Exits.Count > 0)
        {
            sb.AppendLine($"├{new string('─', IW + 1)}┤");
            foreach (var e in room.Exits)
            {
                var targetRoom = await _stateManager.GetPlayerRoomAsync(player.Id, e.Value);
                var targetName = targetRoom?.Name ?? e.Value.Replace("_", " ");
                var exitStr = $"{e.Key} -> {targetName}";
                sb.AppendLine($"│ {Pad(exitStr, IW)}│");
            }
        }

        sb.AppendLine($"└{new string('─', IW + 1)}┘");

        // Status bar below the box
        sb.AppendLine($"  HP:{player.Hp}/{player.MaxHp}  MP:{player.Mp}/{player.MaxMp}  Gold:{player.Gold}");
        sb.Append("```");
        return sb.ToString();
    }

    /// <summary>Pad or truncate a string to exactly the given width.</summary>
    private static string Pad(string text, int width) =>
        text.Length >= width ? text[..width] : text.PadRight(width);

    private static List<string> WordWrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length + word.Length + 1 > maxWidth && current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    private async Task SendCombatResultAsync(SocketThreadChannel thread, PlayerCharacter player, ActionResult result)
    {
        var combatStatus = result.InteractionUpdate?.CombatStatus ?? "ongoing";

        // Use the mechanical summary as the primary combat text — it already contains
        // round headers, narrative flavor, code-block HP bars, and command hints.
        // Send as a plain message so there's no embed description limit (4096 char)
        // and it renders like a console game.
        var text = result.MechanicalSummary ?? "";

        if (combatStatus is "victory" or "defeat" or "fled")
        {
            var title = combatStatus switch
            {
                "victory" => "⚔️ **Victory!**",
                "defeat" => "💀 **Defeated...**",
                "fled" => "🏃 **Fled!**",
                _ => "⚔️ **Combat**"
            };

            // Build full combat summary as plain text
            var combatSb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(text))
                combatSb.AppendLine(text);
            combatSb.AppendLine();
            combatSb.AppendLine(title);

            // Rewards
            var rewards = new List<string>();
            if (result.XpGained > 0) rewards.Add($"⭐ +{result.XpGained} XP");
            if (result.GoldChange > 0) rewards.Add($"💰 +{result.GoldChange}g");
            foreach (var item in result.ItemsGained)
                rewards.Add($"🎁 {item.Name}");
            if (rewards.Count > 0)
                combatSb.AppendLine($"> {string.Join("  ", rewards)}");
            combatSb.AppendLine($"> {FormatStatusBar(player)}");

            await SendChunkedAsync(thread, combatSb.ToString());

            if (result.IsVictory)
                await SendVictoryAnnouncementAsync(thread, player);
        }
        else
        {
            // Ongoing combat: send as plain text for clean console-style rendering
            if (!string.IsNullOrWhiteSpace(text))
                await SendChunkedAsync(thread, text);
            else
                await thread.SendMessageAsync("*Combat continues...*");
        }
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

        // !dm msg @player "message" — send system message to a player's thread
        if (cmd.StartsWith("msg ", StringComparison.OrdinalIgnoreCase) && !cmd.StartsWith("msg-all", StringComparison.OrdinalIgnoreCase))
        {
            var rest = cmd[4..].Trim();
            var spaceIdx = rest.IndexOf(' ');
            if (spaceIdx < 0) { await message.Channel.SendMessageAsync("Usage: `!dm msg @player \"message\"`"); return; }
            var targetId = ExtractMentionId(rest[..spaceIdx]);
            if (targetId is null) { await message.Channel.SendMessageAsync("Invalid player mention."); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }
            if (!player.ThreadId.HasValue) { await message.Channel.SendMessageAsync("Player has no active thread."); return; }

            var sysMsg = rest[(spaceIdx + 1)..].Trim().Trim('"');
            var guild = (message.Channel as SocketGuildChannel)?.Guild;
            var playerThread = guild?.GetChannel(player.ThreadId.Value) as SocketThreadChannel;
            if (playerThread is not null)
            {
                await playerThread.SendMessageAsync($"\U0001F4DC **[System Message]** {sysMsg}");
                await message.Channel.SendMessageAsync($"\u2705 Message sent to {player.Name}.");
            }
            else
                await message.Channel.SendMessageAsync("Could not find player thread.");
            return;
        }

        // !dm msg-all "message" — send system message to ALL adventure threads (including orphaned ones)
        if (cmd.StartsWith("msg-all ", StringComparison.OrdinalIgnoreCase))
        {
            var sysMsg = cmd[8..].Trim().Trim('"');
            var guild = (message.Channel as SocketGuildChannel)?.Guild;
            if (guild is null) { await message.Channel.SendMessageAsync("Guild not found."); return; }

            int sent = 0;

            // First, send to all known player threads
            var players = await _stateManager.GetAllPlayersAsync();
            var knownThreadIds = new HashSet<ulong>();
            foreach (var p in players.Where(p => p.ThreadId.HasValue))
            {
                knownThreadIds.Add(p.ThreadId!.Value);
                var playerThread = guild.GetChannel(p.ThreadId!.Value) as SocketThreadChannel;
                if (playerThread is not null)
                {
                    await playerThread.SendMessageAsync($"\U0001F4DC **[System Message]** {sysMsg}");
                    sent++;
                }
            }

            // Also scan for any adventure threads we don't have player records for
            // (orphaned threads from server restart)
            var mainChannel = guild.TextChannels.FirstOrDefault(c => c.Name == MainChannelName);
            if (mainChannel is not null)
            {
                var activeThreads = mainChannel.Threads;
                foreach (var t in activeThreads)
                {
                    if (!knownThreadIds.Contains(t.Id) && t.Name.Contains("Adventure"))
                    {
                        try
                        {
                            await t.SendMessageAsync($"\U0001F4DC **[System Message]** {sysMsg}");
                            sent++;
                        }
                        catch { /* thread may be archived/inaccessible */ }
                    }
                }
            }

            await message.Channel.SendMessageAsync($"\u2705 System message sent to {sent} thread(s).");
            return;
        }

        // !dm give-spell @player — give the default Heal spell to a player
        if (cmd.StartsWith("give-spell ", StringComparison.OrdinalIgnoreCase))
        {
            var targetId = ExtractMentionId(cmd[11..].Trim());
            if (targetId is null) { await message.Channel.SendMessageAsync("Usage: `!dm give-spell @player`"); return; }
            var player = await _stateManager.GetPlayerByDiscordIdAsync(targetId);
            if (player is null) { await message.Channel.SendMessageAsync("Player not found."); return; }

            if (player.Spellbook.Any(s => s.Name.Equals("Heal", StringComparison.OrdinalIgnoreCase)))
            {
                await message.Channel.SendMessageAsync($"{player.Name} already knows Heal.");
                return;
            }

            player.Spellbook.Add(new Core.Models.LearnedSpell
            {
                Name = "Heal",
                Description = "Channel restorative magic to mend your wounds.",
                DamageDice = "1d4+2",
                DamageStat = "wis",
                Category = Core.Models.SpellCategory.Healing,
                MpCost = 2,
                BasePower = 1,
                LearnedAtLevel = 1,
                TargetType = "self"
            });
            await _stateManager.SavePlayerAsync(player);
            await message.Channel.SendMessageAsync($"\u2728 Granted Heal spell to {player.Name}.");
            return;
        }

        // !dm give-spell-all — give default Heal to ALL players
        if (cmd.Equals("give-spell-all", StringComparison.OrdinalIgnoreCase))
        {
            var players = await _stateManager.GetAllPlayersAsync();
            int count = 0;
            foreach (var p in players)
            {
                if (p.Spellbook.Any(s => s.Name.Equals("Heal", StringComparison.OrdinalIgnoreCase)))
                    continue;
                p.Spellbook.Add(new Core.Models.LearnedSpell
                {
                    Name = "Heal",
                    Description = "Channel restorative magic to mend your wounds.",
                    DamageDice = "1d4+2",
                    DamageStat = "wis",
                    Category = Core.Models.SpellCategory.Healing,
                    MpCost = 2,
                    BasePower = 1,
                    LearnedAtLevel = 1,
                    TargetType = "self"
                });
                await _stateManager.SavePlayerAsync(p);
                count++;
            }
            await message.Channel.SendMessageAsync($"\u2728 Granted Heal spell to {count} player(s).");
            return;
        }

        await message.Channel.SendMessageAsync("Unknown DM command. Available: `status`, `heal`, `teleport`, `grant`, `say`, `kill`, `spawn`, `reset-world`, `announce`, `msg`, `msg-all`, `give-spell`, `give-spell-all`");
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

    private string BuildCharacterStatsText(PlayerCharacter player)
    {
        // Each line: ║ (content padded to 36 chars) ║
        const int W = 38;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine($"╔{new string('═', W)}╗");

        var nameStr = player.Name ?? "Unknown";
        sb.AppendLine($"║  {nameStr,-36}║");

        var raceClassGender = string.IsNullOrWhiteSpace(player.Gender)
            ? $"{player.Race} {player.Class}"
            : $"{player.Gender} {player.Race} {player.Class}";
        var levelStr = $"Lv.{player.Level}";
        sb.AppendLine($"║  {raceClassGender,-25} {levelStr,10}║");

        sb.AppendLine($"╠{new string('═', W)}╣");

        var hpStr = $"HP: {player.Hp}/{player.MaxHp}";
        var mpStr = $"MP: {player.Mp}/{player.MaxMp}";
        sb.AppendLine($"║  {hpStr,-18}{mpStr,-18}║");

        var goldStr = $"Gold: {player.Gold}";
        var xpStr = $"XP: {player.Xp}";
        sb.AppendLine($"║  {goldStr,-18}{xpStr,-18}║");

        sb.AppendLine($"╠{new string('═', W)}╣");

        var stats = player.GetAttributeStats().ToList();
        var bl = _rules.EffectiveBaseline;
        bool showMod = _rules.StatModifierBaseline.HasValue;
        for (int i = 0; i < stats.Count; i += 2)
        {
            var s1 = stats[i];
            var left = showMod
                ? $"{s1.Name}: {s1.Value,2} ({PlayerCharacter.GetStatModifier(s1.Value, bl):+0;-0})"
                : $"{s1.Name}: {s1.Value,2}";
            string right = "";
            if (i + 1 < stats.Count)
            {
                var s2 = stats[i + 1];
                right = showMod
                    ? $"{s2.Name}: {s2.Value,2} ({PlayerCharacter.GetStatModifier(s2.Value, bl):+0;-0})"
                    : $"{s2.Name}: {s2.Value,2}";
            }
            sb.AppendLine($"║  {left,-18}{right,-18}║");
        }

        sb.AppendLine($"╠{new string('═', W)}╣");
        var wpn = player.Equipment.Weapon;
        var arm = player.Equipment.Armor;
        var shd = player.Equipment.Shield;
        sb.AppendLine($"║  Weapon: {(wpn?.Name ?? "none"),-28}║");
        sb.AppendLine($"║  Armor:  {(arm?.Name ?? "none"),-28}║");
        sb.AppendLine($"║  Shield: {(shd?.Name ?? "none"),-28}║");

        // Show personal/backpack items if any
        if (player.Inventory.Count > 0)
        {
            sb.AppendLine($"╠{new string('═', W)}╣");
            foreach (var item in player.Inventory.Take(5))
            {
                var iname = item.Name.Length > 28 ? item.Name[..28] : item.Name;
                var qty = item.Quantity > 1 ? $" x{item.Quantity}" : "";
                sb.AppendLine($"║  🎒 {(iname + qty),-33}║");
            }
            if (player.Inventory.Count > 5)
                sb.AppendLine($"║  ... and {player.Inventory.Count - 5} more{new string(' ', 23)}║");
        }

        sb.AppendLine($"╚{new string('═', W)}╝");
        sb.Append("```");
        return sb.ToString();
    }

    private static string BuildInventoryText(PlayerCharacter player)
    {
        const int W = 34;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```");
        sb.AppendLine($"\u250C\u2500 INVENTORY {new string('\u2500', W - 11)}\u2510");

        // Equipped items
        var equipped = player.Equipment.AllEquipped().ToList();
        if (equipped.Count > 0)
        {
            foreach (var item in equipped)
            {
                var tag = item.Type switch
                {
                    ItemType.Weapon => "wpn",
                    ItemType.Armor => "arm",
                    ItemType.Shield => "shd",
                    ItemType.Helmet => "hlm",
                    ItemType.Cloak => "clk",
                    ItemType.Boots => "bts",
                    ItemType.Gloves => "glv",
                    ItemType.Ring => "rng",
                    ItemType.Amulet => "aml",
                    _ => "   "
                };
                var name = item.Name.Length > 20 ? item.Name[..20] : item.Name;
                sb.AppendLine($"\u2502 [E] {name,-20} ({tag}) \u2502");
            }
        }

        // Backpack items
        if (player.Inventory.Count > 0)
        {
            foreach (var item in player.Inventory)
            {
                var name = item.Name.Length > 20 ? item.Name[..20] : item.Name;
                var qty = item.Quantity > 1 ? $"x{item.Quantity}" : "  ";
                sb.AppendLine($"\u2502     {name,-20}  {qty,3} \u2502");
            }
        }
        else if (equipped.Count == 0)
        {
            sb.AppendLine($"\u2502     (empty){new string(' ', W - 12)}\u2502");
        }

        sb.AppendLine($"\u2502{new string(' ', W)}\u2502");
        sb.AppendLine($"\u2502 Gold: {player.Gold,-27} \u2502");
        sb.AppendLine($"\u2514{new string('\u2500', W)}\u2518");
        sb.Append("```");
        return sb.ToString();
    }

    private static EmbedBuilder BuildHelpEmbed()
    {
        return new EmbedBuilder()
            .WithTitle("Grand Adventure Engine — Commands")
            .WithColor(Color.Teal)
            .AddField("Movement", "`go <direction>` / `north` `south` `east` `west`")
            .AddField("Observation", "`look` / `look at <target>`")
            .AddField("Combat", "`attack <target>`")
            .AddField("Magic", "`cast <spell>` / `cast <spell> at <target>` — registered spells or improvise!")
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
        await thread.SendMessageAsync($"**Your character has been created!** Type `look` to see your surroundings.\n{BuildCharacterStatsText(player)}");
    }

    // ==================== Helpers ====================

    private static string FormatStatusBar(PlayerCharacter player) =>
        $"❤️ {player.Hp}/{player.MaxHp}  ✨ {player.Mp}/{player.MaxMp}  💰 {player.Gold}g  ⭐ Lv.{player.Level} ({player.Xp} XP)";

    private static string FormatOutcomeBadge(DiceRoll roll) => roll.Outcome switch
    {
        RollOutcome.CriticalHit => " 💥 **CRITICAL HIT!**",
        RollOutcome.Hit => " ✅ **Hit!**",
        RollOutcome.GlancingHit => " 🔶 **Glancing Blow**",
        RollOutcome.Miss => " ❌ **Miss**",
        RollOutcome.CriticalMiss => " 💀 **FUMBLE!**",
        _ => ""
    };

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

    private string FormatMod(int stat)
    {
        var mod = (stat - _rules.EffectiveBaseline) / 2;
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

    // ==================== IDiscordNotifier ====================

    async Task IDiscordNotifier.PostToPlayerThreadAsync(string playerId, string message, CancellationToken ct)
    {
        try
        {
            var player = await _stateManager.GetPlayerAsync(playerId, ct);
            if (player?.ThreadId is null) return;

            foreach (var guild in _client.Guilds)
            {
                if (guild.GetChannel(player.ThreadId.Value) is SocketThreadChannel thread)
                {
                    await thread.SendMessageAsync(message);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post to player thread for {PlayerId}", playerId);
        }
    }

    async Task IDiscordNotifier.PostToAdminChannelAsync(string message, CancellationToken ct)
    {
        await PostToAdminChannelAsync(message);
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

    // ==================== Creation Session Persistence ====================

    /// <summary>
    /// Persist all active creation sessions to disk so they survive container restarts.
    /// Called whenever a session is added, updated, or removed.
    /// </summary>
    private void PersistCreationSessions()
    {
        if (string.IsNullOrEmpty(_sessionsPath)) return;
        try
        {
            var data = _creationSessions.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => new PersistedCreationSession
                {
                    DiscordId = kvp.Value.DiscordId,
                    HasSheet = kvp.Value.HasSheet,
                    LastAiResponse = kvp.Value.LastAiResponse,
                    AwaitingRestartConfirmation = kvp.Value.AwaitingRestartConfirmation
                });
            File.WriteAllText(_sessionsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogDebug("Persisted {Count} creation session(s) to disk", data.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist creation sessions");
        }
    }

    /// <summary>
    /// Restore creation sessions from disk after a container restart.
    /// </summary>
    private void RestoreCreationSessions()
    {
        if (string.IsNullOrEmpty(_sessionsPath) || !File.Exists(_sessionsPath)) return;
        try
        {
            var json = File.ReadAllText(_sessionsPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, PersistedCreationSession>>(json);
            if (data is null || data.Count == 0) return;

            foreach (var (key, session) in data)
            {
                if (!ulong.TryParse(key, out var userId)) continue;
                _creationSessions[userId] = new AiCreationSession(session.DiscordId)
                {
                    HasSheet = session.HasSheet,
                    LastAiResponse = session.LastAiResponse,
                    AwaitingRestartConfirmation = session.AwaitingRestartConfirmation
                };
            }
            _logger.LogInformation("Restored {Count} pending creation session(s) from disk", _creationSessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore creation sessions from disk");
        }
    }

    /// <summary>
    /// Clean up the persisted sessions file (call after all sessions are finalized).
    /// </summary>
    private void CleanupPersistedSessions()
    {
        if (string.IsNullOrEmpty(_sessionsPath)) return;
        try
        {
            if (_creationSessions.Count == 0 && File.Exists(_sessionsPath))
                File.Delete(_sessionsPath);
            else
                PersistCreationSessions();
        }
        catch { /* best effort */ }
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

/// <summary>Serializable subset of AiCreationSession for disk persistence.</summary>
internal class PersistedCreationSession
{
    public string DiscordId { get; set; } = "";
    public bool HasSheet { get; set; }
    public CharacterCreationAiResponse? LastAiResponse { get; set; }
    public bool AwaitingRestartConfirmation { get; set; }
}