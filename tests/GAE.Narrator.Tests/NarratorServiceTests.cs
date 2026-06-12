using System.Net;
using System.Text.Json;
using System.Net.Http.Headers;
using GAE.Core.Interfaces;
using GAE.Core.Models;
using GAE.Narrator;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace GAE.Narrator.Tests;

public class NarratorServiceTests
{
    [Fact]
    public async Task NarrateActionAsync_WhenHttpFailsOnNonLookAction_ReturnsFallback()
    {
        // Arrange: create a handler that always throws
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var context = new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room { Id = "test", Name = "Test Room", Description = "A test room." },
            Action = new GameAction { PlayerId = "p1", RawInput = "attack goblin", Type = ActionType.Attack },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You strike the goblin." }
        };

        // Act
        var result = await narrator.NarrateActionAsync(context);

        // Assert: should get contextual fallback narration (mechanical summary), not throw
        Assert.NotNull(result);
        Assert.Contains("strike", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("clears his throat", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateRoomAsync_WhenHttpFails_ReturnsFallbackRoom()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var sourceRoom = new Room { Id = "start", Name = "Start", EnvironmentTags = ["forest"] };

        var result = await narrator.GenerateRoomAsync("new_room", "north", sourceRoom);

        Assert.Equal("new_room", result.Id);
        Assert.NotNull(result.Name);
        Assert.Contains("south", result.Exits.Keys); // should have exit back
    }

    [Fact]
    public async Task GenerateBackstoryAsync_WhenHttpFails_ReturnsFallback()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var concept = new CharacterConcept { Name = "Test", Race = "Elf", Class = "Mage" };

        var result = await narrator.GenerateBackstoryAsync(concept);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_WhenHttpFailsOnConsequentialAction_ReturnsInWorldFailureFallback()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test Hero", Race = "Human", Class = "Warrior", Level = 1, Hp = 10, MaxHp = 10, Mp = 5, MaxMp = 5 },
            new Room { Id = "lab", Name = "QA Lab", Description = "A repeatable manual test fixture room." },
            "pick up the sword and stab the guard",
            []);

        Assert.False(response.Success);
        Assert.DoesNotContain("nothing dramatic happens", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrator", response.Narration, StringComparison.OrdinalIgnoreCase);
        // Free-form fallback should give a brief in-world failure without parroting input
        Assert.Contains("try", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_WhenHttpFailsOnHarmlessAction_ReturnsPlayableLocalFallback()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            new Room { Id = "gate", Name = "Ironhold Gate", Description = "A rusted war gate with iron plates.", Npcs = [new Npc { Id = "guard", Name = "Gate Warden" }] },
            "I want to shine the rusted gate",
            []);

        Assert.True(response.Success);
        Assert.Empty(response.StatChanges);
        Assert.Empty(response.InventoryChanges);
        Assert.Empty(response.EntityChanges);
        Assert.Contains("rusted gate", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("try again", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("narrator", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_WhenModelWrapsJsonInPreamble_StillParsesResponse()
    {
        var handler = new ResponseHttpMessageHandler("""
            {
              "choices": [
                {
                  "message": {
                    "content": "Here is your result:\n{\"narration\":\"Thorin rubs at the gate until a muted shine peers through the rust.\",\"success\":true,\"statChanges\":{},\"inventoryChanges\":[],\"entityChanges\":[],\"combatInitiated\":false,\"roomChanges\":null}"
                  }
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            new Room { Id = "gate", Name = "Ironhold Gate", Description = "A rusted war gate with iron plates." },
            "shine the rusted gate",
            []);

        Assert.True(response.Success);
        Assert.Contains("muted shine", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateActionAsync_ForLook_UsesGroundedRoomNarrationWithoutCallingHttp()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("HTTP should not be called for deterministic look narration."));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room
            {
                Id = "lab",
                Name = "QA Lab",
                Description = "A repeatable manual test fixture room. The walls hum with patient machinery.",
                Exits = new Dictionary<string, string> { ["south"] = "spawn" },
                Npcs = [new Npc { Id = "sentinel", Name = "Sentinel" }],
                Items = [new InventoryItem { Id = "token", Name = "Inspection Token", Quantity = 2 }]
            },
            Action = new GameAction { PlayerId = "p1", RawInput = "look", Type = ActionType.Look },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = string.Empty }
        });

        Assert.Contains("QA Lab", narration);
        Assert.Contains("Sentinel", narration);
        Assert.Contains("Inspection Token", narration);
        Assert.Contains("south", narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mechanical", narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateActionAsync_ForSuccessfulMove_UsesArrivalFallbackWhenHttpFails()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            CurrentRoom = new Room
            {
                Id = "tavern",
                Name = "The Rusty Mug",
                Description = "A smoky tavern with low ceilings.",
                Npcs = [new Npc { Id = "mara", Name = "Mara", Personality = "Gruff barmaid" }],
                Items = [new InventoryItem { Id = "ale", Name = "Flat Ale", Quantity = 1 }],
                Exits = new Dictionary<string, string> { ["south"] = "crossroads", ["up"] = "rooms" }
            },
            Action = new GameAction { PlayerId = "p1", RawInput = "go north", Type = ActionType.Move, Direction = "north" },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You move north to The Rusty Mug." }
        });

        // Arrival fallback should mention the NPC reacting (second person)
        Assert.Contains("Mara", narration);
        Assert.Contains("you", narration, StringComparison.OrdinalIgnoreCase);
        // Should NOT just say the mechanical summary
        Assert.DoesNotContain("You move north", narration);
    }

    [Fact]
    public async Task NarrateActionAsync_ForFailedMove_StillUsesGeneralNarration()
    {
        // Failed moves (no exit) should NOT go through arrival â€” they use the general humor prompt
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            CurrentRoom = new Room
            {
                Id = "spawn",
                Name = "The Crossroads Inn",
                Description = "A weathered inn."
            },
            Action = new GameAction { PlayerId = "p1", RawInput = "go west", Type = ActionType.Move, Direction = "west" },
            MechanicalResult = new ActionResult { Success = false, MechanicalSummary = "There is no exit to the west." }
        });

        // Should use the mechanical summary directly as fallback, not the arrival path
        Assert.Contains("no exit", narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arrives", narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NarrateActionAsync_WithOllamaProvider_SendsContextAndThinkingOptions()
    {
        var handler = new CapturingOllamaHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var narrator = new NarratorService(
            httpClient,
            NullLogger<NarratorService>.Instance,
            model: "diffusiongemma-test",
            provider: "Ollama",
            contextLength: 16_384,
            think: false);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Id = "p1", Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            CurrentRoom = new Room { Id = "forge", Name = "Forge", Description = "A smoky forge." },
            Action = new GameAction { PlayerId = "p1", RawInput = "inspect the anvil", Type = ActionType.Use },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You inspect the anvil." }
        });

        Assert.Contains("anvil", narration, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/api/chat", handler.RequestPath);
        Assert.False(string.IsNullOrWhiteSpace(handler.RequestBody));

        using var json = JsonDocument.Parse(handler.RequestBody!);
        var root = json.RootElement;
        Assert.Equal("diffusiongemma-test", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(16_384, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(512, root.GetProperty("options").GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task NarrateActionAsync_WithOpenAiCompatibleProvider_SendsBearerAndThinkingOptions()
    {
        var handler = new CapturingOpenAiCompatibleHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8000/") };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-unsloth-test");
        var narrator = new NarratorService(
            httpClient,
            NullLogger<NarratorService>.Instance,
            model: "diffusiongemma",
            think: false);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Id = "p1", Name = "Thorin", Race = "Dwarf", Class = "Fighter" },
            CurrentRoom = new Room { Id = "forge", Name = "Forge", Description = "A smoky forge." },
            Action = new GameAction { PlayerId = "p1", RawInput = "inspect the anvil", Type = ActionType.Use },
            MechanicalResult = new ActionResult { Success = true, MechanicalSummary = "You inspect the anvil." }
        });

        Assert.Contains("anvil", narration, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("sk-unsloth-test", handler.AuthorizationParameter);
        Assert.False(string.IsNullOrWhiteSpace(handler.RequestBody));

        using var json = JsonDocument.Parse(handler.RequestBody!);
        var root = json.RootElement;
        Assert.Equal("diffusiongemma", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task NarrateActionAsync_ForMissingLookTarget_AvoidsTemplateFailureText()
    {
        var handler = new FakeHttpMessageHandler(new HttpRequestException("HTTP should not be called for deterministic look narration."));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var narration = await narrator.NarrateActionAsync(new NarratorContext
        {
            Player = new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            CurrentRoom = new Room
            {
                Id = "lab",
                Name = "QA Lab",
                Description = "A repeatable manual test fixture room.",
                Items = [new InventoryItem { Id = "token", Name = "Inspection Token", Quantity = 2 }]
            },
            Action = new GameAction { PlayerId = "p1", RawInput = "look at sentinel", Type = ActionType.Look, Target = "sentinel" },
            MechanicalResult = new ActionResult { Success = false, MechanicalSummary = "Look target 'sentinel' was not found in the current room." }
        });

        Assert.Contains("sentinel", narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing here answers that description", narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithBrokenDialogueQuotes_RepairsAndParses()
    {
        // Simulates the exact broken JSON that arrives after CompletionOrThrowAsync
        // deserializes the outer LM Studio response â€” dialogue quotes are bare/unescaped,
        // and the LLM returns "narrative" instead of "narration".
        var brokenJson = """{"narrative": ""Rumors?" Mara says, leaning closer. "Don't you start with that nonsense, kid," she mutters.", "success": true, "statChanges": {}, "inventoryChanges": [], "entityChanges": [], "combatInitiated": false, "interactionUpdate": {"mode": "conversation", "npcDisposition": "annoyed"}}""";

        var result = NarratorService.TryParseFreeFormResponse(brokenJson, out var response);

        Assert.True(result);
        Assert.Contains("Rumors", response.Narration);
        Assert.Contains("Mara", response.Narration);
        Assert.True(response.Success);
        Assert.NotNull(response.InteractionUpdate);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithGarbledJsonKeys_ExtractsNarrationOnly()
    {
        // Real failure case: LLM garbles "inventoryChanges" key to "lyinventoryChanges"
        // making the JSON syntactically invalid. The narration is still extractable.
        var garbledJson = """
            {
              "narration": "\"Well, if you're looking for trouble, you don't have to look far,\" Mara says, wiping down a stray spill with a quick, practiced motion.",
              "success": true,
              "statChanges": {},
             lyinventoryChanges": [],
              "entityChanges": [],
              "combatInitiated": false,
              "interactionUpdate": { "mode": "conversation", "npcDisposition": "neutral" }
            }
            """;

        var result = NarratorService.TryParseFreeFormResponse(garbledJson, out var response);

        Assert.True(result);
        Assert.Contains("Mara says", response.Narration);
        Assert.Contains("trouble", response.Narration);
        Assert.True(response.Success);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithTruncatedJson_ExtractsNarrationOnly()
    {
        // Simulates a response truncated by context window â€” JSON cuts off mid-field
        var truncatedJson = """
            {
              "narration": "The guard eyes you warily, hand on his sword hilt.",
              "success": true,
              "statChanges": {},
              "inventoryChanges": [],
              "interactionUpda
            """;

        var result = NarratorService.TryParseFreeFormResponse(truncatedJson, out var response);

        Assert.True(result);
        Assert.Contains("guard eyes you warily", response.Narration);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithPlainNarration_SalvagesReply()
    {
        var plainNarration = """
            Mara stops polishing a glass and leans forward, her crimson eyes narrowing slightly.

            "You want to know about the Merchants Guild?" she asks, voice low as if sharing a secret. "They're not what they seem."
            "They control half the trade in this quarter, and they always want a cut."
            """;

        var result = NarratorService.TryParseFreeFormResponse(plainNarration, out var response);

        Assert.True(result);
        Assert.Contains("Merchants Guild", response.Narration);
        Assert.Contains("They control half the trade", response.Narration);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithPlainNarrationAndSchemaTail_TrimsArtifacts()
    {
        var plainNarrationWithTail = """
            Mara Vale: "That poster?" she says, turning around to glance at the faded print. "That was another life."

            She raises her glass slightly. "Sometimes the old fights come knocking anyway."

            SUCCESS: True

            STAT CHANGES: {}

            INTERACTION UPDATE:
            { "mode": "conversation" }
            """;

        var result = NarratorService.TryParseFreeFormResponse(plainNarrationWithTail, out var response);

        Assert.True(result);
        Assert.Contains("That was another life", response.Narration);
        Assert.DoesNotContain("SUCCESS:", response.Narration);
        Assert.DoesNotContain("INTERACTION UPDATE", response.Narration);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithPromptTailAfterNarration_TrimsPromptArtifacts()
    {
        var narrationWithPromptTail = """
            Mara slides a glass across the bar. "You come here looking for trouble or company?" she asks with a crooked smile.

            You're playing a text-based RPG where choices change outcomes and tone determines atmosphere.

            Each turn you provide:
            - Action: the player's move or choice
            """;

        var result = NarratorService.TryParseFreeFormResponse(narrationWithPromptTail, out var response);

        Assert.True(result);
        Assert.Contains("looking for trouble or company", response.Narration);
        Assert.DoesNotContain("You're playing a text-based RPG", response.Narration);
        Assert.DoesNotContain("Each turn you provide", response.Narration);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithGameplayScaffoldingTail_TrimsIt()
    {
        var narrationWithGameplayTail = """
            Mara raises her glass. "To Elarion," she says with a grin that doesn't quite hide her caution.

            Health: 90/100
            Stamina: 75/80
            The player can ask more questions or change topics.
            """;

        var result = NarratorService.TryParseFreeFormResponse(narrationWithGameplayTail, out var response);

        Assert.True(result);
        Assert.Contains("To Elarion", response.Narration);
        Assert.DoesNotContain("Health:", response.Narration);
        Assert.DoesNotContain("Stamina:", response.Narration);
        Assert.DoesNotContain("The player can", response.Narration);
    }

    [Fact]
    public void TryParseFreeFormResponse_WithPromptEcho_DoesNotTreatItAsNarration()
    {
        var promptEcho = """
            You are now voicing Mara Vale in direct conversation with the player.

            CRITICAL RESPONSE RULES:
            1. The narration MUST contain 2-4 full sentences of quoted NPC speech.
            Player says/does: "Tell me about the guild."
            Respond with ONLY valid JSON.
            """;

        var result = NarratorService.TryParseFreeFormResponse(promptEcho, out _);

        Assert.False(result);
    }

    [Fact]
    public async Task ProcessConversationTurnAsync_WhenLlmReturnsDialogueQuotes_ParsesSuccessfully()
    {
        // Simulate the exact broken pattern: after outer JSON deserialization by
        // CompletionOrThrowAsync, the inner JSON has bare unescaped dialogue quotes like:
        //   {"narrative": ""Rumors?" Mara says.", "success": true}
        // We build the outer response so that the content field, when deserialized,
        // produces this broken inner JSON.
        //
        // The broken inner JSON (what TryParseFreeFormResponse receives):
        var brokenInner = """{"narrative": ""Rumors?" Mara says, leaning closer. "Don't you start with that nonsense, kid," she mutters.", "success": true, "statChanges": {}, "inventoryChanges": [], "entityChanges": [], "combatInitiated": false, "interactionUpdate": {"mode": "conversation", "npcDisposition": "annoyed"}}""";
        // Wrap it in a proper LM Studio response â€” JsonSerializer.Serialize escapes quotes
        // so that after outer deserialization we get the broken inner JSON back.
        var outerJson = "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(brokenInner) + "}}]}";
        var handler = new ResponseHttpMessageHandler(outerJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        // Pass a specific model name to skip model resolution (avoids extra HTTP call)
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance, model: "test-model");

        var response = await narrator.ProcessConversationTurnAsync(
            new PlayerCharacter { Name = "Zephyr", Race = "Human", Class = "Rogue", Level = 1 },
            new Room { Id = "tavern", Name = "Tavern", Description = "A smoky tavern." },
            new Npc { Id = "mara", Name = "Mara", Personality = "Gruff barmaid" },
            new InteractionState { Mode = InteractionMode.Conversation, Target = "mara" },
            "flirt with Mara");

        Assert.NotNull(response);
        Assert.Contains("Rumors", response.Narration);
        Assert.DoesNotContain("noncommittal", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessConversationTurnAsync_WhenLlmReturnsNarrativeKey_NormalizesToNarration()
    {
        // LLM returns "narrative" instead of "narration" â€” should still parse
        var handler = new ResponseHttpMessageHandler("""
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"narrative\": \"The barkeep nods slowly.\", \"success\": true, \"statChanges\": {}, \"inventoryChanges\": [], \"entityChanges\": []}"
                  }
                }
              ]
            }
            """);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessConversationTurnAsync(
            new PlayerCharacter { Name = "Zephyr", Race = "Human", Class = "Rogue", Level = 1 },
            new Room { Id = "tavern", Name = "Tavern", Description = "A smoky tavern." },
            new Npc { Id = "barkeep", Name = "Barkeep", Personality = "Friendly" },
            new InteractionState { Mode = InteractionMode.Conversation, Target = "barkeep" },
            "ask about rumors");

        Assert.NotNull(response);
        Assert.Contains("barkeep", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("noncommittal", response.Narration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessFreeFormAsync_ForLowStakesAction_WhenHttpFails_ReturnsFallback()
    {
        // Low-stakes actions now go through the LLM for humorous narration.
        // When LLM is offline, the fallback still handles them gracefully.
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test Hero", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A repeatable manual test fixture room.", Npcs = [new Npc { Id = "sentinel", Name = "Sentinel" }] },
            "pick nose",
            []);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Narration);
    }

    // â”€â”€ Retry logic tests â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task CompletionRetry_TransientFailureThenSuccess_ReturnsNarration()
    {
        // First call fails, second succeeds. retryCount=1 means 2 total attempts.
        var handler = new TransientFailureHandler(
            failCount: 1,
            successBody: """{"choices":[{"message":{"content":"{\"narration\":\"The gate gleams.\",\"success\":true,\"statChanges\":{},\"inventoryChanges\":[],\"entityChanges\":[],\"combatInitiated\":false}"}}]}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance, retryCount: 1, retryDelayMs: 0);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A test room." },
            "polish the gate",
            []);

        Assert.True(response.Success);
        Assert.Contains("gleams", response.Narration, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task CompletionRetry_AllRetriesExhausted_FallsBackGracefully()
    {
        // 3 total attempts (retryCount=2), all fail â†’ CompletionAsync catches and returns fallback
        var handler = new FakeHttpMessageHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance, retryCount: 2, retryDelayMs: 0);

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A test room." },
            "open the chest",
            []);

        // Should get fallback (not throw), meaning all retries were attempted
        Assert.NotNull(response);
        Assert.NotEmpty(response.Narration);
    }

    [Fact]
    public async Task CompletionRetry_ZeroRetries_SingleAttemptOnly()
    {
        var handler = new TransientFailureHandler(
            failCount: 1,
            successBody: """{"choices":[{"message":{"content":"ok"}}]}""");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance, retryCount: 0, retryDelayMs: 0);

        // With retryCount=0, only 1 attempt â€” it fails, falls through to CompletionAsync fallback
        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A test room." },
            "kick the door",
            []);

        Assert.NotNull(response);
        Assert.Equal(1, handler.CallCount); // Only one attempt, no retry
    }

    [Fact]
    public async Task CompletionRetry_CancellationDuringRetry_DoesNotRetryAfterCancellation()
    {
        // With a pre-cancelled token, the first HTTP call throws OperationCanceledException.
        // The retry filter (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        // should NOT catch it â€” it should propagate through CompletionOrThrowAsync.
        // CompletionAsync catches it and returns fallback (which is fine â€” fast exit).
        var handler = new TransientFailureHandler(failCount: 99, successBody: "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234/") };
        var narrator = new NarratorService(httpClient, NullLogger<NarratorService>.Instance, retryCount: 5, retryDelayMs: 50_000);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await narrator.ProcessFreeFormAsync(
            new PlayerCharacter { Name = "Test", Race = "Human", Class = "Warrior" },
            new Room { Id = "lab", Name = "QA Lab", Description = "A test room." },
            "open the door",
            [],
            ct: cts.Token);

        // Should get fallback quickly â€” cancelled token prevents retry delay from completing,
        // so only the first attempt fires before cancellation kicks in
        Assert.NotNull(response);
        Assert.Equal(1, handler.CallCount);
    }

    // â”€â”€ Test helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public FakeHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private class ResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public ResponseHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }

    private class CapturingOllamaHandler : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"message":{"role":"assistant","content":"Thorin studies the anvil; it answers with soot, silence, and one useful dent."},"done":true}
                    """)
            };
        }
    }

    private class CapturingOpenAiCompatibleHandler : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri?.AbsolutePath;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"choices":[{"delta":{"content":"Thorin studies the anvil; it answers with soot, silence, and one useful dent."}}]}

                    data: [DONE]

                    """)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        }
    }

    private class TransientFailureHandler : HttpMessageHandler
    {
        private readonly int _failCount;
        private readonly string _successBody;
        private int _callCount;

        public int CallCount => _callCount;

        public TransientFailureHandler(int failCount, string successBody)
        {
            _failCount = failCount;
            _successBody = successBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Skip model-resolution requests (GET /v1/models) â€” always succeed
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"test-model"}]}""")
                });
            }

            _callCount++;
            if (_callCount <= _failCount)
                throw new HttpRequestException("Transient failure");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_successBody)
            });
        }
    }
}
