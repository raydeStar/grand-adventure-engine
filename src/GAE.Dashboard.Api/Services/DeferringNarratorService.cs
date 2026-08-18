using System.Text.Json;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Core.Registry;
using GAE.Engine.State;

namespace GAE.Dashboard.Api.Services;

/// <summary>
/// Wraps the narrator so a slow model costs the player a wait they can play through instead of a
/// stalled turn.
///
/// Only <see cref="NarrateActionAsync"/> is deferred. That call produces prose for an action the
/// engine has already resolved, so the result is decorative and safe to deliver late. Every other
/// narrator call returns structured data the engine acts on immediately — a conversation turn carries
/// the NPC's disposition and the next interaction mode, and combat carries damage — so deferring
/// those would mean guessing at game state. Those pass straight through.
///
/// The happy path does not waste work. The pending row is written before the wait so nothing is lost
/// to a crash, but the already-running call keeps going and settles that row itself when it lands.
/// The background worker only picks up rows this process failed to finish.
/// </summary>
public class DeferringNarratorService : INarratorService
{
    private readonly INarratorService _inner;
    private readonly INarrationQueue _queue;
    private readonly INarrationDelivery _delivery;
    private readonly ILogger<DeferringNarratorService> _logger;
    private readonly TimeSpan _foregroundBudget;

    /// <summary>Shown while the prose is still being written.</summary>
    private const string PendingNarrationPlaceholder =
        "*The narrator is still composing this moment; it will arrive shortly.*";

    public DeferringNarratorService(
        INarratorService inner,
        INarrationQueue queue,
        INarrationDelivery delivery,
        ILogger<DeferringNarratorService> logger,
        TimeSpan? foregroundBudget = null)
    {
        _inner = inner;
        _queue = queue;
        _delivery = delivery;
        _logger = logger;
        _foregroundBudget = foregroundBudget ?? TimeSpan.FromSeconds(8);
    }

    public async Task<string> NarrateActionAsync(NarratorContext context, CancellationToken ct = default)
    {
        // Deferral needs an action id to update the right story entry later.
        var actionId = context.Action?.Id;
        var playerId = context.Player?.Id;
        if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(playerId) || _foregroundBudget <= TimeSpan.Zero)
            return await _inner.NarrateActionAsync(context, ct);

        // Deliberately not tied to the request's token: the narration must survive the response
        // returning so it can be delivered afterwards.
        var narrationTask = _inner.NarrateActionAsync(context, CancellationToken.None);

        var finishedInTime = await Task.WhenAny(narrationTask, Task.Delay(_foregroundBudget, ct)) == narrationTask;
        if (finishedInTime)
        {
            // Surfaces a genuine narrator failure to the caller exactly as before.
            return await narrationTask;
        }

        var pending = await EnqueueAsync(context, actionId!, playerId!, ct);
        if (pending is null)
        {
            // The queue is unavailable, so waiting is better than losing the prose entirely.
            return await narrationTask;
        }

        _logger.LogInformation(
            "Narration for action {ActionId} exceeded the {Budget:0.#}s budget; returning mechanics now and delivering prose when it lands",
            actionId, _foregroundBudget.TotalSeconds);

        TrackToCompletion(narrationTask, pending);
        return PendingNarrationPlaceholder;
    }

    /// <summary>
    /// Records the debt before returning, so a crash between now and the narrator finishing leaves
    /// work the background service can recover rather than a permanently missing paragraph.
    /// </summary>
    private async Task<PendingNarration?> EnqueueAsync(NarratorContext context, string actionId, string playerId, CancellationToken ct)
    {
        try
        {
            var pending = new PendingNarration
            {
                ActionId = actionId,
                PlayerId = playerId,
                WorldId = string.IsNullOrWhiteSpace(context.Player?.ActiveWorldId)
                    ? WorldDefaults.DefaultWorldId
                    : context.Player!.ActiveWorldId,
                RoomId = context.CurrentRoom?.Id,
                Sequence = await _queue.NextSequenceAsync(playerId, ct),
                Operation = "action",
                ContextJson = JsonSerializer.Serialize(context, NarrationContextJson.Options),
                PlaceholderNarration = PendingNarrationPlaceholder,
                // Claimed by this process: the in-flight call will settle it, and the worker only
                // takes it over if that never happens.
                Status = PendingNarrationStatus.InFlight
            };

            return await _queue.EnqueueAsync(pending, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enqueue deferred narration for action {ActionId}", actionId);
            return null;
        }
    }

    /// <summary>
    /// Follows the already-running narrator call and settles the queue row when it finishes, so the
    /// work is never done twice in the normal case.
    /// </summary>
    private void TrackToCompletion(Task<string> narrationTask, PendingNarration pending)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var narration = await narrationTask;
                if (string.IsNullOrWhiteSpace(narration))
                {
                    await _queue.FailAsync(pending, "narrator returned nothing", CancellationToken.None);
                    return;
                }

                await _queue.CompleteAsync(pending, narration, CancellationToken.None);
                await _delivery.DeliverAsync(pending.ActionId, pending.PlayerId, pending.RoomId, narration, CancellationToken.None);

                _logger.LogInformation(
                    "Deferred narration for action {ActionId} delivered after {Elapsed:0.0}s",
                    pending.ActionId, (DateTimeOffset.UtcNow - pending.CreatedAt).TotalSeconds);
            }
            catch (Exception ex)
            {
                // Returning it to Pending lets the background worker retry with backoff.
                _logger.LogWarning(ex, "Deferred narration for action {ActionId} failed; leaving it for the worker", pending.ActionId);
                try
                {
                    await _queue.FailAsync(pending, ex.Message, CancellationToken.None);
                }
                catch (Exception queueEx)
                {
                    _logger.LogError(queueEx, "Could not record the narration failure for action {ActionId}", pending.ActionId);
                }
            }
        });
    }

    // ── Everything below returns data the engine acts on immediately, so it is never deferred. ──

    public string GetActiveModel() => _inner.GetActiveModel();

    public void SetActiveModel(string model) => _inner.SetActiveModel(model);

    public Task<Room> GenerateRoomAsync(string roomId, string direction, Room sourceRoom, CancellationToken ct = default)
        => _inner.GenerateRoomAsync(roomId, direction, sourceRoom, ct);

    public Task<Npc> GenerateNpcAsync(Room room, string? faction = null, CancellationToken ct = default)
        => _inner.GenerateNpcAsync(room, faction, ct);

    public Task<string> GenerateAsciiArtAsync(string subject, CancellationToken ct = default)
        => _inner.GenerateAsciiArtAsync(subject, ct);

    public Task<string> GenerateBackstoryAsync(CharacterConcept concept, CancellationToken ct = default)
        => _inner.GenerateBackstoryAsync(concept, ct);

    public Task<string?> ParseIntentAsync(string rawInput, CancellationToken ct = default)
        => _inner.ParseIntentAsync(rawInput, ct);

    public Task<Room> GenerateBlindAdventureRoomAsync(string roomId, string direction, Room sourceRoom, StorylineContext storyline, IReadOnlyList<string> visitedRoomSummaries, string? nextPlotBeat, int roomsRemaining, CancellationToken ct = default)
        => _inner.GenerateBlindAdventureRoomAsync(roomId, direction, sourceRoom, storyline, visitedRoomSummaries, nextPlotBeat, roomsRemaining, ct);

    public Task<(string Narration, string Summary)> NarrateBlindAdventureConclusionAsync(StorylineContext storyline, IReadOnlyList<string> visitedRooms, IReadOnlyList<string> keyEvents, CancellationToken ct = default)
        => _inner.NarrateBlindAdventureConclusionAsync(storyline, visitedRooms, keyEvents, ct);

    public Task<Room> GenerateDungeonEntranceAsync(string dungeonId, int playerLevel, Room sourceRoom, CancellationToken ct = default)
        => _inner.GenerateDungeonEntranceAsync(dungeonId, playerLevel, sourceRoom, ct);

    public Task<SpellVetResponse?> VetSpellAsync(PlayerCharacter player, string spellDescription, Room room, CancellationToken ct = default)
        => _inner.VetSpellAsync(player, spellDescription, room, ct);

    public Task<ImprovisedSpellResult> EvaluateImprovisedSpellAsync(PlayerCharacter player, Room room, string spellName, string? target, int powerCap, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
        => _inner.EvaluateImprovisedSpellAsync(player, room, spellName, target, powerCap, recentStory, ct);

    public Task<string> GenerateContentAsync(string contentType, string description, string? existingJson, CancellationToken ct = default)
        => _inner.GenerateContentAsync(contentType, description, existingJson, ct);

    public Task<CharacterCreationAiResponse?> CreateCharacterFromDescriptionAsync(string playerDescription, string? previousSheet, CancellationToken ct = default)
        => _inner.CreateCharacterFromDescriptionAsync(playerDescription, previousSheet, ct);

    public Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
        => _inner.ListAvailableModelsAsync(ct);

    public Task<StatTranslationResponse?> TranslateStatsAsync(StatTranslationRequest request, CancellationToken ct = default)
        => _inner.TranslateStatsAsync(request, ct);

    public Task<string?> NarrateRealmTransitionAsync(string playerName, string sourceWorldName, string destinationWorldName, string? portalHint, CancellationToken ct = default)
        => _inner.NarrateRealmTransitionAsync(playerName, sourceWorldName, destinationWorldName, portalHint, ct);

    public Task<string> ProvideGuidanceAsync(PlayerCharacter player, Room room, string? question, CancellationToken ct = default)
        => _inner.ProvideGuidanceAsync(player, room, question, ct);

    public Task<string> GenerateHeroIntroAsync(PlayerCharacter player, Room room, CancellationToken ct = default)
        => _inner.GenerateHeroIntroAsync(player, room, ct);

    public Task<CyoaChoiceNode> GenerateCyoaNodeAsync(PlayerCharacter player, string? choiceText, IReadOnlyList<CyoaChoiceRecord> recentHistory, CancellationToken ct = default)
        => _inner.GenerateCyoaNodeAsync(player, choiceText, recentHistory, ct);

    public Task<string> GenerateCyoaDeathNarrationAsync(PlayerCharacter player, string deathSceneNarration, bool hasCheckpoint, CancellationToken ct = default)
        => _inner.GenerateCyoaDeathNarrationAsync(player, deathSceneNarration, hasCheckpoint, ct);

    public Task<string> GenerateCyoaEndingNarrationAsync(PlayerCharacter player, string endingType, string finalSceneNarration, string adventureSummary, CancellationToken ct = default)
        => _inner.GenerateCyoaEndingNarrationAsync(player, endingType, finalSceneNarration, adventureSummary, ct);

    public Task<FreeFormResponse> ProcessFreeFormAsync(PlayerCharacter player, Room room, string rawInput, IReadOnlyList<StoryEntry> recentStory, CancellationToken ct = default)
        => _inner.ProcessFreeFormAsync(player, room, rawInput, recentStory, ct);

    public Task<FreeFormResponse> ProcessConversationTurnAsync(PlayerCharacter player, Room room, Npc npc, InteractionState interaction, string rawInput, CancellationToken ct = default)
        => _inner.ProcessConversationTurnAsync(player, room, npc, interaction, rawInput, ct);

    public Task<FreeFormResponse> ProcessCombatTurnAsync(PlayerCharacter player, Room room, Npc enemy, InteractionState interaction, string rawInput, CancellationToken ct = default)
        => _inner.ProcessCombatTurnAsync(player, room, enemy, interaction, rawInput, ct);
}
