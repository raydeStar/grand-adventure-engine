using GAE.Core.Models;

namespace GAE.Narrator;

/// <summary>
/// Builds NPC replies from the character's own authored traits and their memory of this player,
/// for use when the narrator model is unreachable.
///
/// This exists because the offline replies were the single worst thing players saw. There were three
/// separate fallback producers — the conversation pool, the ongoing-conversation pool, and the
/// free-form social pool — each with its own hand-written lines that substituted a name into generic
/// aphorisms. A town drunk, a flirtatious barkeep and a guard captain all deflected identically:
/// "turn that into a sentence with a purpose", "spend it on something useful". Fixing one pool left
/// the others untouched, so all three now consult this first.
///
/// The aim is not to imitate the model. It is to be *specific*: name the thing the player did, use
/// this NPC's actual traits, honour outstanding debts, and acknowledge shared history. A concrete
/// reply from a shallow generator reads far better than an eloquent evasion.
/// </summary>
internal static class NpcVoice
{
    /// <summary>
    /// Produces a grounded reply, or null when this NPC has nothing specific to work with and the
    /// caller should use its own generic pool.
    /// </summary>
    public static string? TryBuildGroundedReply(Npc npc, string playerName, string? rawInput)
    {
        var memory = npc.DispositionState.Memory;
        var input = (rawInput ?? string.Empty).Trim();
        var seed = Math.Abs(HashCode.Combine(npc.Id, input));

        // 1. An unpaid debt outranks everything. Deflecting again is the failure that started this.
        var traits = ExtractTraits(npc.Personality);
        if (traits.Count == 0)
            return null;

        var trait = traits[seed % traits.Count];

        var owed = memory.OpenPromises.FirstOrDefault();
        if (owed is not null && LooksLikeAskingForSomething(input))
            return BuildDebtReply(npc, playerName, trait, seed);
        var knowsThem = memory.EncounterCount > 0;

        // 2. Being handed money is a concrete act. Acknowledge the coin, then trade on it.
        if (MentionsPayment(input))
            return BuildPaidReply(npc, playerName, trait, knowsThem, seed);

        // 3. A direct request for information should produce information, or a stated price for it.
        if (AsksForInformation(input))
            return BuildInformationReply(npc, playerName, trait, seed);

        // 4. A greeting from someone they know should not read like a first meeting.
        if (IsGreeting(input))
            return BuildGreetingReply(npc, playerName, trait, knowsThem, memory, seed);

        return BuildGeneralReply(npc, playerName, trait, knowsThem, seed);
    }

    private static string BuildDebtReply(Npc npc, string playerName, string? trait, int seed)
    {
        var aside = Aside(npc, trait);
        return (seed % 3) switch
        {
            0 => $"{npc.Name}{aside} exhales, caught out. \"Right. I owe you, and I have been putting it off. Ask me plainly and I will not wriggle out of it twice, {playerName}.\"",
            1 => $"{npc.Name}{aside} taps the counter once, conceding the point. \"You are owed something from me, {playerName}. Say which part you want first.\"",
            _ => $"{npc.Name}{aside} has the grace to look faintly ashamed. \"I know what I promised, {playerName}. Push me on it and I will make good.\""
        };
    }

    /// <summary>
    /// Answers a payment as an event that happened. Being handed coin previously produced "spend it
    /// on something useful", which ignores that the player just did exactly that.
    /// </summary>
    private static string BuildPaidReply(Npc npc, string playerName, string? trait, bool knowsThem, int seed)
    {
        var aside = Aside(npc, trait);
        var greedy = MentionsAny(npc.Personality, "coin", "gold", "greed", "desperate", "poor", "drink", "drunk");

        if (greedy)
        {
            return (seed % 2) == 0
                ? $"{npc.Name}{aside} has the coin off the wood before it stops rattling. \"Now we are speaking a language I understand, {playerName}. Ask, and I will tell it straight.\""
                : $"{npc.Name}{aside} palms the coin with practised speed. \"That buys you honesty, which is cheaper than you would think. Go on then, {playerName} — ask.\"";
        }

        var familiar = knowsThem ? $"You did not need to pay me, not you, {playerName}." : $"You did not need to pay me, {playerName}.";
        return $"{npc.Name}{aside} looks at the coin, then leaves it where it lies. \"{familiar} Ask your question; the money can stay on the table.\"";
    }

    private static string BuildInformationReply(Npc npc, string playerName, string? trait, int seed)
    {
        var aside = Aside(npc, trait);
        // Skip catch-all scopes: "talk about rumors" says nothing, whereas "talk about dread hollow"
        // gives the player somewhere to push.
        string[] tooGeneric = ["rumors", "rumours", "local", "general", "gossip"];
        var subject = npc.KnowledgeScopes
            .FirstOrDefault(sc => !string.IsNullOrWhiteSpace(sc) && !tooGeneric.Contains(sc, StringComparer.OrdinalIgnoreCase))
            ?.Replace('_', ' ');
        var about = string.IsNullOrWhiteSpace(subject) ? "this town" : subject;

        return (seed % 2) == 0
            ? $"{npc.Name}{aside} weighs how much to give away. \"What I hear about {about} is worth having, {playerName} — but ask me something narrower than that.\""
            : $"{npc.Name}{aside} lowers their voice by a half step. \"There is talk about {about}, {playerName}. Name the part you care about and I will tell you what I actually know.\"";
    }

    private static string BuildGreetingReply(Npc npc, string playerName, string? trait, bool knowsThem, NpcMemoryLedger memory, int seed)
    {
        var aside = Aside(npc, trait);

        if (knowsThem)
        {
            var core = memory.Entries.FirstOrDefault(e => e.IsCore);
            var callback = core is not null ? " We have history, you and I." : "";
            return $"{npc.Name}{aside} recognises you before you finish speaking. \"Back again, {playerName}.{callback} What is it this time?\"";
        }

        return (seed % 2) == 0
            ? $"{npc.Name}{aside} sizes you up without hurrying about it. \"New face. Say what you want, {playerName}, and I will tell you whether you can have it.\""
            : $"{npc.Name}{aside} gives you a nod that costs them nothing. \"You have my attention for as long as you keep it interesting, {playerName}.\"";
    }

    private static string BuildGeneralReply(Npc npc, string playerName, string? trait, bool knowsThem, int seed)
    {
        var aside = Aside(npc, trait);

        if (knowsThem && (seed % 3) == 0)
            return $"{npc.Name}{aside} answers like someone who has already taken your measure. \"You know how I am by now, {playerName}. Out with it.\"";

        return (seed % 2) == 0
            ? $"{npc.Name}{aside} answers without dressing it up. \"That is the shape of it, {playerName}. Push on whichever part interests you.\""
            : $"{npc.Name}{aside} shrugs, and the shrug says most of it. \"Make of that what you like, {playerName}.\"";
    }

    /// <summary>
    /// Renders a trait as a narrator aside rather than dialogue.
    ///
    /// Authored personalities are written in the third person about the character — "Lonely since her
    /// husband left", "Claims he saw the dungeon boss once" — so quoting them inside the NPC's own
    /// speech produced characters describing themselves in the third person. As an appositive in the
    /// narration the same text is grammatically correct and still shows who they are.
    /// </summary>
    private static string Aside(Npc npc, string? trait)
    {
        if (string.IsNullOrWhiteSpace(trait))
            return string.Empty;

        var text = trait.Trim().TrimEnd('.');
        if (text.Length == 0)
            return string.Empty;

        // Lower the first letter so the aside reads as part of the sentence, unless it is a name.
        if (char.IsUpper(text[0]) && (text.Length == 1 || !char.IsUpper(text[1])))
            text = char.ToLowerInvariant(text[0]) + text[1..];

        return $", {text},";
    }

    /// <summary>
    /// Splits an authored personality into quotable, sentence-sized traits. The seeded personalities
    /// are written as short declarative statements, which makes them usable as the NPC's own outlook
    /// rather than something the narrator has to paraphrase.
    /// </summary>
    private static List<string> ExtractTraits(string? personality)
    {
        if (string.IsNullOrWhiteSpace(personality))
            return [];

        return personality
            .Split(['.', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length is >= 12 and <= 110)
            .Select(part => part.EndsWith('.') ? part : part + ".")
            .Take(6)
            .ToList();
    }

    private static bool MentionsPayment(string input) =>
        MentionsAny(input, "coin", "gold", "pay", "buy", "drink", "tip", "bribe", "silver", "purse");

    private static bool AsksForInformation(string input) =>
        MentionsAny(input, "rumor", "rumour", "gossip", "heard", "news", "know about", "tell me", "what do you know",
            "any word", "anything interesting", "what's going on", "whats going on");

    private static bool IsGreeting(string input)
    {
        var text = input.TrimStart().ToLowerInvariant();
        return text.StartsWith("hello", StringComparison.Ordinal)
            || text.StartsWith("hi", StringComparison.Ordinal)
            || text.StartsWith("hey", StringComparison.Ordinal)
            || text.StartsWith("greetings", StringComparison.Ordinal)
            || text.StartsWith("good morning", StringComparison.Ordinal)
            || text.StartsWith("good evening", StringComparison.Ordinal);
    }

    private static bool LooksLikeAskingForSomething(string input) =>
        AsksForInformation(input) || MentionsAny(input, "promised", "owe", "you said", "well?", "so?", "going to tell");

    private static bool MentionsAny(string? haystack, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(haystack)) return false;
        var text = haystack.ToLowerInvariant();
        return needles.Any(n => text.Contains(n, StringComparison.Ordinal));
    }
}
