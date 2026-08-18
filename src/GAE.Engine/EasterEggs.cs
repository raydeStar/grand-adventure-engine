namespace GAE.Engine;

/// <summary>
/// Homages to the things this engine grew out of: Monty Python and the Holy Grail, the Final
/// Fantasy series, Weird Al Yankovic, Dungeon Crawler Carl, and the old-school crawlers and
/// text adventures that invented the genre's vocabulary.
///
/// Two rules govern everything in this file:
///
/// 1. <b>The trigger may quote; the response never does.</b> Players type the phrases they
///    remember, so the recognised inputs include famous words. Every line the game writes back is
///    original text that evokes the source rather than reproducing it — no song lyrics, no film
///    dialogue, no passages lifted from a novel.
/// 2. <b>Trademarked names stay on the input side.</b> A player may type a franchise creature's
///    name; the reply describes the creature instead of naming it.
///
/// Magic words are real recognised commands, not fallback text. They cost no turn and are safe to
/// use in any interaction mode.
/// </summary>
public static class EasterEggs
{
    /// <summary>
    /// Recognised magic words, normalised to lowercase with surrounding whitespace removed. Several
    /// spellings can share one response pool.
    /// </summary>
    private static readonly Dictionary<string, string[]> MagicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Text adventures and old-school crawlers ──

        // The original magic word, from the cave that started all of this.
        ["xyzzy"] =
        [
            "You speak the word. Somewhere far below, something ancient declines to be impressed. Nothing happens.",
            "The word hangs in the air, does nothing whatsoever, and somehow feels like coming home.",
            "A hollow click echoes from a cave that has not existed for forty years. Nothing else happens."
        ],
        ["plugh"] =
        [
            "The word works exactly as well as it ever did, which is to say not at all.",
            "You are not teleported. You are, however, briefly filled with the confidence of someone holding graph paper."
        ],
        ["plover"] =
        [
            "No emerald appears. You check your pockets anyway."
        ],
        // NetHack: the word players scratch into the dust to make monsters think better of it.
        ["elbereth"] =
        [
            "You scratch the word into the floor. Nothing in this room can read, but the gesture is noted with respect.",
            "The dust holds your engraving for a moment, then forgets it. Tradition, however, is satisfied."
        ],
        ["dywypi"] =
        [
            "Not yet. Your possessions remain resolutely unidentified, and you remain resolutely alive."
        ],
        ["yendor"] = ["Not in this dungeon. Probably. Keep descending and ask again."],
        ["amulet of yendor"] = ["Not in this dungeon. Probably. Keep descending and ask again."],
        // Zork's lurking dark-dweller.
        ["grue"] =
        [
            "You hear nothing menacing at all, which is precisely what a patient predator would arrange.",
            "Something in the unlit places declines to introduce itself. Bring a lantern."
        ],
        ["graph paper"] =
        [
            "You sketch the room's corners from memory. Three walls agree with you. The fourth is being difficult.",
            "Somewhere, a pencil is worn to a stub in your honour."
        ],
        ["twisty little passages"] =
        [
            "The passages here are twisty, little, and — you note with relief — distinguishable from one another."
        ],

        // ── Monty Python and the Holy Grail ──

        ["ni"] =
        [
            "The word lands with terrible weight. A nearby shrubbery shifts, sensing it may soon be required.",
            "Somewhere close by, several knights startle and refuse to explain why."
        ],
        ["shrubbery"] =
        [
            "You have no shrubbery. This will be held against you by parties yet to be introduced."
        ],
        ["flesh wound"] =
        [
            "A knight several rooms away, currently missing more of himself than he will admit, nods in fierce agreement."
        ],
        ["holy hand grenade"] =
        [
            "You have no such relic. Were you holding one, the instructions would insist on a very specific count, and you would get it wrong."
        ],
        ["coconut"] = ["No coconuts. Your horse remains theoretical."],
        ["coconuts"] = ["No coconuts. Your horse remains theoretical."],
        ["what is your quest"] =
        [
            "To survive the next room, mostly. You suspect the correct answer is grander, and that guessing wrong is fatal."
        ],
        ["favourite colour"] = ["You commit to an answer. Nothing collapses. This time."],
        ["favorite color"] = ["You commit to an answer. Nothing collapses. This time."],
        ["airspeed velocity"] =
        [
            "You begin to work it out, realise nobody specified which kind of swallow, and stop."
        ],
        ["killer rabbit"] =
        [
            "Nothing small and white is watching you. You check twice, because the first check is never the reassuring one."
        ],

        // ── Final Fantasy ──

        ["fanfare"] =
        [
            "Seven triumphant notes rise from absolutely nowhere, hold for exactly as long as victory deserves, and stop.",
            "A brief flourish of brass insists that something has been accomplished. You choose to believe it."
        ],
        ["victory fanfare"] = ["A brief flourish of brass insists that something has been accomplished. You choose to believe it."],
        ["save point"] =
        [
            "A patch of floor glows faintly, promises that this moment can be returned to, and declines to elaborate.",
            "You feel, briefly, that this exact instant has been written down somewhere safe."
        ],
        ["limit break"] =
        [
            "Something builds, crests, and finds no target worthy of it. The feeling passes. You will need it later."
        ],
        ["phoenix down"] =
        [
            "You have no such feather. Death, should you meet it, will be handled the old-fashioned way."
        ],
        ["chocobo"] =
        [
            "No enormous flightless bird presents itself for saddling. The stable, such as it is, remains empty.",
            "You whistle for a great yellow riding bird. A pigeon arrives instead, and is deeply unsuitable."
        ],
        ["kupo"] =
        [
            "Nothing small, white and winged answers. The pompom-bearers keep their own counsel."
        ],
        ["moogle"] = ["Nothing small, white and winged answers. The pompom-bearers keep their own counsel."],
        ["cid"] =
        [
            "Every world has an engineer of that name, usually shouting at an airship. This one is presumably busy."
        ],
        ["tonberry"] =
        [
            "Nothing in a small robe is approaching you very slowly with a kitchen knife. Do keep checking."
        ],
        ["excalipoor"] =
        [
            "You draw a magnificent blade. It is, on inspection, magnificent at nothing whatsoever."
        ],

        // ── Weird Al Yankovic ──

        ["accordion"] =
        [
            "An accordion wheezes to life somewhere behind you, plays four bars of something suspiciously familiar with all the wrong words, and stops.",
            "Bellows sigh open in the dark. Whatever tune follows is legally distinct from the one you recognised."
        ],
        ["polka"] =
        [
            "Somewhere, a bard compresses eleven heroic ballads into ninety seconds of accordion and refuses to apologise."
        ],
        ["parody"] =
        [
            "The local bard's entire repertoire consists of other bards' songs with the words replaced. He is, infuriatingly, better paid than any of them."
        ],
        ["hawaiian shirt"] =
        [
            "You are not wearing one. The bard is, and it clashes with the entire concept of a dungeon."
        ],

        // ── Dungeon Crawler Carl ──

        ["crawler"] =
        [
            "[The system acknowledges the term. Somewhere, a number goes up. It is not your number.]",
            "[Registered. You are not the protagonist of this broadcast, but you are polling well.]"
        ],
        ["sponsor"] =
        [
            "[No benefactor has claimed you. Your recent decisions are cited as the reason.]",
            "[A sponsorship offer is drafted, reconsidered, and quietly withdrawn.]"
        ],
        ["sponsorship"] = ["[A sponsorship offer is drafted, reconsidered, and quietly withdrawn.]"],
        ["viewers"] =
        [
            "[Nobody is watching. The empty seats are, the system assures you, a formatting error.]",
            "[Concurrent viewers: unflattering. The audience is described as patient.]"
        ],
        ["audience"] = ["[Concurrent viewers: unflattering. The audience is described as patient.]"],
        ["loot box"] =
        [
            "[No box materialises. The odds it would have offered are described as generous by people who set them.]"
        ],
        ["achievement"] =
        [
            "[Achievement unlocked: Asked For An Achievement. Reward: this message. Congratulations.]"
        ],
        ["talking cat"] =
        [
            "No cat deigns to answer. Somewhere, one with far better charisma than you is being interviewed."
        ]
    };

    /// <summary>
    /// Recognises a magic word and returns its response.
    ///
    /// Matching is exact on the whole trimmed input: a magic word is something the player types on
    /// purpose, and prefix-matching short words like "ni" would hijack ordinary sentences.
    /// </summary>
    public static bool TryGetMagicWordResponse(string? rawInput, string? actionId, out string response)
    {
        response = string.Empty;

        var trimmed = (rawInput ?? string.Empty).Trim().TrimEnd('.', '!', '?', ',', ';', ':');
        if (trimmed.Length == 0)
            return false;

        if (!MagicWords.TryGetValue(trimmed, out var pool) || pool.Length == 0)
            return false;

        // Seeded from the action id so the reply is stable for a given action — replays and tests
        // stay deterministic — while repeated invocations vary.
        var seed = actionId is null ? 0 : actionId.GetHashCode(StringComparison.Ordinal);
        response = pool[Math.Abs(seed % pool.Length)];
        return true;
    }

    /// <summary>True when the input is a recognised magic word.</summary>
    public static bool IsMagicWord(string? rawInput)
    {
        var trimmed = (rawInput ?? string.Empty).Trim().TrimEnd('.', '!', '?', ',', ';', ':');
        return trimmed.Length > 0 && MagicWords.ContainsKey(trimmed);
    }

    /// <summary>Every recognised magic word, for the discovery hint and for tests.</summary>
    public static IReadOnlyCollection<string> AllMagicWords => MagicWords.Keys;

    // ── Combat flavour ──────────────────────────────────────────────────

    /// <summary>
    /// Bravado from an enemy that is nearly finished — a nod to a certain knight's refusal to
    /// concede that any of his injuries count. "{0}" is the enemy's name.
    /// </summary>
    private static readonly string[] DefiantWoundedTaunts =
    [
        "{0} insists the wound is trivial, and lists the reasons at length.",
        "{0} describes the injury as a formality and invites you to continue.",
        "{0} regards the missing pieces as a technicality of no real importance.",
        "{0} has clearly not been told how badly this is going, and would resent being told.",
        "{0} declares the fight barely begun, from a position that suggests otherwise."
    ];

    /// <summary>
    /// Returns bravado for an enemy on its last legs, or null when the moment does not call for it.
    /// Fires only below a quarter health and only sometimes, so it stays a punchline.
    /// </summary>
    public static string? TryBuildDefiantWoundedTaunt(string enemyName, int hp, int maxHp, int seed)
    {
        if (maxHp <= 0 || hp <= 0)
            return null;

        if (hp * 4 > maxHp)
            return null;

        // Roughly one in three qualifying moments, chosen deterministically from the seed.
        if (Math.Abs(seed) % 3 != 0)
            return null;

        var template = DefiantWoundedTaunts[Math.Abs(seed / 3) % DefiantWoundedTaunts.Length];
        return string.Format(template, enemyName);
    }

    /// <summary>Flourishes for a won fight, in the spirit of a certain series' victory sting.</summary>
    private static readonly string[] VictoryFlourishes =
    [
        "\U0001F3B5 Seven notes of brass arrive from nowhere in particular, entirely pleased with you.",
        "\U0001F3B5 A short triumphant sting plays. You have no idea where the orchestra is hiding.",
        "\U0001F3B5 Somewhere, unseen instruments agree that this counted."
    ];

    /// <summary>
    /// Returns a victory flourish, or null. Occasional by design — a fanfare after every scuffle
    /// stops being a joke and becomes a noise.
    /// </summary>
    public static string? TryBuildVictoryFlourish(int seed)
        => Math.Abs(seed) % 4 == 0
            ? VictoryFlourishes[Math.Abs(seed / 4) % VictoryFlourishes.Length]
            : null;

    // ── Death flavour ───────────────────────────────────────────────────

    /// <summary>
    /// Epitaphs blending the old crawlers' blunt bookkeeping with the running commentary of a
    /// dungeon that is also a broadcast.
    /// </summary>
    private static readonly string[] DeathEpitaphs =
    [
        "[Do you want your possessions identified? The question is traditional. The answer is no.]",
        "[Run ended. The audience is informed. The audience is unmoved.]",
        "[Your remains are logged, catalogued, and rated below average for presentation.]",
        "[Achievement unlocked: Died. It is not a rare one.]",
        "[The system notes your performance and declines to elaborate on it.]"
    ];

    /// <summary>Returns a death epitaph chosen deterministically from the seed.</summary>
    public static string BuildDeathEpitaph(int seed)
        => DeathEpitaphs[Math.Abs(seed) % DeathEpitaphs.Length];
}
