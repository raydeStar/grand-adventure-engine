using GAE.Core.Models;
using GAE.Narrator;
using Xunit.Abstractions;

namespace GAE.Engine.Tests;

/// <summary>
/// The replies players see when the narrator is unreachable. Every reported bad transcript came from
/// this path, so it gets its own tests: an offline reply must name what the player actually did, use
/// this NPC's authored traits, and honour outstanding debts instead of deflecting.
/// </summary>
public class OfflineNpcVoiceTests
{
    private readonly ITestOutputHelper _output;

    public OfflineNpcVoiceTests(ITestOutputHelper output) => _output = output;

    private static Npc Mara() => new()
    {
        Id = "innkeeper_mara",
        Name = "Mara the Barkeep",
        Faction = "merchants_guild",
        Personality = "Warm and flirtatious, but sharp as a knife. Knows every secret in Thornwall. "
                      + "Has a soft spot for adventurers with good stories. Lonely since her husband left.",
        KnowledgeScopes = ["rumors", "town", "shadow_market"]
    };

    private static Npc Pete() => new()
    {
        Id = "drunk_pete",
        Name = "Stumbling Pete",
        Faction = "neutral",
        Personality = "Town drunk. Cowardly, gullible, and desperate for coin. "
                      + "Claims he saw the dungeon boss once. Nobody believes him. Surprisingly honest when sober, which is never.",
        KnowledgeScopes = ["rumors", "dread_hollow"]
    };

    /// <summary>Prints what a player would actually read, for eyeballing tone.</summary>
    [Fact]
    public void Preview_TheReportedInputs()
    {
        foreach (var (npc, input) in new[]
                 {
                     (Mara(), "hello there"),
                     (Mara(), "I put a coin on the table \"Hear any good rumors lately?\""),
                     (Pete(), "say hello to stumbling pete"),
                     (Pete(), "\"Aight\" i say, plopping a coin on the counter"),
                     (Pete(), "uh... tell me something nobody else will"),
                 })
        {
            var reply = NpcVoice.TryBuildGroundedReply(npc, "Mister Meow Meow", input);
            _output.WriteLine($"IN : {input}");
            _output.WriteLine($"OUT: {reply}");
            _output.WriteLine("");
        }
    }

    // ── It must be specific, not evasive ──

    [Theory]
    [InlineData("hello there")]
    [InlineData("I put a coin on the table")]
    [InlineData("hear any good rumors lately?")]
    [InlineData("tell me something nobody else will")]
    public void EveryReply_DrawsOnTheAuthoredPersonality(string input)
    {
        var reply = NpcVoice.TryBuildGroundedReply(Pete(), "Meow Meow", input);

        Assert.NotNull(reply);

        // Some fragment of the NPC's own character sheet has to survive into the reply. Derived from
        // the personality itself so the test cannot drift from the authored content.
        var fragments = Pete().Personality
            .Split(['.', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length >= 12)
            .ToList();
        Assert.Contains(fragments, fragment => reply!.Contains(fragment.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The reported failure: money changes hands and the NPC says "spend it on something useful",
    /// as if nothing happened.
    /// </summary>
    [Theory]
    [InlineData("I put a coin on the table")]
    [InlineData("\"Aight\" i say, plopping a coin on the counter")]
    [InlineData("buy him a drink")]
    public void BeingPaid_IsAcknowledgedRatherThanDeflected(string input)
    {
        var reply = NpcVoice.TryBuildGroundedReply(Pete(), "Meow Meow", input)!;

        Assert.Contains(new[] { "coin", "buys you", "money" }, token => reply.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Spend it on something useful", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AskingForRumors_NamesSomethingTheNpcActuallyKnowsAbout()
    {
        var reply = NpcVoice.TryBuildGroundedReply(Mara(), "Meow Meow", "hear any good rumors lately?")!;

        // Scopes are the NPC's authored knowledge; a rumour request should reference one.
        Assert.Contains(new[] { "rumors", "town", "shadow market" }, s => reply.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An outstanding debt must be settled, not re-offered — the Pete failure.</summary>
    [Fact]
    public void AnOutstandingDebt_IsHonouredWhenTheyAsk()
    {
        var pete = Pete();
        pete.DispositionState.Memory.PromiseSomething("tell them something nobody else will");

        var reply = NpcVoice.TryBuildGroundedReply(pete, "Meow Meow", "well? tell me something nobody else will")!;

        Assert.Contains(new[] { "owe", "promised", "putting it off" }, t => reply.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AReturningPlayer_IsNotGreetedAsAStranger()
    {
        var mara = Mara();
        mara.DispositionState.Memory.EncounterCount = 4;
        mara.DispositionState.Memory.Remember(new NpcMemoryEntry { Summary = "They flirted with me.", Weight = 60 });

        var reply = NpcVoice.TryBuildGroundedReply(mara, "Meow Meow", "hello there")!;

        Assert.Contains(new[] { "Back again", "already taken your measure", "recognises you" },
            t => reply.Contains(t, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("New face", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStranger_IsTreatedAsOne()
    {
        var reply = NpcVoice.TryBuildGroundedReply(Mara(), "Meow Meow", "hello there")!;

        Assert.DoesNotContain("Back again", reply, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two different characters must not answer the same question the same way.</summary>
    [Fact]
    public void DifferentNpcs_DoNotSoundIdentical()
    {
        var maraReply = NpcVoice.TryBuildGroundedReply(Mara(), "Meow Meow", "hear any good rumors lately?")!;
        var peteReply = NpcVoice.TryBuildGroundedReply(Pete(), "Meow Meow", "hear any good rumors lately?")!;

        Assert.NotEqual(maraReply, peteReply);
    }

    [Fact]
    public void AnNpcWithNoAuthoredPersonality_YieldsToTheGenericPool()
    {
        var blank = new Npc { Id = "nobody", Name = "Hooded Figure", Personality = "" };

        Assert.Null(NpcVoice.TryBuildGroundedReply(blank, "Meow Meow", "hello there"));
    }

    [Fact]
    public void RepliesAreStableForTheSameInput_AndVaryAcrossInputs()
    {
        var first = NpcVoice.TryBuildGroundedReply(Pete(), "Meow Meow", "hello there");
        var again = NpcVoice.TryBuildGroundedReply(Pete(), "Meow Meow", "hello there");
        var other = NpcVoice.TryBuildGroundedReply(Pete(), "Meow Meow", "what do you know about the boss?");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }
}
