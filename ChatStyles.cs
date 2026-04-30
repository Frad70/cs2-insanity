using System.Text;

namespace InsanityRevive;

public enum BotStyle { Normal, GenZ, KidRage, ESL, DemonRager, SpeedTyper }

/// How chatty the bot is overall. Distribution skewed: most bots are mostly silent.
public enum Talkativeness { Silent, Low, Medium, High }

/// Demeanor — hostile vs friendly. Most are hostile/neutral; ~10% are nice teammates.
public enum Friendliness { Hostile, Neutral, Friendly }

/// Bot's gameplay archetype — affects positioning/movement decisions.
/// Generic bots use the engine default. Archetypes pick spots and timing differently.
public enum BotArchetype
{
    Generic,        // default — no override
    Entry,          // pushes first, takes opening duels (high aim, low patience)
    Lurker,         // off-angles, late timings, knife-out rotations
    AwperPassive,   // long sightlines, peeks slowly, holds angles
    AwperAggro,     // dry-peek, swing wide, pre-fires
    Support,        // utility-first, trade-frags, hugs teammates
    Anchor,         // site-holder, won't push, pre-aims common entry
    IGL,            // vocal in callouts, calls strats, will rotate aggressively
    BaitOMatic,     // hides behind teammate, frags only when teammate dies
    HeadshotOnly,   // only aims head — high reward, lower hit %
}

public class BotPersona
{
    public BotStyle Style { get; set; } = BotStyle.Normal;
    public Talkativeness Tab { get; set; } = Talkativeness.Silent;
    public Friendliness Mood { get; set; } = Friendliness.Hostile;
    public string Name { get; set; } = "bot";
    public int Wpm { get; set; } = 60;
    public float TypoChance { get; set; } = 0.05f;
    public float CapsChance { get; set; } = 0.05f;
    public bool TauntRussianTarget { get; set; } = false;
    /// Per-bot per-match incidents — used for vote/grudge logic.
    public int Grudge { get; set; } = 0;
    /// "No-headphones" trait — won't react to gunshots/footsteps.
    public bool IsDeaf { get; set; } = false;
    /// 0..2 — relative skill multiplier; affects aim quality and chance of "bad play"
    /// behaviour like missing shots / standing in the open.
    public float Skill { get; set; } = 1.0f;
    /// Counter for "low [zone] {n}x" callouts so prefix increments.
    public int LowCallCount { get; set; } = 0;

    // --- Per-bot aim profile (driven by Skill but with random spread) ---
    // We compute these once at persona creation so the bot has a consistent
    // "feel" the whole match. Reseeded if the bot reconnects.
    public float AimSnapPerTick { get; set; } = 0.30f;     // lerp aggressiveness (0..1)
    public float AimMaxBiasDeg { get; set; } = 0.5f;       // baked-in goal jitter (deg)
    public float AimGoalRefreshSec { get; set; } = 0.22f;  // goal lifetime (sec)
    public float AimReactionTimeSec { get; set; } = 0.18f; // delay after spotting target
    public float AimOvershootChance { get; set; } = 0.10f; // chance per goal-refresh
    public float AimOvershootDeg { get; set; } = 1.5f;     // overshoot magnitude (deg)
    public float AimTrackingNoiseDeg { get; set; } = 0.0f; // tiny per-tick wobble (deg)
    public float AimMicroAdjustChance { get; set; } = 0.0f;// chance of mid-engage micro-correction
    public bool  AimSpraysWell { get; set; } = false;      // pulls down on prolonged fire
    public float AimFlickStrength { get; set; } = 1.0f;    // multiplier for big snaps to fresh enemies

    // Bot's gameplay archetype — affects movement/positioning when we get there.
    // Computed alongside aim profile so behaviors are correlated with skill.
    public BotArchetype Archetype { get; set; } = BotArchetype.Generic;

    /// Last few generated lines — used for de-dup so the same phrase doesn't fire 3 times in a row
    public Queue<string> RecentLines { get; } = new();
    public const int RecentMax = 6;

    /// Per-round mood. Edited by streaks, ff incidents.
    public int Salt { get; set; } = 0;     // 0 calm .. 5 livid

    /// Multiplier on chat probability based on Tab.
    public float ChatProbabilityFactor => Tab switch
    {
        Talkativeness.Silent => 0.05f,
        Talkativeness.Low    => 0.30f,
        Talkativeness.Medium => 0.85f,
        Talkativeness.High   => 1.60f,
        _ => 0.30f,
    };
}

public static class ChatStyles
{
    private static bool HasCyrillic(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
            if (c >= 'Ѐ' && c <= 'ӿ') return true;
        return false;
    }

    /// <summary>
    /// How a real player would refer to {target} when they can't be bothered to type the
    /// real nickname (special chars, foreign script, too long, weird symbols). Picks a
    /// strategy: short prefix, transliteration, color/team description, or generic ref.
    /// </summary>
    public static string RefName(string targetName, int targetTeam, Random rng)
    {
        if (string.IsNullOrWhiteSpace(targetName)) return "u";

        // Strip leading/trailing non-letters (clan tags etc.)
        var clean = StripJunk(targetName);

        var roll = rng.NextDouble();

        // (a) transliterate cyrillic
        if (HasCyrillic(clean) && roll < 0.55)
            return Translit(clean).ToLowerInvariant();

        // (b) just first 2-4 latin chars when it's long
        if (roll < 0.65 && clean.Length > 4)
            return clean[..rng.Next(2, Math.Min(5, clean.Length + 1))].ToLowerInvariant();

        // (c) team color reference
        if (roll < 0.85)
        {
            string col = targetTeam == 2 ? "red" : targetTeam == 3 ? "blue" : "spec";
            return rng.Next(5) switch
            {
                0 => col,
                1 => "the " + col + " guy",
                2 => col + " dude",
                3 => "that " + col + " kid",
                _ => col + " " + (1 + rng.Next(5)),
            };
        }

        // (d) generic
        if (roll < 0.97)
            return new[] { "u", "this guy", "that one", "kid", "noob", "dude", "bro", "this clown" }[rng.Next(8)];

        // (e) full name (rare)
        return clean;
    }

    private static string StripJunk(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-') sb.Append(c);
        }
        var r = sb.ToString().Trim();
        return r.Length == 0 ? s : r;
    }

    private static readonly Dictionary<char, string> Cyr2Lat = new()
    {
        ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",['е']="e",['ё']="yo",
        ['ж']="zh",['з']="z",['и']="i",['й']="y",['к']="k",['л']="l",['м']="m",
        ['н']="n",['о']="o",['п']="p",['р']="r",['с']="s",['т']="t",['у']="u",
        ['ф']="f",['х']="h",['ц']="ts",['ч']="ch",['ш']="sh",['щ']="sch",
        ['ъ']="",['ы']="y",['ь']="",['э']="e",['ю']="yu",['я']="ya",
    };
    private static string Translit(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
            sb.Append(Cyr2Lat.TryGetValue(c, out var v) ? v : c.ToString());
        return sb.ToString();
    }

    public static BotPersona RandomPersona(Random rng, string name)
    {
        var styleRoll = rng.NextDouble();
        BotStyle s =
            styleRoll < 0.32 ? BotStyle.Normal      :
            styleRoll < 0.50 ? BotStyle.GenZ        :
            styleRoll < 0.66 ? BotStyle.KidRage     :
            styleRoll < 0.80 ? BotStyle.ESL         :
            styleRoll < 0.93 ? BotStyle.SpeedTyper  :
                               BotStyle.DemonRager;

        var tabRoll = rng.NextDouble();
        Talkativeness tab =
            tabRoll < 0.55 ? Talkativeness.Silent :
            tabRoll < 0.82 ? Talkativeness.Low    :
            tabRoll < 0.95 ? Talkativeness.Medium :
                             Talkativeness.High;

        var moodRoll = rng.NextDouble();
        Friendliness mood =
            moodRoll < 0.55 ? Friendliness.Hostile  :
            moodRoll < 0.88 ? Friendliness.Neutral  :
                              Friendliness.Friendly;

        var (wpmLo, wpmHi, typo, caps) = s switch
        {
            BotStyle.Normal      => (45,  85, 0.04f, 0.04f),
            BotStyle.GenZ        => (55, 100, 0.06f, 0.10f),
            BotStyle.KidRage     => (50,  95, 0.10f, 0.40f),
            BotStyle.ESL         => (24,  52, 0.32f, 0.06f),
            BotStyle.SpeedTyper  => (95, 140, 0.42f, 0.05f),
            BotStyle.DemonRager  => (70, 110, 0.12f, 0.55f),
            _                    => (50,  80, 0.05f, 0.05f),
        };

        // Skill: gaussian-ish 0.4..1.7, mean 1.0
        float skill = 0.55f + (float)rng.NextDouble() * 1.10f;
        // Deaf trait: ~6%
        bool deaf = rng.NextDouble() < 0.06;

        var p = new BotPersona
        {
            Style = s,
            Tab = tab,
            Mood = mood,
            Name = name,
            Wpm = rng.Next(wpmLo, wpmHi + 1),
            TypoChance = typo,
            CapsChance = caps,
            Skill = skill,
            IsDeaf = deaf,
        };

        ApplySkillToAim(p, rng);
        ApplyArchetype(p, rng);
        return p;
    }

    /// Roll aim parameters based on Skill so a low-skill bot feels different from a
    /// high-skill bot. Tuned by hand to match what MM players "look like" on radar:
    /// low-skill bots are loose, high-skill are crisp but still imperfect.
    public static void ApplySkillToAim(BotPersona p, Random rng)
    {
        float s = Math.Clamp(p.Skill, 0.40f, 1.80f);

        // SnapPerTick: 0.10 (super-loose, MG/Silver feel) .. 0.55 (Global feel).
        // Mapped piecewise so most bots cluster around 0.22..0.40.
        float baseSnap = 0.10f + (s - 0.40f) * (0.45f / 1.40f);   // 0.10..0.55
        float snapJit  = ((float)rng.NextDouble() - 0.5f) * 0.06f; // ±3%
        p.AimSnapPerTick = Math.Clamp(baseSnap + snapJit, 0.06f, 0.65f);

        // MaxBiasDeg: high skill → tiny consistent bias; low skill → wide jitter
        // so even when the goal is the head, they end up shooting bodies/walls.
        float baseBias = 2.4f - (s - 0.40f) * (2.1f / 1.40f);     // 2.4..0.30
        float biasJit  = (float)rng.NextDouble() * 0.4f - 0.2f;    // ±0.2
        p.AimMaxBiasDeg = Math.Clamp(baseBias + biasJit, 0.10f, 3.5f);

        // GoalRefreshSec: high skill = re-pick more often (smoother tracking);
        // low skill = stale goal (looks like they're "asleep").
        float baseRefresh = 0.40f - (s - 0.40f) * (0.28f / 1.40f); // 0.40..0.12
        float refreshJit  = ((float)rng.NextDouble() - 0.5f) * 0.06f;
        p.AimGoalRefreshSec = Math.Clamp(baseRefresh + refreshJit, 0.08f, 0.55f);

        // ReactionTime: low skill bots react slow (300+ms), high skill are 80-150ms.
        float baseRT = 0.30f - (s - 0.40f) * (0.22f / 1.40f);     // 0.30..0.08
        float rtJit  = ((float)rng.NextDouble() - 0.3f) * 0.10f;
        p.AimReactionTimeSec = Math.Clamp(baseRT + rtJit, 0.05f, 0.50f);

        // Overshoot: more common in mid-skill bots (eager but inconsistent).
        // Pure low-skill = lazy, pure high-skill = controlled. Bell-shape.
        float skillBell = 1.0f - Math.Abs(s - 1.05f) / 0.65f;     // peak around s=1.05
        skillBell = Math.Clamp(skillBell, 0.0f, 1.0f);
        p.AimOvershootChance = 0.04f + 0.18f * skillBell + (float)rng.NextDouble() * 0.05f;
        p.AimOvershootDeg    = 1.0f + 1.5f * skillBell + (float)rng.NextDouble() * 0.6f;

        // Tracking noise: per-tick mouse wobble for low-skill bots only.
        if (s < 0.85f)
            p.AimTrackingNoiseDeg = 0.10f + (0.85f - s) * 0.40f;  // up to 0.28°
        else
            p.AimTrackingNoiseDeg = 0.0f;

        // Micro-adjustments: high-skill bots correct mid-engage. ~5% of bots.
        p.AimMicroAdjustChance = (s > 1.20f) ? 0.20f + (float)rng.NextDouble() * 0.20f : 0f;

        // Spray control: emerges at s>1.0
        p.AimSpraysWell = s > 1.05f && rng.NextDouble() < 0.55;

        // Flick strength: 0.4..1.4 — low-skill bots flick weakly (don't reach target).
        p.AimFlickStrength = 0.55f + (s - 0.40f) * (0.85f / 1.40f);
        p.AimFlickStrength += ((float)rng.NextDouble() - 0.5f) * 0.20f;
        p.AimFlickStrength = Math.Clamp(p.AimFlickStrength, 0.35f, 1.55f);
    }

    /// Roll a gameplay archetype. Skill biases the distribution: high-skill bots
    /// more likely to be Entry/Awper/IGL; low-skill more likely Generic/BaitOMatic.
    public static void ApplyArchetype(BotPersona p, Random rng)
    {
        float s = Math.Clamp(p.Skill, 0.40f, 1.80f);
        float roll = (float)rng.NextDouble();

        // ~40% Generic regardless — keeps server feeling like a real lobby
        if (roll < 0.40f) { p.Archetype = BotArchetype.Generic; return; }
        roll = (roll - 0.40f) / 0.60f;

        // Skill-weighted bucket. 10 archetypes; weights skill-dependent.
        float w_entry        = 0.10f + 0.18f * (s - 0.7f);
        float w_lurker       = 0.10f + 0.05f * (s - 0.7f);
        float w_awperPassive = 0.07f + 0.08f * (s - 0.7f);
        float w_awperAggro   = 0.06f + 0.10f * (s - 0.7f);
        float w_support      = 0.16f;
        float w_anchor       = 0.13f;
        float w_igl          = 0.04f + 0.06f * (s - 0.7f);
        float w_bait         = 0.18f - 0.10f * (s - 0.7f);
        float w_headshot     = 0.05f + 0.04f * (s - 0.7f);
        // Clamp negatives
        var weights = new (BotArchetype, float)[]
        {
            (BotArchetype.Entry, Math.Max(0.02f, w_entry)),
            (BotArchetype.Lurker, Math.Max(0.02f, w_lurker)),
            (BotArchetype.AwperPassive, Math.Max(0.02f, w_awperPassive)),
            (BotArchetype.AwperAggro, Math.Max(0.02f, w_awperAggro)),
            (BotArchetype.Support, Math.Max(0.02f, w_support)),
            (BotArchetype.Anchor, Math.Max(0.02f, w_anchor)),
            (BotArchetype.IGL, Math.Max(0.02f, w_igl)),
            (BotArchetype.BaitOMatic, Math.Max(0.02f, w_bait)),
            (BotArchetype.HeadshotOnly, Math.Max(0.02f, w_headshot)),
        };

        float total = 0f;
        foreach (var (_, w) in weights) total += w;
        float pick = roll * total;
        float acc = 0f;
        foreach (var (a, w) in weights)
        {
            acc += w;
            if (pick <= acc) { p.Archetype = a; return; }
        }
        p.Archetype = BotArchetype.Generic;
    }

    public static bool TargetIsRussian(string name) => HasCyrillic(name);

    // ----------------------------------------------------------------------
    //  PHRASE POOLS — large, intentionally distinct, no emojis.
    //  Keys with {who} get the related player's name substituted in.
    // ----------------------------------------------------------------------

    private static readonly string[] Kill_Normal =
    {
        "ez", "ezz", "ezzz", "ezzzzz", "got him", "next", "sit down", "trash",
        "absolute trash", "free kill", "1 down", "easy diff", "skill diff",
        "diffed", "rolled u", "lol", "lmao", "wp btw", "nt", "delete game",
        "u literally cant aim", "0 aim", "shut up bot", "ratio", "ratio + L",
        "shit aim {who}", "ur mid", "trash kid", "cope", "uninstall",
        "go back to silver", "shouldve dodged", "nice strat 4head",
        "didnt even try ngl", "imagine peeking", "ur head was free",
        "snap aim ezpz", "didnt move btw", "u walked into it",
        "predicted u twice", "preaim diff", "free real estate",
        "hes giving free", "tagged u", "was that ai?", "ur play time wasted",
        "one taps only", "got him im going next", "thats one for the wallet",
        "didnt even check the angle bro", "this is casual to me",
    };
    private static readonly string[] Kill_GenZ =
    {
        "ratio + L + skill issue", "no cap u r mid", "bro fell off",
        "L taper fade kid", "u r not him", "down bad fr", "ohio aim ngl",
        "fanum tax on ur kd", "skibidi corpse", "ate that no crumbs",
        "rizzless", "this u? L", "we cooked u", "u not eating today",
        "low taper fade aim", "sigma move from me", "actual bot behavior",
        "thats crazy", "diabolical kill ngl", "im him fr",
        "we washed him", "L for him W for me", "ur gameplay is mid",
        "like literally why are u still here", "down catastrophic",
        "delulu about ur skill", "kid is cooked", "kid is jorking",
        "no chat fr ratio + L + ur opinion + mid + i diff u + skill issue",
    };
    private static readonly string[] Kill_KidRage =
    {
        "EZ CLAP", "EZZZZZZ", "1V1 ME NOOB", "L BOZO", "GET CLAPPED",
        "REPORTED FOR BEING TRASH", "uninstall kid", "imagine getting hsed lol",
        "u play with feet?", "go back to fortnite", "im built different",
        "im sigma fr", "DIFFFF", "actual silver lobby", "GET REKT",
        "GET WRECKED KID", "cry harder kid", "go to bed school",
        "ur dad is dissapointed", "WORST PLAYER NA", "MOMS AGGRO MODE",
        "BOZO ALERT", "im hardstuck against bots", "rage q time?",
    };
    private static readonly string[] Kill_ESL =
    {
        "ez game noob", "ez noob", "u bad", "u so bad delete game",
        "i diff u hard", "plz lern aim", "report this guy team",
        "stupid kid", "ur mom from steam", "go back faceit lvl 1",
        "skill 0 luck 0 lol", "russia win", "not skill ur bad",
        "stfu noob", "no aim no brain", "u noob 100 percent",
        "go practice 1v1 first", "this guy hardstuck silver",
        "no team play just lucky", "his mom proud probably",
        "this is faceit lvl 1?", "i play 5 years u play 1 day",
    };
    private static readonly string[] Kill_DemonRager =
    {
        "FUCKING TRASH", "GET FUCKED KID", "FUCK U {who}",
        "KYS LOSER", "DIE BITCH", "STFU AND UNINSTALL",
        "FUCKING BOT BEHAVIOR", "I FUCKING HATE THIS GAME",
        "U ARE FUCKING NOTHING", "WHY ARE U STILL ALIVE",
        "ZERO SKILL ZERO IQ", "GO BACK TO TF2 RETARD",
        "ANOTHER ONE BITES THE FUCKING DUST", "DIE DIE DIE",
        "FUCK U AND UR FAMILY", "STAY FUCKING DOWN",
        "U MAKE ME SICK", "BE BETTER OR DELETE",
        "WE ARE NOT THE SAME I FUCKING TAP YOU",
    };
    private static readonly string[] Kill_SpeedTyper =
    {
        "ezzzz", "lmaoooo", "wtfff he so bad", "bro got cooked",
        "diffffff", "lmao instant", "ez 1tap", "imagine that aim",
        "he literally cant play", "stop coping kid", "next bot in queue",
        "lmaaaooo", "rolled fr", "0iq btw", "wtffffff",
        "bro just walked in", "ezzzzzclap", "1tapppp", "noooooo waaaay",
    };

    private static readonly string[] Death_Normal =
    {
        "wtf", "lucky", "tagged", "ok", "rage", "bs", "fucking lag",
        "wallhack?", "1tap from where", "really?", "bro is cheating",
        "nice timing", "lol nice", "shit hitreg", "i hate this team",
        "sus aim", "no way that hit", "100hp to 0?", "tagged 4 then died",
        "im actually done", "actual aimbot", "cl_interp 10",
        "third strike i swear", "this is rigged",
        "dropped my mouse mid spray", "i pre aimed wrong",
        "ill rotate after this no wait im dead",
    };
    private static readonly string[] Death_GenZ =
    {
        "no way", "ratio against me", "im cooked", "down bad",
        "we washed", "team is mid", "actually diabolical",
        "im not him today", "bro is cracked", "L for me ig",
        "i fell off", "ratio L take this round", "im ohio rn",
        "skill issue on me i guess", "got mid diffed",
    };
    private static readonly string[] Death_KidRage =
    {
        "WHAT", "WTF WAS THAT", "STREAM SNIPER", "REPORT FOR HACKING",
        "UR HACKING IM REPORTING", "MOMMMM HE HACK", "100% AIMBOT",
        "NO WAY THAT WAS LEGIT", "FUCKING CHEATER", "WALLBANG THROUGH 5 WALLS",
        "RIGGED LOBBY", "REVIEWED REPLAYS U HACK FR",
        "VAC SHOULD BAN U", "100% SUS MOVEMENT",
    };
    private static readonly string[] Death_ESL =
    {
        "wallhack 100%", "u r cheater", "report u to valve",
        "no aim only luck", "bs hitreg", "vac coming for u",
        "this game lag too much", "russia conection",
        "he cheat for sure", "team see he cheat?",
        "ban hammer come now", "demo will show",
    };
    private static readonly string[] Death_DemonRager =
    {
        "FUCK THIS GAME", "FUCKING CHEATER", "U USE WALLHACK",
        "STREAMSNIPED", "100% HACKING", "I HOPE U DIE IRL",
        "FUCKING RNG GAME", "I HATE THIS FUCKING TEAM",
        "KYS HACKER", "RAGEHACKING TRASH",
        "OFF YOURSELF FOR USING WH", "U R A VIRUS ON THIS GAME",
        "FUCKING DELETE THIS PLAYER",
    };
    private static readonly string[] Death_SpeedTyper =
    {
        "wtffff", "lmaooo no way", "he wallhack ngl", "diffed by hack",
        "bs aim", "so unlucky lmao", "rngngngn", "smh fr",
        "bruhhhh", "lmaoooooo not me getting clapped",
    };

    private static readonly string[] RoundStart_Normal =
    {
        "default", "stay alive", "save", "anti eco",
        "stack a", "stack b", "ima go b", "ima go a",
        "follow me", "rotate fast", "watch flank", "play picks",
        "no awp this round", "info first", "smoke off ct then peek",
        "bring nade for mid", "dont overpush", "im b alone need rotates",
        "lurking b dont push w/o me", "default wait timer", "play default",
        "im on b watch numbers", "anyone for utility?", "boost me on a",
    };
    private static readonly string[] RoundStart_GenZ =
    {
        "we cooking this round", "ima 1v9 bestie", "stay built",
        "lock in fr", "no l's this round", "pop off pls",
        "default but make it sigma", "gyatt strat lets go",
        "everyone on the same page rn", "we eating this round",
        "bestie strat: rush b", "let him cook", "follow my lead bestie",
    };
    private static readonly string[] RoundStart_KidRage =
    {
        "RUSH B CYKA", "I CARRY", "DONT BE TRASH", "DO NOT THROW",
        "FOLLOW ME OR LOSE", "EZ ROUND INC", "LOCK IN FR",
        "WTF NOBODY MOVES", "GO GO GO", "im 1v5 watch",
        "DROP ME AWP", "WHO HAS HE I NEED HE", "DO NOT FORGET DEFUSE",
        "ANYONE GOT KIT", "STOP RUSHING WITHOUT UTIL",
    };
    private static readonly string[] RoundStart_ESL =
    {
        "rush b", "rush a", "save plz", "buy nade plz",
        "i need awp", "anyone awp?", "team plz play",
        "stop afk", "we lose if no buy", "give me defuser",
        "drop me weapon", "where my team", "rush a default b safe",
        "we ECO i save",
    };
    private static readonly string[] RoundStart_DemonRager =
    {
        "DO NOT FUCK THIS UP", "DROP ME AWP NOW", "STOP BEING TRASH",
        "FOLLOW THE FUCKING CALL", "I SAID GO B", "THROWERS GO KYS",
        "ANYONE THROWS I REPORT", "I CARRY THIS LOBBY",
        "LISTEN OR LOSE", "SHUT THE FUCK UP AND PLAY",
    };
    private static readonly string[] RoundStart_SpeedTyper =
    {
        "default rotate b last 30", "rush 5 fast a", "im b u guys a",
        "smoke ct then push", "anti eco no scope", "tag and rotate",
        "stutter peek mid", "lurk a u guys b fast", "fake b real a",
        "split a thru cat",
    };

    // Banter — chill round-start chatter unrelated to strat
    private static readonly string[] RoundStart_Banter =
    {
        "lmao who afk", "anyone got drop?", "stop dancing",
        "im bored", "let me knife em", "knife round?",
        "no one buy nade as usual", "anyone got eco strat?",
        "drop me a deag at least", "gonna eco even if i can buy",
        "save it boys", "pretend its first round and chill",
        "yall ate breakfast?", "wholesome team rn",
        "team is talking thats new", "chillin chillin",
    };

    private static readonly string[] Win_Normal =
    {
        "ez", "wp us", "easy round", "diff", "skill diff",
        "trash team enemy", "thats how its done", "next round same",
        "owned them", "they couldnt do anything", "predicted everything",
        "called the rotate perfectly", "my reads were insane",
        "nt enemies", "thank you team", "clean round",
    };
    private static readonly string[] Win_GenZ =
    {
        "we ate no crumbs", "diabolical W", "skill issue on them",
        "L bozos", "ratiod the lobby", "we cooking fr",
        "no caps W", "they fell off", "got cooked", "we him fr",
        "lobby got mid diffed", "ur whole team is ohio",
    };
    private static readonly string[] Win_KidRage =
    {
        "EZ CLAP", "EZZZZZ", "DIFF", "SKILL ISSUE LMAO",
        "GET CLAPPED LOSERS", "UNINSTALL ENEMY TEAM",
        "REPORT THEM FOR BEING BAD", "GG EZ NO RE",
        "U GUYS BAD AT GAME", "KIDDIE LOBBY",
        "GIRLS UNDER 12 LOBBY", "EZ NO RE BABY RAGE",
    };
    private static readonly string[] Win_ESL =
    {
        "ez round noobs", "report enemy noob", "ez game",
        "easy round russia", "they baby", "skill 0",
        "u all bad", "imagine play this bad", "go practice",
        "team noob enemy",
    };
    private static readonly string[] Win_DemonRager =
    {
        "EZZZZZZ FUCK U ALL", "FUCKING TRASH ENEMIES", "GG EZ KYS",
        "DELETE THE GAME LOSERS", "UNINSTALL CS2 RETARDS",
        "U LITERALLY CANT BEAT ME", "STAY MAD KIDS",
        "RAGEQUEUE BABYS", "REFUND THIS GAME LOSERS",
    };
    private static readonly string[] Win_SpeedTyper =
    {
        "ezz lmao", "diffff", "skill issue lol", "they trash ngl",
        "rolled em", "next round next L for them",
        "easy peezy", "ezz ezzzz",
    };

    private static readonly string[] Lose_Normal =
    {
        "team diff", "shit team", "no comms", "bot teammates",
        "i carry yall do nothing", "this team is mid",
        "im 1v5 every round", "trash teammates", "im hardstuck this rank",
        "team eco when they shouldnt have", "no awp no game",
        "missed easy reads", "no util used wtf", "team didnt rotate",
        "anchored too late",
    };
    private static readonly string[] Lose_GenZ =
    {
        "team is washed", "we mid fr", "L on us", "down bad team",
        "we not eating", "team got cooked", "ratio to my team",
        "L taper fade lobby", "team fell off in real time",
        "we ohio rn", "team chemistry zero",
    };
    private static readonly string[] Lose_KidRage =
    {
        "TEAM IS TRASH", "TF YOU ALL DOING", "MUTE EVERYONE",
        "REPORT TEAM FOR THROWING", "I HATE THIS TEAM",
        "GO BACK TO MM", "Y U NO BUY NOOB", "DONT QUEUE WITH ME",
        "REPORTING ALL OF U", "TEAM DIFF NOT SKILL",
    };
    private static readonly string[] Lose_ESL =
    {
        "team trash", "no skill team", "all noob no comms",
        "i carry no help", "1v5 i play", "team afk all round",
        "team play like rats", "no team this not team",
    };
    private static readonly string[] Lose_DemonRager =
    {
        "FUCKING TRASH TEAM", "I HOPE U ALL UNINSTALL",
        "WHY DO I QUEUE WITH BOTS", "MUTE ALL DODGE LOBBY",
        "REPORT EVERY TEAMMATE", "FUCK THIS GAME",
        "EVERY TEAM IS BOTS WTF", "KYS TEAM",
        "WHY DO I PLAY THIS GAME", "GAME IS DEAD WHEN I QUEUE WITH U",
    };
    private static readonly string[] Lose_SpeedTyper =
    {
        "team mid lmao", "sad ngl", "we threw bro",
        "no help xdd", "lost to bots ngl", "rage q lmao",
        "nooooo", "lmaoooo not us throwing again",
    };

    // Hearing — bot reacts to a sound source. {who} replaced with player name.
    private static readonly string[] Hear_Step =
    {
        "i hear steps {who}", "footsteps {who}", "{who} ur loud af",
        "stop walking", "{who} crouch better lol",
        "{who} u not silent", "i can hear u breathing",
        "loud af {who}", "ur steps gave u away",
        "shift before peeking", "im gonna prefire {who}",
        "{who} ur shoes too loud", "nice loud walk", "u walking ape",
    };
    private static readonly string[] Hear_Jump =
    {
        "{who} jumping like fortnite", "stop bhopping kid",
        "{who} can hear u jump", "bhop wont save u",
        "fortnite kid spotted", "{who} we hear ur jumps lmao",
        "tarzan over there", "rabbit lobby",
    };
    private static readonly string[] Hear_Scope =
    {
        "awper at {who}", "{who} scoping in", "snipe locked",
        "scope incoming", "{who} stop awping pls",
        "they have awp out", "scoped thru smoke?",
        "watch the awp angle", "awp {who} is on it",
    };
    private static readonly string[] Hear_Shot =
    {
        "shots fired", "loud bro", "echo location enabled",
        "{who} thinks im deaf", "tracers on me", "got tagged",
        "wide peek incoming", "audio gave it away",
    };

    private static readonly string[] FF_Sorry =
    {
        "sry", "sorry bro", "my bad", "mb", "mb mb", "oops sry",
        "fuck sry didnt see", "shit sry", "afk fix",
        "didnt see u", "mb thought u were enemy",
        "mb lag", "mb model glitch", "mb im blind",
        "sry thought thats him", "model swap glitched me",
        "lag spike sry", "mb random spray",
        "i thought u rotated already sry", "mb walked into spray",
    };
    private static readonly string[] FF_Annoyed =
    {
        "stop tking", "tking is reportable kid", "wtf u doing",
        "stop hitting me", "watch ur fire", "thats 2 in a row",
        "kid stop", "u throwing?", "u doing this on purpose?",
        "stop nading me", "stop spraying us", "watch ur lines fr",
        "thats my third hit dude", "swear if u hit me again",
    };
    private static readonly string[] FF_Rage =
    {
        "U FUCKING THROWING REPORT INC", "FUCKING TROLL ON OUR TEAM",
        "MUTE THIS TROLL", "kys troll", "this guy is throwing report him",
        "FUCK U TEAMMATE", "STOP TKING U RETARD",
        "im killing him next round watch", "REPORT FOR INTING",
        "U LOSE PRIME ACCESS BUDDY", "Im going to toxic this kid until he leaves",
        "muting this throwing rat", "u hitting me again ima frag u",
    };

    private static readonly string[] Plant_Normal =
    {
        "bomb planted", "anchor it", "we planted", "site is ours",
        "post up", "site locked", "ez plant", "kit needed",
        "watch retake", "stack on plant",
    };

    // When teammate keeps dying — mock chat from a survivor
    public static readonly string[] Mock_DyingTeammate =
    {
        "u died again",
        "{who} 0-{n} how",
        "{who} delete cs",
        "no kills again {who}",
        "{who} bro ur 0-3",
        "ur not the carry today {who}",
        "save next round {who} pls",
        "buy a brain instead of awp {who}",
        "{who} go practice deathmatch",
        "again {who}? rng main character",
    };

    // When teammate is being toxic — others rebuke
    public static readonly string[] Rebuke_Toxic =
    {
        "stfu",
        "shut up",
        "dude chill",
        "stop crying",
        "shut up bot",
        "{who} stfu",
        "ur 1-7 stfu",
        "mute this guy",
        "muting {who}",
        "{who} u made 2 kills sit",
        "imagine flaming when ur worse",
        "loudest player worst kd",
        "kid stop yapping",
        "this {who} guy actually crying lmao",
        "and {who} cooks the chat but starves on the field",
        "{who} chill we lose 2-3 rounds it happens",
        "u ok? need water?",
        "talk less play more",
    };

    // Mid-round disagreement — argue chain
    public static readonly string[] Argue_Disagree =
    {
        "no a is empty",
        "no b stop calling a",
        "im going b alone watch",
        "stop calling that wrong info",
        "info wrong they on a",
        "rotate already",
        "wtf u calling",
        "stop micro spamming",
        "i said a not b",
        "ur info is from 30s ago",
        "they pushing now stop calling default",
        "b is taken go away from a call",
    };

    // Teammate kill streak — talker reacts
    public static readonly string[] StreakHype =
    {
        "{who} hes locked in",
        "{who} ace?",
        "{who} on fire fr",
        "{who} pop off bestie",
        "free win {who} carrying",
        "give {who} the awp every round",
        "{who} hardware diff",
    };

    // ---------- end-of-round friendly "gg" ----------
    public static readonly string[] GG_Friendly =
    {
        "gg", "ggwp", "gg wp", "gg guys", "gg all", "gg lol", "good round",
        "nt team", "wp", "nicely played", "well played", "ggs", "ggez no offense",
        "gg fr", "ggs all", "rip me but gg", "good game", "gg hf",
        "respect", "respect ggwp", "love to see it gg",
    };
    // friendly nice-teammate idle
    public static readonly string[] Friendly_Nice =
    {
        "we got this", "nice round", "good rotate", "im on it",
        "rotating to help", "covering you", "ill flash u in",
        "trust the process", "we balling tonight", "good comms",
        "we'll get them next round", "head up team",
        "nice team play", "nice util", "nice nade",
        "im feeding u info ill be quiet now", "lets focus", "go go we got time",
        "nice clutch attempt", "u tried good job",
    };
    // ---------- AFK announcements ----------
    public static readonly string[] AFK_HeadsUp =
    {
        "afk sec", "brb", "be right back", "dog needs out 1 sec",
        "pizza arrived", "doorbell brb", "afk save plz", "phone call sry brb",
        "afk gonna piss", "wifi rebooting brb", "irl emergency 1 sec",
        "afk dont push w me", "save me brb", "leaving ill be back",
    };
    public static readonly string[] AFK_Flame =
    {
        "{who} afk wtf", "{who} stop afking", "this guy afk every round",
        "{who} not playing ban him", "afk teammate again wtf",
        "{who} go play tetris if u afk", "vote kick {who} hes afk",
        "{who} u gonna play or what",
    };
    // ---------- pause / freeze period idle banter ----------
    public static readonly string[] Pause_Idle =
    {
        "anyone else hungry", "yall using bluetooth on the awp",
        "i will 1v1 anyone in lobby", "what is everyone elo",
        "i lost 200 elo today", "this game is dying ngl",
        "im about to ragequit", "cs2 worst patch ever",
        "anyone playing dota tonight",
        "imagine playing this game in 2026",
        "csgo was better change my mind", "valve fix the game pls",
        "is anyone else sick of de_dust2", "id rather play tarkov",
        "yo what map is next", "vote mirage next",
        "vote inferno next", "vote nuke next",
        "i hate every map equally",
        "anyone trying to swap teams", "imagine teaming with me lol",
        "im hardstuck silver send help", "global elite btw",
        "lvl 10 faceit no cap",
        "i smurf btw", "yall play prime?", "everyone smurfing in NA",
        "is the awper crouched in spawn",
        "yo did u catch the major final",
        "spirit just won btw",
        "donk overrated change my mind",
        "zywoo carries every team easy",
        "niko on falcons washed",
        "i play with 30 ping btw",
        "y'all using rubber bands on m1",
        "ngl havent slept in 2 days",
        "school night gtg sleep soon",
        "wifi keeps dropping fml",
        "i miss csgo source 1",
        "gun sounds in cs2 are mid",
        "smoke nades op rn",
        "the new molly is broken",
        "anubis is mid map",
        "ancient is the worst map ever",
        "vertigo is the best map fight me",
        "im on a 10 game loss streak help",
        "im actually washed",
        "y'all doing anything fun this weekend",
        "cs is my therapy fr",
        "thinking of quitting cs and playing val",
        "valorant kids stay on valorant",
        "league players in cs lobbies stay losing",
        "i bet ill ace next round watch",
        "imagine pushing first all the time",
    };
    // ---------- bait / dumbass chat ----------
    public static readonly string[] Pause_Bait =
    {
        "your aim is mid btw {who}",
        "{who} u gonna throw again?",
        "look at me {who} im better than u",
        "{who} 0-3 still",
        "1v1 me {who}",
        "{who} prove u not silver right now",
        "skill issue {who}",
        "{who} where did u learn cs from youtube?",
        "imagine if u played good {who}",
        "{who} ur kd is hilarious",
    };
    // ---------- body-bump rage ----------
    public static readonly string[] BodyBlock_Rage =
    {
        "MOVE", "move bro", "MOVE BITCH", "stop blocking",
        "step aside dummy", "WTF MOVE", "u stuck or what",
        "im pushing move", "stop standing in door", "watch out",
        "wake up move", "GET OUT OF THE WAY",
    };
    public static readonly string[] BodyBlock_AfterTK =
    {
        "told u to move", "thats what u get for blocking",
        "shouldnt have stood there", "warned u",
        "no apology u were blocking", "move next time",
        "thats karma", "lesson learned hopefully",
    };
    // ---------- rage quit announcements ----------
    public static readonly string[] RageQuit_Outgoing =
    {
        "im out", "im quitting this game", "uninstalling",
        "yall keep losing without me", "done with this lobby",
        "leaving good luck losers", "im out gg",
        "this team is unplayable im leaving", "im disconnecting",
        "afk perma", "ragequit sry team", "cant play with bots",
    };
    // ---------- vote-kick reasoning ----------
    public static readonly string[] VoteKick_Reason =
    {
        "vote kick this guy",
        "vote kick {who}",
        "kick {who} hes throwing",
        "kick {who} hes griefing",
        "kick {who} tk",
        "votekick {who} pls",
        "im calling vote on {who}",
        "{who} needs to go",
        "yes vote kick him",
        "f1 kick him",
        "kick the troll",
    };
    public static readonly string[] VoteKick_Yes =
    {
        "yes f1", "f1 vote yes", "f1 kick", "f1", "vote yes",
        "kick him bye", "yes deserved",
    };
    public static readonly string[] VoteKick_No =
    {
        "f2", "vote no", "no leave him alone", "no f2",
        "f2 hes new",
    };
    // ---------- match flow misc ----------
    public static readonly string[] Defuse_Lines =
    {
        "defused", "ez defuse", "kit diff", "got the defuse",
        "defuse done", "saved the round defuser",
    };
    public static readonly string[] Eco_Save =
    {
        "save guys", "full save", "eco round", "next round buy",
        "force buy?", "save with deag", "drop me deag",
        "cant afford", "100 short on awp", "drop me main",
        "im poor someone drop", "no awp this round",
    };
    public static readonly string[] FF_PostBump_Victim =
    {
        "BRO U HIT ME",
        "DID U JUST SHOOT ME",
        "wtf was that",
        "{who} ur teammate dumbass",
        "stop tking",
        "i was BLOCKING u shoot me??",
        "thats reportable bro",
        "calm down i moved",
        "u literally killed me wtf",
        "DID U JUST DEAL DMG TO ME",
    };

    // Russian taunts — appended occasionally when target name has cyrillic
    private static readonly string[] RussianTaunts =
    {
        "fucking russian",
        "ruski go home",
        "vatnik aim",
        "u play from internet cafe?",
        "get out of NA servers",
        "tractor lobby",
        "enjoy ur 800 ping",
        "russian = trash confirmed",
        "go back to ru server",
        "bro plays from a bunker",
        "ruski on his last potato pc",
        "fucking ruski",
        "russia lobbies are wild",
        "1xbet add btw",
        "stop saying cyka",
    };

    // Coordination — head shake / no thx
    public static readonly string[] Coord_NoYouFirst =
    {
        "u first", "go u first", "after u", "no thx",
        "im not pushing first", "u peek first",
        "u die first this time", "im behind u",
        "no way im going there first", "i hold u peek",
    };
    public static readonly string[] Coord_HeadShake =
    {
        "no", "nope", "no fucking way", "im not going there",
        "death corridor", "ima rotate", "absolutely not",
        "lmao no", "hard pass", "i'd rather die anywhere else",
    };

    // ----------------------------------------------------------------------
    //  PUBLIC PICK API
    // ----------------------------------------------------------------------

    public static string PickKillLine(BotPersona p, string subject)        => Pick(p, subject, Kill_Normal,        Kill_GenZ,        Kill_KidRage,        Kill_ESL,        Kill_DemonRager,        Kill_SpeedTyper);
    public static string PickDeathLine(BotPersona p, string subject)       => Pick(p, subject, Death_Normal,       Death_GenZ,       Death_KidRage,       Death_ESL,       Death_DemonRager,       Death_SpeedTyper);
    public static string PickRoundStartLine(BotPersona p, string subject)  => Pick(p, subject, RoundStart_Normal,  RoundStart_GenZ,  RoundStart_KidRage,  RoundStart_ESL,  RoundStart_DemonRager,  RoundStart_SpeedTyper);
    public static string PickWinLine(BotPersona p, string subject)         => Pick(p, subject, Win_Normal,         Win_GenZ,         Win_KidRage,         Win_ESL,         Win_DemonRager,         Win_SpeedTyper);
    public static string PickLoseLine(BotPersona p, string subject)        => Pick(p, subject, Lose_Normal,        Lose_GenZ,        Lose_KidRage,        Lose_ESL,        Lose_DemonRager,        Lose_SpeedTyper);
    public static string PickPlantLine(BotPersona p, string subject)       => Pick(p, subject, Plant_Normal,       Plant_Normal,     Plant_Normal,        Plant_Normal,    Plant_Normal,           Plant_Normal);
    public static string PickBanterLine(BotPersona p, string subject)      => Pick(p, subject, RoundStart_Banter,  RoundStart_Banter,RoundStart_Banter,   RoundStart_Banter,RoundStart_Banter,     RoundStart_Banter);

    public static string PickHearLine(BotPersona p, string kind, string who, Random rng)
    {
        var pool = kind switch
        {
            "step"  => Hear_Step,
            "jump"  => Hear_Jump,
            "scope" => Hear_Scope,
            "shot"  => Hear_Shot,
            _ => Hear_Step,
        };
        var line = pool[rng.Next(pool.Length)].Replace("{who}", string.IsNullOrEmpty(who) ? "u" : who);
        if (p.TauntRussianTarget && rng.NextDouble() < 0.20)
            line = line + " " + RussianTaunts[rng.Next(RussianTaunts.Length)];
        return line;
    }

    public static string PickFFSorryLine(Random rng)    => FF_Sorry[rng.Next(FF_Sorry.Length)];
    public static string PickFFAnnoyedLine(Random rng)  => FF_Annoyed[rng.Next(FF_Annoyed.Length)];
    public static string PickFFRageLine(Random rng)     => FF_Rage[rng.Next(FF_Rage.Length)];

    public static string PickRebukeLine(string toxicTeammateName, Random rng)
    {
        var l = Rebuke_Toxic[rng.Next(Rebuke_Toxic.Length)];
        return l.Replace("{who}", toxicTeammateName);
    }

    public static string PickMockDyingLine(string deadName, int deathsCount, Random rng)
    {
        var l = Mock_DyingTeammate[rng.Next(Mock_DyingTeammate.Length)];
        return l.Replace("{who}", deadName).Replace("{n}", deathsCount.ToString());
    }

    public static string PickArgueLine(Random rng) => Argue_Disagree[rng.Next(Argue_Disagree.Length)];
    public static string PickStreakHype(string who, Random rng) => StreakHype[rng.Next(StreakHype.Length)].Replace("{who}", who);
    public static string PickCoordHeadShake(Random rng) => Coord_HeadShake[rng.Next(Coord_HeadShake.Length)];
    public static string PickCoordNoYouFirst(string who, Random rng) => Coord_NoYouFirst[rng.Next(Coord_NoYouFirst.Length)].Replace("{who}", who);

    public static string PickGGLine(Random rng)         => GG_Friendly[rng.Next(GG_Friendly.Length)];

    /// v0.15: spectator-ping fired by a bot the moment they die. Names the
    /// zone they died in so teammates know what to expect. {z} substituted
    /// with zone label. Mood-skewed.
    private static readonly string[] DeathPing_Neutral =
    {
        "{z}", "they {z}", "i died {z}", "lost {z}", "got tagged {z}",
        "watch {z}", "info {z}", "from {z}", "pushed me {z}",
    };
    private static readonly string[] DeathPing_Toxic =
    {
        "great trade huh", "where was the trade {z}", "0 trade {z}",
        "obviously they were {z}", "literally said {z}", "useless team {z}",
        "carrying corpses", "watched me die {z}",
    };
    public static string PickDeathPing(BotPersona p, string zone, Random rng)
    {
        var pool = p.Mood == Friendliness.Hostile ? DeathPing_Toxic : DeathPing_Neutral;
        return pool[rng.Next(pool.Length)].Replace("{z}", string.IsNullOrEmpty(zone) ? "site" : zone);
    }

    /// v0.17: bot ACK / dissent reply to a human teammate's strat call.
    /// kind: "rush" / "split" / "default" / "stack" / "eco" / "force" / "fast"
    private static readonly string[] StratAck_Friendly =
    {
        "k", "ok", "with u", "go", "im in", "lets go", "ill follow",
        "y", "rg", "rgr", "lets do it", "ok ill push", "alright",
    };
    private static readonly string[] StratAck_Hostile =
    {
        "no", "ill do my own thing", "trash call", "lol no", "k whatever",
        "nah", "ill solo lurk", "y bot", "nope", "no thx",
    };
    private static readonly string[] StratAck_NeutralRush =
    {
        "rushing", "go", "with u", "k", "im in", "rg", "go go",
    };
    private static readonly string[] StratAck_NeutralEco =
    {
        "save", "eco yeah", "k save", "saving", "rg",
    };
    private static readonly string[] StratAck_NeutralDefault =
    {
        "k slow", "ok default", "playing slow", "rg", "info first",
    };

    /// v0.20: drop-request line. {who} = ref-name of rich teammate.
    private static readonly string[] DropReq_Friendly =
    {
        "drop me pls {who}", "broke pls", "drop me {who}?", "buy me?",
        "im broke {who}", "drop pls", "drop me ak", "drop awp pls",
        "im 800 drop?", "anyone drop", "guys drop me",
    };
    private static readonly string[] DropReq_Hostile =
    {
        "drop me {who} or u bot", "drop or kicked", "drop me i carry",
        "useless if u dont drop", "drop", "drop now {who}",
    };
    private static readonly string[] DropReq_Neutral =
    {
        "drop me {who}", "drop pls", "im 1k drop?", "drop me ill carry",
        "have nothing", "broke", "drop pls if u can",
    };
    /// v0.22: rich bot's reply when a teammate asks for a drop.
    private static readonly string[] DropReply_Friendly =
    {
        "k", "ok", "after rd", "1s", "incoming", "wait", "1 sec",
        "after this round", "y", "ok ill drop", "after spawn",
    };
    private static readonly string[] DropReply_Hostile =
    {
        "buy ur own", "no", "earn it", "lol no", "broke not my problem",
        "save up", "ill drop after u prove it", "no",
    };
    private static readonly string[] DropReply_Neutral =
    {
        "after rd", "hmm", "1s", "ok", "after spawn", "if i alive", "k",
    };
    public static string PickDropReply(BotPersona p, Random rng)
    {
        var pool = p.Mood switch
        {
            Friendliness.Friendly => DropReply_Friendly,
            Friendliness.Hostile  => DropReply_Hostile,
            _                     => DropReply_Neutral,
        };
        return pool[rng.Next(pool.Length)];
    }

    public static string PickDropRequest(BotPersona p, string who, Random rng)
    {
        var pool = p.Mood switch
        {
            Friendliness.Hostile  => DropReq_Hostile,
            Friendliness.Friendly => DropReq_Friendly,
            _                     => DropReq_Neutral,
        };
        return pool[rng.Next(pool.Length)].Replace("{who}", string.IsNullOrEmpty(who) ? "u" : who);
    }

    /// v0.21: dead bot in spectator mode mocking a living teammate's play.
    /// Different from Mock_DyingTeammate which is living-mocks-dying.
    private static readonly string[] SpecMock_Toxic =
    {
        "wtf is this aim", "how do u miss that", "go {z} not where u going",
        "drop the smoke", "use ur nades????", "ofc u missed", "pls die already",
        "playing 1v5 in spec", "watching paint dry", "ull lose ur duel",
        "this guy is silver", "bot aim", "hes gonna die",
        "missed everything", "wide swing nice {who}", "lmao what",
    };
    private static readonly string[] SpecMock_Friendly =
    {
        "u got this", "nice positioning", "watch corner", "behind u",
        "1 left", "swing it", "info pls", "good luck",
    };
    private static readonly string[] SpecMock_Neutral =
    {
        "watch corner", "1 left {z}", "behind u", "info pls", "rotate",
        "play time", "they pushing {z}", "stay alive",
    };
    public static string PickSpecMock(BotPersona p, string targetRef, string zone, Random rng)
    {
        var pool = p.Mood switch
        {
            Friendliness.Hostile  => SpecMock_Toxic,
            Friendliness.Friendly => SpecMock_Friendly,
            _                     => SpecMock_Neutral,
        };
        return pool[rng.Next(pool.Length)]
            .Replace("{who}", string.IsNullOrEmpty(targetRef) ? "u" : targetRef)
            .Replace("{z}", string.IsNullOrEmpty(zone) ? "site" : zone);
    }

    public static string PickStratAck(BotPersona p, string kind, Random rng)
    {
        if (p.Mood == Friendliness.Hostile && rng.NextDouble() < 0.55)
            return StratAck_Hostile[rng.Next(StratAck_Hostile.Length)];
        if (p.Mood == Friendliness.Friendly)
            return StratAck_Friendly[rng.Next(StratAck_Friendly.Length)];
        // Neutral — kind-specific pools
        return kind switch
        {
            "rush" or "fast"   => StratAck_NeutralRush[rng.Next(StratAck_NeutralRush.Length)],
            "eco" or "save"    => StratAck_NeutralEco[rng.Next(StratAck_NeutralEco.Length)],
            "default" or "slow"=> StratAck_NeutralDefault[rng.Next(StratAck_NeutralDefault.Length)],
            _                  => StratAck_Friendly[rng.Next(StratAck_Friendly.Length)],
        };
    }
    public static string PickFriendlyLine(Random rng)   => Friendly_Nice[rng.Next(Friendly_Nice.Length)];
    public static string PickAFKHeadsUp(Random rng)     => AFK_HeadsUp[rng.Next(AFK_HeadsUp.Length)];
    public static string PickAFKFlame(string who, Random rng) => AFK_Flame[rng.Next(AFK_Flame.Length)].Replace("{who}", who);
    public static string PickPauseIdle(Random rng)      => Pause_Idle[rng.Next(Pause_Idle.Length)];
    public static string PickPauseBait(string who, Random rng) => Pause_Bait[rng.Next(Pause_Bait.Length)].Replace("{who}", string.IsNullOrEmpty(who) ? "u" : who);
    public static string PickBodyBlockRage(Random rng)  => BodyBlock_Rage[rng.Next(BodyBlock_Rage.Length)];
    public static string PickBodyBlockAfterTK(Random rng) => BodyBlock_AfterTK[rng.Next(BodyBlock_AfterTK.Length)];
    public static string PickRageQuit(Random rng)       => RageQuit_Outgoing[rng.Next(RageQuit_Outgoing.Length)];
    public static string PickVoteKickReason(string who, Random rng) => VoteKick_Reason[rng.Next(VoteKick_Reason.Length)].Replace("{who}", who);
    public static string PickVoteKickYes(Random rng)    => VoteKick_Yes[rng.Next(VoteKick_Yes.Length)];
    public static string PickVoteKickNo(Random rng)     => VoteKick_No[rng.Next(VoteKick_No.Length)];
    public static string PickDefuse(Random rng)         => Defuse_Lines[rng.Next(Defuse_Lines.Length)];
    public static string PickEcoSave(Random rng)        => Eco_Save[rng.Next(Eco_Save.Length)];
    public static string PickFFPostBumpVictim(string who, Random rng) => FF_PostBump_Victim[rng.Next(FF_PostBump_Victim.Length)].Replace("{who}", who);

    private static readonly string[] FirstBlood_Lines =
    {
        "first blood ezpz", "first blood", "ez first blood", "1st blood",
        "early kill ez", "got first", "took first kill ez",
    };
    private static readonly string[] OneTap_Lines =
    {
        "1tap", "tap", "ez 1tap", "tapped", "click click", "head clicked",
        "1 click ez", "tagged + tap", "AK 1tap", "ez tap btw",
    };
    private static readonly string[] Knife_Kill =
    {
        "knifed", "humiliated", "knife kill ez", "lol knife",
        "tasted steel", "ratio + knife", "got knifed lmao",
    };
    private static readonly string[] AwpKill =
    {
        "awp diff", "awp 1tap", "scope clicked", "ez awp",
        "noscope clean", "awp main btw", "free awp kill",
    };
    private static readonly string[] LowHP_Self =
    {
        "im 1hp watch out", "low hp here cover me",
        "5hp anchor i need a peek", "im paper",
        "i tagged for 90 hp left", "8hp save me",
        "im low hold a sec", "low health rotating slow",
    };
    private static readonly string[] Clutch_Survive =
    {
        "1v3 holding", "clutch up time", "stay alive watching me",
        "im in the 1v2", "trust me i got this",
        "1v4 i need info", "where they coming from",
    };
    /// Fires the SECOND a bot becomes last-man. Different from PickClutch
    /// which is for the post-win brag. Mood-aware split:
    private static readonly string[] PreClutch_Neutral =
    {
        "im 1v{n}", "1v{n} guys", "got it", "shut", "quiet", "trade me {ref}",
        "trying", "watch info", "where they at", "hold info pls",
    };
    private static readonly string[] PreClutch_Toxic =
    {
        "thx for nothing", "wow team", "nobody helped great", "0 trade",
        "carrying again", "trash team", "1v{n} solo as usual", "no trade you donut",
        "cool bait", "useless team",
    };
    private static readonly string[] PreClutch_Friendly =
    {
        "ill try", "1v{n} info pls", "stay quiet team", "watching me?",
        "pls trade", "rooting for u", "guys quiet plz", "1v{n} got this",
    };
    public static string PickPreClutch(BotPersona p, int oppCount, Random rng)
    {
        var pool = p.Mood switch
        {
            Friendliness.Friendly => PreClutch_Friendly,
            Friendliness.Hostile  => PreClutch_Toxic,
            _                     => PreClutch_Neutral,
        };
        return pool[rng.Next(pool.Length)].Replace("{n}", oppCount.ToString());
    }
    private static readonly string[] HumanChat_Reactions =
    {
        "ok",
        "lmao",
        "nobody asked",
        "k",
        "and?",
        "crying?",
        "skill issue",
        "stfu",
        "bro thinks anyones reading",
        "another paragraph wow",
        "L take",
        "real one",
        "based",
        "cope",
        "ratio",
    };

    public static string PickFirstBlood(Random rng)  => FirstBlood_Lines[rng.Next(FirstBlood_Lines.Length)];
    public static string PickOneTap(Random rng)      => OneTap_Lines[rng.Next(OneTap_Lines.Length)];
    public static string PickKnifeKill(Random rng)   => Knife_Kill[rng.Next(Knife_Kill.Length)];
    public static string PickAwpKill(Random rng)     => AwpKill[rng.Next(AwpKill.Length)];
    public static string PickLowHP(Random rng)       => LowHP_Self[rng.Next(LowHP_Self.Length)];
    public static string PickClutch(Random rng)      => Clutch_Survive[rng.Next(Clutch_Survive.Length)];
    public static string PickHumanChatReact(Random rng) => HumanChat_Reactions[rng.Next(HumanChat_Reactions.Length)];

    // ---------- Grudge / vendetta — random target hatred (Polish kurwa-style + generic spite) ----------
    private static readonly string[] Grudge_Hate =
    {
        "{who} kurwa",
        "kurwa {who} stfu",
        "ja pierdole {who}",
        "{who} kurwa noob",
        "{who} matka twoja",
        "kurwa team {who} idiot",
        "i hate {who} specifically",
        "ima ruin {who} this round",
        "{who} ur next on my list",
        "{who} ima TK u next round",
        "{who} stfu polish kid",
        "{who} bring me my drop or die",
        "{who} blocking again",
        "im going for {who} not enemy",
        "kurwa {who} idz spac",
        "polski kid {who} go cry",
        "{who} u shouldnt have queued",
        "{who} nade incoming",
        "{who} watch ur back fr",
        "i swear next round {who} dies first",
        "{who} 1v1 me right now",
        "{who} ill make ur game miserable",
        "kurwa {who} ja cie zabije",
        "{who} the second u peek im hitting u",
        "everyone except {who} can play",
        "kicking {who} after this round watch",
    };
    private static readonly string[] Grudge_NadeAccident =
    {
        "oops", "lag spike sry", "fingers slipped",
        "bad nade lineup mb", "thought it was smoke sry",
        "afk fix nade", "totally not on purpose",
    };
    public static string PickGrudgeHate(string who, Random rng) =>
        Grudge_Hate[rng.Next(Grudge_Hate.Length)].Replace("{who}", who);
    public static string PickGrudgeNadeExcuse(Random rng) =>
        Grudge_NadeAccident[rng.Next(Grudge_NadeAccident.Length)];

    private static readonly string[] Nade_Praise =
    {
        "nice nade", "nade was perf", "nade clutch", "good util",
        "smoke clutch", "molly diff", "flash setup ez",
    };
    private static readonly string[] Nade_Mock =
    {
        "{who} nice nade lmao",
        "{who} that smoke was ohio",
        "{who} ur molly went into space",
        "{who} the decoy did more than u",
        "{who} stop yeeting nades",
        "{who} youtube nade setups not for u",
        "{who} aim training ≠ nade training apparently",
        "{who} the wall doesnt count as a kill",
        "{who} the spawn isnt the target",
    };
    public static string PickNadePraise(Random rng) => Nade_Praise[rng.Next(Nade_Praise.Length)];
    public static string PickNadeMock(string who, Random rng) =>
        Nade_Mock[rng.Next(Nade_Mock.Length)].Replace("{who}", who);

    // ---------- 0.7 NEW POOLS ----------
    public static readonly string[] GLHF_Match =
    {
        "glhf", "gl team", "have fun guys", "gl boys", "glhf all",
        "have a good one", "gg in advance", "glhf lets cook",
        "gl gl", "good luck team", "glhf!", "have fun",
    };
    public static readonly string[] DeafMock =
    {
        "{who} doesnt have headphones lol",
        "{who} bro is deaf",
        "{who} where audio? speakers off?",
        "{who} ur volume on 0?",
        "{who} did u even hear that",
        "{who} headphones broken?",
        "{who} u playing on tv speakers",
        "{who} buy headphones bro",
        "i swear {who} plays muted",
        "{who} ears must be down",
    };
    public static readonly string[] Strat_Rush =
    {
        "rush a fast", "rush b 5 stack", "rush 5 a no util",
        "fast b lets go", "rush a thru palace", "rush b apps",
        "rush b tunnels go", "rush a long",
    };
    public static readonly string[] Strat_Shift =
    {
        "shift in slow", "silent walk default", "play default no aggro",
        "default with shift", "all of u shift", "no sound default",
    };
    public static readonly string[] Strat_Force =
    {
        "forcebuy this round", "force armor + smg", "force pistols",
        "force this one boys", "all-in force", "buy with what u got force",
    };
    public static readonly string[] Strat_Eco =
    {
        "save save save", "eco round full save", "drop me 1 weapon save rest",
        "save it boys next round full", "cant afford full eco",
        "save and stack",
    };
    public static readonly string[] Strat_Full =
    {
        "fullbuy go", "we got full util this round", "fullbuy + nades",
        "spec stuff full kit", "full kit play passive",
    };
    public static readonly string[] Strat_NotListening =
    {
        "{who} stop rushing alone wtf",
        "{who} we said save bro",
        "{who} u peeked first again",
        "{who} stay with us",
        "{who} thats not the call",
        "{who} listen to comms",
        "{who} u ego peeking again",
        "{who} stop running solo",
    };
    public static readonly string[] Spec_Mock =
    {
        "{who} 0-{n} watching from spec is painful",
        "{who} how do u miss that",
        "i died and im STILL not the worst on team {who}",
        "{who} cant believe what im watching",
        "{who} that aim is criminal",
        "i muted my game watching {who}",
        "{who} ur whiffing on stationary targets",
        "{who} u literally walked into spray",
        "watching {who} play makes me wanna uninstall",
        "{who} pls stop peeking everywhere",
    };
    public static readonly string[] HighTab_Flame =
    {
        "team is so bad i cant carry",
        "every round same shit",
        "im 1v9 vs the lobby",
        "team kd: -inf",
        "this lobby is a coinflip i lose every flip",
        "imagine queueing with bots fr",
        "y'all need to learn the game",
        "ur all dragging me down",
        "report ALL my teammates",
        "i hate every single one of u",
        "this is unwinnable",
    };
    // Position-name pool used by "low ..." callouts
    private static readonly Dictionary<string, string[]> Map_Zones = new()
    {
        ["de_dust2"]    = new[] { "long", "short", "mid", "tunnels", "b doors", "ct spawn", "t spawn", "pit", "goose", "site a", "site b" },
        ["de_mirage"]   = new[] { "a", "b", "mid", "connector", "apps", "underpass", "ramp", "palace", "ct spawn", "t spawn", "jungle", "site a", "site b" },
        ["de_inferno"]  = new[] { "banana", "apps", "long a", "short a", "mid", "ct spawn", "t spawn", "library", "pit", "site a", "site b" },
        ["de_overpass"] = new[] { "long a", "short", "monster", "bathrooms", "connector", "ct spawn", "t spawn", "site a", "site b", "playground" },
        ["de_nuke"]     = new[] { "outside", "ramp", "main", "lobby", "ct spawn", "t spawn", "vents", "heaven", "secret", "yard", "site a", "site b" },
        ["de_ancient"]  = new[] { "mid", "donut", "cave", "house", "ct spawn", "t spawn", "site a", "site b", "ramp" },
        ["de_anubis"]   = new[] { "mid", "connector", "alley", "stairs", "ct spawn", "t spawn", "site a", "site b", "water" },
        ["de_vertigo"]  = new[] { "mid", "ramp", "ct spawn", "t spawn", "stairs", "elevator", "site a", "site b" },
        ["de_train"]    = new[] { "mid", "ivy", "z connector", "ct spawn", "t spawn", "site a", "site b", "yard", "el" },
    };
    public static string PickZoneFor(string mapName, Random rng)
    {
        if (Map_Zones.TryGetValue(mapName, out var zones))
            return zones[rng.Next(zones.Length)];
        // generic fallback
        return new[] { "a", "b", "mid", "ct spawn", "t spawn" }[rng.Next(5)];
    }

    /// Canonicalize a callout line into a "zone family" key for dedup.
    /// "low A site" / "they A" / "rotate A" → "a". "long A" → "a" (collapsed).
    /// Returns null if no zone token recognized.
    public static string? ZoneKeyFor(string mapName, string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        if (!Map_Zones.TryGetValue(mapName, out var zones))
            zones = new[] { "a", "b", "mid", "ct spawn", "t spawn" };
        var lower = " " + line.ToLowerInvariant() + " ";
        foreach (var z in zones.OrderByDescending(s => s.Length))
        {
            var pad = " " + z + " ";
            if (lower.Contains(pad)) return CollapseFamily(z);
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bsite\s*a\b|\blong\s*a\b|\bshort\s*a\b|\bapps?\b|\bramp\b|\bbanana\b|\bpalace\b|\bgoose\b|\bpit\b|\blibrary\b")) return "a";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bsite\s*b\b|\blong\s*b\b|\btunnels?\b|\bunderpass\b|\bb doors\b|\bmonster\b|\bbathrooms\b")) return "b";
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bmid\b|\bconnector\b|\bvents?\b|\bz connector\b|\bjungle\b|\bdonut\b|\bcave\b|\bstairs\b|\belevator\b")) return "mid";
        return null;
    }

    private static string CollapseFamily(string zone)
    {
        var z = zone.ToLowerInvariant();
        if (z.EndsWith(" a") || z == "a" || z == "long a" || z == "short a" || z == "apps" || z == "ramp" || z == "banana" || z == "palace" || z == "goose" || z == "pit" || z == "long" || z == "short" || z == "library") return "a";
        if (z.EndsWith(" b") || z == "b" || z == "tunnels" || z == "underpass" || z == "b doors" || z == "monster" || z == "bathrooms") return "b";
        if (z == "mid" || z == "connector" || z == "vents" || z == "z connector" || z == "jungle" || z == "donut" || z == "cave" || z == "stairs" || z == "elevator") return "mid";
        return z;
    }

    // ----- Callout pools (v0.9.0) -----
    public static readonly string[] Callout_Smoke =
    {
        "smoke {z}", "smoked {z}", "they smoked {z}", "smoke off {z}", "{z} smoked", "wall {z}",
    };
    public static readonly string[] Callout_Molly =
    {
        "molly {z}", "incen {z}", "fire {z}", "{z} on fire", "burning {z}", "molly on {z}",
    };
    public static readonly string[] Callout_Flashed =
    {
        "flashed", "im flashed", "blind", "cant see", "FLASHED", "fully flashed", "flash flash",
    };
    public static readonly string[] Callout_Planted =
    {
        "PLANTED {z}", "they planted {z}", "bomb {z}", "plant {z}", "down {z} defuse",
    };
    public static readonly string[] Callout_DefuseCommit =
    {
        "going for defuse", "defusing", "kit on me", "on it", "defuse i need cover", "im defusing cover me",
    };
    public static readonly string[] Callout_TimeLow =
    {
        "TIME", "time", "10 sec", "no time", "we lost time", "timeeee",
    };
    public static readonly string[] Callout_OneShot =
    {
        "one shot {who}", "low {who}", "{who} low", "{who} 1hp", "low low low", "ONE SHOT", "shot {who}",
    };
    public static readonly string[] Callout_OneShotZone =
    {
        "one shot {z}", "low {z}", "{z} low", "shot {z}",
    };
    public static readonly string[] Callout_ShotsFired =
    {
        "shots {z}", "firing {z}", "they {z}", "contact {z}", "engaging {z}", "shooting {z}",
    };
    public static readonly string[] Callout_Footsteps =
    {
        "steps {z}", "i hear {z}", "someone {z}", "ppl {z}", "rotating {z}",
    };
    public static readonly string[] Callout_Echo =
    {
        "yeah i see him", "got him", "ye copy", "i see", "same", "+", "yep", "saw",
    };
    public static readonly string[] Callout_Question =
    {
        "where exactly", "where", "?", "where {z}", "exact?", "alive?", "still there?",
    };
    public static readonly string[] Callout_Rebuke =
    {
        "shut up bot", "we know", "stfu", "stop spamming", "we know bot", "yes obviously", "ok and?",
    };
    public static readonly string[] Callout_Trade =
    {
        "rotating", "coming", "om w", "on the way", "trading", "with you", "moving {z}",
    };

    public static string PickCallout(string[] pool, string mapName, string who, Random rng)
    {
        var z = PickZoneFor(mapName, rng);
        var line = pool[rng.Next(pool.Length)];
        return line.Replace("{z}", z).Replace("{who}", string.IsNullOrEmpty(who) ? "him" : who);
    }
    public static string PickCalloutFixed(string[] pool, string zone, string who, Random rng)
    {
        var line = pool[rng.Next(pool.Length)];
        return line.Replace("{z}", zone ?? "").Replace("{who}", string.IsNullOrEmpty(who) ? "him" : who);
    }

    public static string PickGLHF(Random rng) => GLHF_Match[rng.Next(GLHF_Match.Length)];
    public static string PickDeafMock(string who, Random rng) => DeafMock[rng.Next(DeafMock.Length)].Replace("{who}", who);
    public static string PickStratRush(Random rng) => Strat_Rush[rng.Next(Strat_Rush.Length)];
    public static string PickStratShift(Random rng) => Strat_Shift[rng.Next(Strat_Shift.Length)];
    public static string PickStratForce(Random rng) => Strat_Force[rng.Next(Strat_Force.Length)];
    public static string PickStratEco(Random rng) => Strat_Eco[rng.Next(Strat_Eco.Length)];
    public static string PickStratFull(Random rng) => Strat_Full[rng.Next(Strat_Full.Length)];
    public static string PickStratNotListening(string who, Random rng) => Strat_NotListening[rng.Next(Strat_NotListening.Length)].Replace("{who}", who);
    public static string PickSpecMock(string who, int deaths, Random rng) => Spec_Mock[rng.Next(Spec_Mock.Length)].Replace("{who}", who).Replace("{n}", deaths.ToString());
    public static string PickHighTabFlame(Random rng) => HighTab_Flame[rng.Next(HighTab_Flame.Length)];

    private static string Pick(BotPersona p, string subject,
        string[] norm, string[] genz, string[] kid, string[] esl, string[] rage, string[] speed)
    {
        var rng = new Random();
        // Choose a primary pool by style (with 20% fallback to Normal)
        string[] pool = p.Style switch
        {
            BotStyle.GenZ        => rng.NextDouble() < 0.20 ? norm : genz,
            BotStyle.KidRage     => rng.NextDouble() < 0.18 ? norm : kid,
            BotStyle.ESL         => rng.NextDouble() < 0.18 ? norm : esl,
            BotStyle.DemonRager  => rng.NextDouble() < 0.20 ? norm : rage,
            BotStyle.SpeedTyper  => rng.NextDouble() < 0.18 ? norm : speed,
            _                    => norm,
        };

        // De-dup against bot's recent line history. Try up to 5 times.
        string line = pool[rng.Next(pool.Length)];
        for (int tries = 0; tries < 5 && p.RecentLines.Contains(line); tries++)
            line = pool[rng.Next(pool.Length)];

        line = line.Replace("{who}", string.IsNullOrWhiteSpace(subject) ? "u" : subject);
        if (p.TauntRussianTarget && rng.NextDouble() < 0.22)
            line = line + " " + RussianTaunts[rng.Next(RussianTaunts.Length)];
        return line;
    }

    public static string MaybeMangle(string line, BotPersona p, Random rng)
    {
        if (rng.NextDouble() < p.CapsChance) line = line.ToUpperInvariant();

        // Speed typer extends trailing letter ("ez" → "ezzz")
        if (p.Style == BotStyle.SpeedTyper && rng.NextDouble() < 0.55 && line.Length > 2)
        {
            var c = line[^1];
            if (char.IsLetter(c)) line += new string(c, rng.Next(1, 4));
        }

        if (p.Style == BotStyle.DemonRager && rng.NextDouble() < 0.25)
            line += " " + Kill_DemonRager[rng.Next(Kill_DemonRager.Length)];

        if (rng.NextDouble() < p.TypoChance)
            line = InjectTypos(line, rng, p.Style);

        // record into recent history
        p.RecentLines.Enqueue(line);
        while (p.RecentLines.Count > BotPersona.RecentMax)
            p.RecentLines.Dequeue();
        return line;
    }

    private static string InjectTypos(string s, Random rng, BotStyle style)
    {
        var sb = new StringBuilder(s);
        int swaps = style switch
        {
            BotStyle.SpeedTyper => rng.Next(2, 5),
            BotStyle.ESL        => rng.Next(2, 4),
            _                   => rng.Next(1, 3),
        };
        for (int k = 0; k < swaps && sb.Length > 2; k++)
        {
            int op = rng.Next(4);
            int i = rng.Next(sb.Length);
            switch (op)
            {
                case 0:
                    if (i < sb.Length - 1 && char.IsLetter(sb[i]) && char.IsLetter(sb[i + 1]))
                        (sb[i], sb[i + 1]) = (sb[i + 1], sb[i]);
                    break;
                case 1: if (char.IsLetter(sb[i])) sb.Insert(i, sb[i]); break;
                case 2: if (sb.Length > 4 && char.IsLetter(sb[i])) sb.Remove(i, 1); break;
                case 3:
                    if (style == BotStyle.ESL || style == BotStyle.SpeedTyper)
                    {
                        var cur = sb.ToString();
                        cur = cur.Replace("the ", rng.Next(2) == 0 ? "te " : "da ");
                        cur = cur.Replace("you", rng.Next(3) switch { 0 => "u", 1 => "ju", _ => "you" });
                        cur = cur.Replace("your", rng.Next(2) == 0 ? "ur" : "yor");
                        cur = cur.Replace("ing ", rng.Next(2) == 0 ? "in " : "ng ");
                        sb.Clear(); sb.Append(cur);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    public static float ComputeTypingTime(string line, int wpm, Random rng)
    {
        int chars = string.IsNullOrEmpty(line) ? 1 : line.Length;
        float minutes = chars / 5f / Math.Max(20, wpm);
        float typingSec = minutes * 60f;
        float thinkSec = (float)(0.30 + rng.NextDouble() * 1.0);
        return thinkSec + typingSec;
    }
}
