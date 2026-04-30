using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

/// <summary>
/// Detects clutch situations (1vN: one of our team alive vs N opponents) and
/// reports flags + behavior modifiers. The host plugin queries IsClutching()
/// and decorates aim/movement with longer pauses, more deliberate peeks, and
/// suppressed callouts (real players go silent in clutches to listen).
///
/// v0.11 — scaffold. Read-only model; no side effects on game state.
/// </summary>
public class ClutchBehavior
{
    /// <summary>Snapshot of the clutch state for one bot.</summary>
    public class ClutchState
    {
        public int Slot;
        public bool IsLastMan;          // true if our team has 1 alive (this bot) and opponents > 0
        public int OpponentsAlive;      // number of opponents currently alive
        public float ClutchStartedAt;   // CurrentTime when bot became last man
        public bool QuietMode;          // true while bot is "listening" — suppress callouts
    }

    private readonly Dictionary<int, ClutchState> _state = new();

    /// <summary>Run on every Tick / OnPlayerDeath. Updates clutch state for all alive bots.</summary>
    public void Refresh()
    {
        var aliveByTeam = new Dictionary<CsTeam, List<CCSPlayerController>>();
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid) continue;
            var pp = p.PlayerPawn?.Value;
            if (pp == null || pp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            if (p.Team <= CsTeam.Spectator) continue;
            if (!aliveByTeam.TryGetValue(p.Team, out var list))
                aliveByTeam[p.Team] = list = new List<CCSPlayerController>();
            list.Add(p);
        }

        var t = aliveByTeam.GetValueOrDefault(CsTeam.Terrorist, new());
        var ct = aliveByTeam.GetValueOrDefault(CsTeam.CounterTerrorist, new());
        UpdateOneTeam(t, ct.Count);
        UpdateOneTeam(ct, t.Count);
    }

    private void UpdateOneTeam(List<CCSPlayerController> team, int oppCount)
    {
        bool isClutch = team.Count == 1 && oppCount > 0;
        foreach (var p in team)
        {
            if (!p.IsBot) continue;
            if (!_state.TryGetValue(p.Slot, out var st))
                _state[p.Slot] = st = new ClutchState { Slot = p.Slot };
            if (isClutch && !st.IsLastMan)
            {
                st.ClutchStartedAt = Server.CurrentTime;
                st.IsLastMan = true;
                st.QuietMode = true;
            }
            else if (!isClutch && st.IsLastMan)
            {
                // Clutch ended (death OR rescue by teammate respawning, etc.)
                st.IsLastMan = false;
                st.QuietMode = false;
            }
            st.OpponentsAlive = isClutch ? oppCount : 0;
        }
    }

    public bool IsClutching(int slot) =>
        _state.TryGetValue(slot, out var s) && s.IsLastMan;

    public bool ShouldStayQuiet(int slot) =>
        _state.TryGetValue(slot, out var s) && s.QuietMode;

    public ClutchState? Get(int slot) =>
        _state.TryGetValue(slot, out var s) ? s : null;

    /// <summary>Called when bot wins the clutch.</summary>
    public void Resolve(int slot, bool won)
    {
        if (!_state.TryGetValue(slot, out var s)) return;
        s.IsLastMan = false;
        s.QuietMode = false;
    }

    public void Forget(int slot) => _state.Remove(slot);

    // ----------------------------------------------------------------------
    // Behavior modifiers — the host plugin reads these to decorate ticks
    // ----------------------------------------------------------------------

    /// <summary>How much should this bot SLOW DOWN their aim/decision-making?
    /// Real players in 1v3+ clutches play noticeably slower (anti-greed).
    /// Returns multiplier: 1.0 = normal, 1.5 = much slower decisions.</summary>
    public float DecisionSlowdown(int slot)
    {
        if (!_state.TryGetValue(slot, out var s) || !s.IsLastMan) return 1.0f;
        return s.OpponentsAlive switch
        {
            1 => 1.10f,
            2 => 1.30f,
            >= 3 => 1.55f,
            _ => 1.0f,
        };
    }

    /// <summary>Multiplier on chat probability while clutching. Real players
    /// drop their chat to ~10% — they're listening for footsteps.</summary>
    public float ChatSuppression(int slot)
    {
        if (!_state.TryGetValue(slot, out var s) || !s.IsLastMan) return 1.0f;
        return 0.10f;
    }

    /// <summary>How long has bot been clutching (seconds)?</summary>
    public float ClutchDuration(int slot)
    {
        if (!_state.TryGetValue(slot, out var s) || !s.IsLastMan) return 0f;
        return Server.CurrentTime - s.ClutchStartedAt;
    }
}
