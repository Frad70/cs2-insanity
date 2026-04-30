using CounterStrikeSharp.API;

namespace InsanityRevive;

/// <summary>
/// Per-bot match-flow decision engine. Encodes "tilt", "momentum",
/// "eco awareness", "risk profile" — values that color bot decisions
/// without overriding the engine bot AI directly.
///
/// Read-mostly: callers (InsanityRevive plugin) query DecisionEngine for
/// hints like "should this bot push aggressively" or "is this bot
/// currently saving" and decorate behavior accordingly.
///
/// v0.11: scaffolded but not yet wired. Parallel branches (callouts,
/// movement) integrate independently.
/// </summary>
public class DecisionEngine
{
    /// <summary>Risk preference 0..1. 0 = passive, 1 = reckless.</summary>
    public class BotMatchState
    {
        public int Slot;
        public int RoundsWonStreak;     // consecutive round wins on this bot's team
        public int RoundsLostStreak;    // consecutive round losses
        public int KillsThisMatch;
        public int DeathsThisMatch;
        public int HeadshotsThisMatch;
        public int TimesClutched;       // 1vN clutch attempts (won)
        public int TimesChoked;         // last man, lost
        public int FFTakenThisMatch;    // damage from teammates
        public int FFGivenThisMatch;    // damage to teammates
        public int FFKillsThisMatch;    // v0.19: separate counter for team-kill events
        public float Tilt;              // -1..+1; positive = pumped, negative = tilted
        public float Confidence;        // 0..1 (aim shake reduces if low)
        public bool ProbablyEcoing;     // current round bot is saving
        public float MatchTimeStarted;  // CurrentTime when first registered
    }

    private readonly Dictionary<int, BotMatchState> _states = new();

    public BotMatchState GetOrCreate(int slot)
    {
        if (!_states.TryGetValue(slot, out var s))
        {
            s = new BotMatchState { Slot = slot, MatchTimeStarted = Server.CurrentTime, Confidence = 0.6f };
            _states[slot] = s;
        }
        return s;
    }

    public bool TryGet(int slot, out BotMatchState s) => _states.TryGetValue(slot, out s!);

    public void Forget(int slot) => _states.Remove(slot);

    // ----------------------------------------------------------------------
    // Event ingestion
    // ----------------------------------------------------------------------

    /// <summary>Bot got a kill. Updates streaks, confidence, tilt.</summary>
    public void OnBotKill(int slot, bool wasHeadshot, bool wasTeammate)
    {
        var s = GetOrCreate(slot);
        // v0.19: FF kills still increment KillsThisMatch (matches scoreboard
        // semantics) but DON'T boost confidence and DO heavily tilt the killer.
        // Separate FFKillsThisMatch counter is read by IsChronicTeamKiller.
        s.KillsThisMatch++;
        if (wasHeadshot) s.HeadshotsThisMatch++;
        if (wasTeammate)
        {
            s.FFKillsThisMatch++;
            s.FFGivenThisMatch += 100;
            s.Tilt = MathF.Max(-1f, s.Tilt - 0.30f);
            return;
        }
        s.Confidence = MathF.Min(1f, s.Confidence + 0.04f + (wasHeadshot ? 0.04f : 0f));
        s.Tilt = MathF.Min(1f, s.Tilt + (wasHeadshot ? 0.10f : 0.05f));
    }

    /// <summary>v0.19: bot has 2+ FF kills this match — chronic team-killer.
    /// Used to gate vote-kick auto-fire from teammates.</summary>
    public bool IsChronicTeamKiller(int slot) =>
        _states.TryGetValue(slot, out var s) && s.FFKillsThisMatch >= 2;

    /// <summary>Bot died.</summary>
    public void OnBotDeath(int slot, bool diedToTeammate, bool dyingAsLastMan)
    {
        var s = GetOrCreate(slot);
        s.DeathsThisMatch++;
        if (diedToTeammate)
        {
            // FF death — significantly tilt-inducing (this is the user's social-chaos goal).
            s.Tilt = MathF.Max(-1f, s.Tilt - 0.25f);
            s.Confidence = MathF.Max(0.10f, s.Confidence - 0.05f);
        }
        else
        {
            s.Tilt = MathF.Max(-1f, s.Tilt - 0.06f);
            s.Confidence = MathF.Max(0.10f, s.Confidence - 0.03f);
        }
        if (dyingAsLastMan)
        {
            s.TimesChoked++;
            s.Tilt = MathF.Max(-1f, s.Tilt - 0.15f);
        }
    }

    /// <summary>Bot took FF damage but didn't die from it. Smaller tilt nudge.</summary>
    public void OnBotTookFF(int slot, int damage)
    {
        var s = GetOrCreate(slot);
        s.FFTakenThisMatch += damage;
        s.Tilt = MathF.Max(-1f, s.Tilt - damage * 0.004f);
    }

    /// <summary>Bot was on the team that won the round.</summary>
    public void OnRoundWonForBot(int slot)
    {
        var s = GetOrCreate(slot);
        s.RoundsWonStreak++;
        s.RoundsLostStreak = 0;
        s.Tilt = MathF.Min(1f, s.Tilt + 0.06f);
        // Streak amplifier — bots on a 4+ round streak get extra confident
        if (s.RoundsWonStreak >= 4)
            s.Confidence = MathF.Min(1f, s.Confidence + 0.05f);
    }

    public void OnRoundLostForBot(int slot)
    {
        var s = GetOrCreate(slot);
        s.RoundsLostStreak++;
        s.RoundsWonStreak = 0;
        s.Tilt = MathF.Max(-1f, s.Tilt - 0.05f);
        if (s.RoundsLostStreak >= 5)
            s.Confidence = MathF.Max(0.10f, s.Confidence - 0.05f);
    }

    public void OnClutchWon(int slot)
    {
        var s = GetOrCreate(slot);
        s.TimesClutched++;
        s.Confidence = MathF.Min(1f, s.Confidence + 0.15f);
        s.Tilt = MathF.Min(1f, s.Tilt + 0.30f);  // chad moment
    }

    /// <summary>Mark that bot is saving this round (don't push).</summary>
    public void MarkEcoing(int slot, bool isEcoing) => GetOrCreate(slot).ProbablyEcoing = isEcoing;

    public void OnNewRound(int slot)
    {
        var s = GetOrCreate(slot);
        // Tilt decays toward neutral each round
        s.Tilt *= 0.85f;
        s.Confidence = 0.55f * 1f + 0.45f * s.Confidence;  // pull toward 0.55
        s.ProbablyEcoing = false;
    }

    // ----------------------------------------------------------------------
    // Decision heuristics — read by InsanityRevive plugin
    // ----------------------------------------------------------------------

    /// <summary>0..1 — how aggressively this bot wants to push this round.</summary>
    public float PushPropensity(int slot, BotPersona persona)
    {
        var s = GetOrCreate(slot);
        float baseAggro = persona.Archetype switch
        {
            BotArchetype.Entry         => 0.85f,
            BotArchetype.AwperAggro    => 0.70f,
            BotArchetype.IGL           => 0.55f,
            BotArchetype.Lurker        => 0.40f,
            BotArchetype.Support       => 0.45f,
            BotArchetype.Anchor        => 0.20f,
            BotArchetype.AwperPassive  => 0.30f,
            BotArchetype.BaitOMatic    => 0.15f,
            BotArchetype.HeadshotOnly  => 0.50f,
            _                          => 0.50f,
        };
        // tilt pushes you to extremes (Russian-roulette-y tilted Entry plays riskier)
        float tilted = baseAggro + s.Tilt * 0.20f;
        // confidence pushes toward base aggro (low confidence regresses to safer)
        tilted = (1 - s.Confidence) * 0.30f + s.Confidence * tilted;
        // ecoing → suppress
        if (s.ProbablyEcoing) tilted *= 0.40f;
        return MathF.Min(1f, MathF.Max(0f, tilted));
    }

    /// <summary>0..1 — how spammy/talky this bot will be from THIS round forward.
    /// Pumped tilt + lots of FF given = high salt = lots of trash talk.</summary>
    public float ChatBoost(int slot, BotPersona persona)
    {
        var s = GetOrCreate(slot);
        float boost = 1.0f;
        if (s.Tilt > 0.40f) boost += 0.50f;        // chad arc
        if (s.Tilt < -0.40f) boost += 0.80f;       // tilted = even more vocal
        if (s.RoundsWonStreak >= 4) boost += 0.40f;
        if (s.TimesClutched > 0) boost += 0.35f;
        if (s.FFTakenThisMatch > 80) boost += 0.30f; // got hurt by team — sour
        return boost;
    }

    /// <summary>True when bot's stats suggest "playing hot" — fewer hesitations.</summary>
    public bool IsPlayingHot(int slot)
    {
        if (!_states.TryGetValue(slot, out var s)) return false;
        return s.Confidence > 0.80f && s.Tilt > 0.30f && s.RoundsWonStreak >= 2;
    }

    /// <summary>True when bot is on the verge of ragequit territory.</summary>
    public bool IsHardTilted(int slot)
    {
        if (!_states.TryGetValue(slot, out var s)) return false;
        return (s.Tilt < -0.55f) || (s.RoundsLostStreak >= 6) || (s.FFTakenThisMatch > 200);
    }

    /// <summary>True when bot is having a "chad" run — will be smug in chat.</summary>
    public bool IsOnChadStreak(int slot)
    {
        if (!_states.TryGetValue(slot, out var s)) return false;
        return s.Tilt > 0.50f && (s.HeadshotsThisMatch >= 3 || s.RoundsWonStreak >= 3);
    }
}
