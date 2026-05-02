using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

/// <summary>
/// Reveal Finale state machine (P/12, v0.6.0-beta).
///
/// Triggered by `!reveal` (admin chat) or `insanity_reveal` (rcon). NO
/// confirmation prompt, re-runnable indefinitely. Re-trigger during an
/// active reveal calls <see cref="CleanupReveal"/> first, then enters
/// Stage 0 fresh.
///
/// State machine:
///   Idle
///     ↓  Start()
///   Stage 0  pre-warning ~5s — random bot spams "1" 5× + sync impulse
///     ↓  Stage 1 entry at t=5s
///   Stage 1  knife rush — strip+knife, m_flLaggedMovementValue=1.4
///     ↓  threshold = min(stage2_time s, stage2_kills bots dead)
///   Stage 2  escalation — m249/negev, infinite ammo, perfect aim,
///                          slowmo 0.3 for 2s on human death
///     ↓  trigger = 0 living humans
///   Stage 3  cleanup — CleanupReveal + mp_restartgame 1 (if config'd)
///     ↓
///   Idle
///
/// Advisor-flagged risks (not yet probed empirically):
/// 1. Sync impulse via <c>AbsVelocity.Z=300</c>: bot AI may write velocity
///    every tick, overwriting our impulse. Fallback: drop sync, rely on
///    chat spam alone for Stage 0 punch.
/// 2. Perfect aim via <c>EyeAngles</c> write: tick-ordering vs bot AI's
///    own aim writes is unverified. May need to use post-bot-AI hook
///    (Listeners.OnTick fires once per tick — ordering relative to AI tbd).
/// 3. Stage 1 knife-only is leaky: bots pick up dropped guns. Tick guard
///    re-strips if active weapon != knife.
/// </summary>
public sealed class RevealController
{
    public enum RevealStage { Idle, Stage0, Stage1, Stage2, Stage3 }

    public RevealStage Stage { get; private set; } = RevealStage.Idle;

    private readonly FakeClientManager _mgr;
    private int _stageStartTick;
    private int _botsKilledThisReveal;
    private int _humansAtStart;          // for Stage 3 detection
    private bool _stage2Triggered;
    private readonly Random _rng = new();

    /// <summary>Bots whose loadout we've forced — used for Stage 1 weapon-lock + Stage 3 restore.</summary>
    private readonly Dictionary<int, BotCombatState> _combatState = new();

    /// <summary>
    /// Pre-reveal value of <c>mp_teammates_are_enemies</c> — captured
    /// at Stage 1 entry, restored exactly at CleanupReveal. v0.6.0.1
    /// forced this to 1, but the side-effect (bots target each other,
    /// fleet self-mulches in 30s) made it unworkable. v0.6.0.2 reverts
    /// to natural CT-vs-T combat — see Stage 1 doc for the design.
    /// </summary>
    private bool? _prevTeammatesAreEnemies;

    /// <summary>
    /// Pre-reveal value of <c>mp_solid_teammates</c>. Forced to 0 at
    /// Stage 1 entry so the bot swarm can pile through each other
    /// without collision (otherwise teleport-on-top-of-human stacks
    /// and ejects bots in random directions). Restored exactly at
    /// CleanupReveal.
    /// </summary>
    private bool? _prevSolidTeammates;

    private struct BotCombatState
    {
        public string ForcedWeapon;       // e.g. "weapon_knife", "weapon_m249"
        public RevealStage StageWhenSet;
    }

    public RevealController(FakeClientManager mgr) => _mgr = mgr;

    /// <summary>Admin entry. !reveal or insanity_reveal lands here.</summary>
    public void Start()
    {
        if (Stage != RevealStage.Idle)
        {
            Log.Info($"Reveal restart requested mid-reveal (was {Stage}) — running CleanupReveal first");
            CleanupReveal();
        }

        EnterStage0();
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 0 — pre-warning
    // ──────────────────────────────────────────────────────────────────
    private void EnterStage0()
    {
        Stage = RevealStage.Stage0;
        _stageStartTick = Server.TickCount;
        _botsKilledThisReveal = 0;
        _stage2Triggered = false;
        _humansAtStart = LivingHumansCount();

        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage0" }, { "humansAtStart", _humansAtStart },
            { "fleetSize", _mgr.All.Count } });

        var bots = _mgr.All.ToList();
        if (bots.Count == 0)
        {
            Log.Warn("Reveal Stage 0: no bots in fleet — aborting");
            CleanupReveal();
            return;
        }

        // Pick a random bot to spam "1" five times across 5 seconds.
        var spammer = bots[_rng.Next(bots.Count)];
        for (int i = 0; i < 5; i++)
        {
            int delaySec = i;
            Server.RunOnTick(Server.TickCount + delaySec * 64,
                () => SayAsBot(spammer, "1"));
        }

        // Sync jump impulse DROPPED in v0.6.0-beta. m_vecAbsVelocity is
        // a CBaseEntity field that CSSharp warns isn't networked — even
        // if the write succeeded server-side, clients wouldn't see the
        // visible jump. Without a working sync-jump effect, the bot AI
        // would just pop up imperceptibly. Chat spam alone signals weirdness
        // for Stage 0 (advisor's recommended fallback).
        // Reinstate in v0.6.1 once a clientside-visible-jump path is found
        // (candidates: write IN_JUMP via CCSPlayer_MovementServices schema,
        // teleport up via m_vecOrigin, or trigger via console alias).

        // At t=5s: enter Stage 1.
        Server.RunOnTick(Server.TickCount + 5 * 64, () => {
            if (Stage == RevealStage.Stage0) EnterStage1();
        });
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 1 — knife rush (swarm)
    // ──────────────────────────────────────────────────────────────────
    //
    // Design (v0.6.0.2-beta): natural CT-vs-T combat — all bots flipped
    // to the opposite team of the (first) living human, then teleported
    // to a tight cluster around the human centroid. Bot AI sees only
    // humans as enemies (default mp_teammates_are_enemies=0), so they
    // converge on humans without internal fighting. mp_solid_teammates=0
    // lets the swarm pile through each other without colliding.
    //
    // Sequence:
    //   1. Capture cvars (mp_teammates_are_enemies, mp_solid_teammates).
    //   2. Force defaults (=0) and mp_solid_teammates=0.
    //   3. Pre-flip all bots to opposite team via SwitchTeam().
    //   4. mp_restartgame 1 — clean round, all respawn at spawn points
    //      with new team affiliation honored.
    //   5. After 5-tick-second settle: detect human centroid, teleport
    //      bots to (centroid.X+offset, centroid.Y+offset, centroid.Z),
    //      then strip+knife+speed-boost.
    //
    // Edge case: zero living humans at swarm time → skip teleport, run
    // knife rush in default spawn positions (bots wander).
    private void EnterStage1()
    {
        Stage = RevealStage.Stage1;
        _stageStartTick = Server.TickCount;

        // (1) Capture cvars to restore at CleanupReveal.
        try {
            _prevTeammatesAreEnemies = ConVar.Find("mp_teammates_are_enemies")?.GetPrimitiveValue<bool>() ?? false;
        } catch { _prevTeammatesAreEnemies = false; }
        try {
            _prevSolidTeammates = ConVar.Find("mp_solid_teammates")?.GetPrimitiveValue<bool>() ?? true;
        } catch { _prevSolidTeammates = true; }

        // (2) Force cvars: default targeting (bots respect team), no
        //     teammate collision (swarm pile-up).
        Server.ExecuteCommand("mp_teammates_are_enemies 0");
        Server.ExecuteCommand("mp_solid_teammates 0");

        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage1" },
            { "prevTeammatesAreEnemies", _prevTeammatesAreEnemies },
            { "prevSolidTeammates", _prevSolidTeammates } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] reveal initiated");

        // (3) Determine bot target team = opposite of human's team. If
        //     no humans, default to T (2) — bots will be on T, no humans
        //     to fight, knife-rush plays out as wandering chaos.
        var humans = LivingHumanControllers();
        int humanTeam = humans.Count > 0 ? (int)humans[0].TeamNum : 3;  // default human=CT
        int botTeam = humanTeam == 2 ? 3 : 2;  // opposite

        // Pre-flip teams BEFORE mp_restartgame so respawn at restart uses
        // the new team's spawn points.
        foreach (var fc in _mgr.All) {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c != null && c.IsValid)
                    c.SwitchTeam((CsTeam)botTeam);
            } catch (Exception ex) { Log.Debug($"SwitchTeam slot={fc.Slot}: {ex.Message}"); }
        }

        // (4) Clean round restart for fresh respawn state.
        Server.ExecuteCommand("mp_restartgame 1");

        // (5) After respawn settles: teleport swarm to human centroid,
        //     then apply knife rush. 5 sec covers the 1-sec mp_restart
        //     delay + 1-sec round restart processing + 3-sec respawn buffer.
        Server.RunOnTick(Server.TickCount + 64 * 5, () => {
            if (Stage != RevealStage.Stage1) return;  // re-trigger guarded
            DeploySwarmAndKnifeRush();
        });
    }

    private void DeploySwarmAndKnifeRush()
    {
        var humansNow = LivingHumanControllers();
        Vector? centroid = humansNow.Count > 0 ? ComputeCentroid(humansNow) : null;

        var bots = _mgr.All.ToList();
        for (int i = 0; i < bots.Count; i++) {
            var fc = bots[i];
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid) continue;
                if (pawn.LifeState != 0) continue;  // dead bot — skip swarm-tp

                // Teleport with small stagger so bots are clustered, not
                // stacked at a single point. mp_solid_teammates=0 allows
                // overlap, but visual variation looks better. Stagger
                // pattern: 4-wide rows, ±1.5 unit spread.
                if (centroid != null) {
                    float dx = (i % 4) - 1.5f;
                    float dy = (i / 4) - 1.0f;
                    var pos = new Vector(centroid.X + dx, centroid.Y + dy, centroid.Z);
                    pawn.Teleport(pos, pawn.AbsRotation, new Vector(0, 0, 0));
                }

                ApplyKnifeRush(fc);
            } catch (Exception ex) { Log.Error($"DeploySwarm slot={fc.Slot}: {ex.Message}"); }
        }
    }

    private List<CCSPlayerController> LivingHumanControllers()
    {
        var list = new List<CCSPlayerController>();
        foreach (var c in Utilities.GetPlayers()) {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (_mgr.FindBySlot((int)c.Slot) != null) continue;  // managed bot
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.LifeState != 0) continue;
            list.Add(c);
        }
        return list;
    }

    private static Vector ComputeCentroid(List<CCSPlayerController> humans)
    {
        float sx = 0, sy = 0, sz = 0;
        int n = 0;
        foreach (var h in humans) {
            var p = h.PlayerPawn?.Value;
            if (p?.AbsOrigin == null) continue;
            sx += p.AbsOrigin.X; sy += p.AbsOrigin.Y; sz += p.AbsOrigin.Z;
            n++;
        }
        return n == 0 ? new Vector(0, 0, 0) : new Vector(sx / n, sy / n, sz / n);
    }

    private void ApplyKnifeRush(FakeClient fc)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(fc.Slot);
            if (c == null || !c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            StripAllWeapons(c);
            c.GiveNamedItem("weapon_knife");

            // Speed boost via dynamic schema setter.
            Schema.SetSchemaValue<float>(pawn.Handle, "CCSPlayerPawn",
                "m_flVelocityModifier", 1.4f);
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");

            _combatState[fc.Slot] = new BotCombatState {
                ForcedWeapon = "weapon_knife", StageWhenSet = RevealStage.Stage1 };
        } catch (Exception ex) { Log.Error($"ApplyKnifeRush slot={fc.Slot}: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 2 — escalation
    // ──────────────────────────────────────────────────────────────────
    //
    // Design note (v0.6.0-beta): mid-round weapon swap (strip + give m249)
    // crashed the server in initial smoke tests — engine has live
    // references to in-flight weapons that StripAllWeapons invalidates.
    // Workaround: trigger `mp_restartgame 1` at Stage 2 entry (clean round
    // state, all weapons reset), THEN give m249/negev to bots in the
    // fresh round. Side benefit: respawns the human as well, which is
    // narratively appropriate ("the round was over but the bots still
    // showed up with m249s" — feels intentional).
    private void EnterStage2()
    {
        if (_stage2Triggered) return;
        _stage2Triggered = true;
        Stage = RevealStage.Stage2;
        _stageStartTick = Server.TickCount;
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage2" }, { "kills", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] STAGE 2 — AIM ASSIST ENGAGED");

        // Round restart for clean weapon state. mp_restartgame N takes
        // N seconds to actually fire — wait 4s (1s for restart command +
        // 1s for round restart processing + 2s buffer for respawn frames
        // and CCSPlayerPawn re-init) before giving m249s. Earlier 2-tick
        // delay raced with respawn machinery and crashed the server.
        Server.ExecuteCommand("mp_restartgame 1");
        Server.RunOnTick(Server.TickCount + 64 * 4, () => {
            // Re-check stage in case CleanupReveal raced (re-trigger).
            if (Stage != RevealStage.Stage2) return;
            foreach (var fc in _mgr.All) ApplyM249Rush(fc);
        });
    }

    private void ApplyM249Rush(FakeClient fc)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(fc.Slot);
            if (c == null || !c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;
            if (pawn.LifeState != 0) return;  // dead — wait for next round

            // Don't strip — fresh round means bots only have default
            // pistol. Just give the heavy. Engine handles slot management.
            var weapon = _rng.Next(2) == 0 ? "weapon_m249" : "weapon_negev";
            c.GiveNamedItem(weapon);

            _combatState[fc.Slot] = new BotCombatState {
                ForcedWeapon = weapon, StageWhenSet = RevealStage.Stage2 };
        } catch (Exception ex) { Log.Error($"ApplyM249Rush slot={fc.Slot}: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 3 — cleanup
    // ──────────────────────────────────────────────────────────────────
    private void EnterStage3()
    {
        Stage = RevealStage.Stage3;
        var stageDurationTicks = Server.TickCount - _stageStartTick;
        _mgr.Telemetry.Write("reveal_stage_enter", new Dictionary<string, object?> {
            { "stage", "Stage3" }, { "totalKills", _botsKilledThisReveal } });

        Server.PrintToChatAll($" {ChatColors.DarkRed}[INSANITY] reveal complete");
        CleanupReveal();

        if (_mgr.Config.RevealAutoRestart) {
            // mp_restartgame 1 revives killed humans for next !reveal.
            // 2-tick delay so chat message renders before round flash.
            Server.RunOnTick(Server.TickCount + 2,
                () => Server.ExecuteCommand("mp_restartgame 1"));
        }
    }

    /// <summary>
    /// Roll back ALL per-stage live overrides — host_timescale, weapon
    /// locks, speed multipliers, combat state. Idempotent. Called:
    /// 1. As FIRST step of Start() if reveal was already active;
    /// 2. From EnterStage3() on natural end.
    /// </summary>
    public void CleanupReveal()
    {
        try {
            Server.ExecuteCommand("host_timescale 1.0");
            // Restore captured cvars. null = never captured (Stage 0 or
            // Idle re-trigger before ever reaching Stage 1) — leave cvar
            // alone.
            if (_prevTeammatesAreEnemies.HasValue) {
                Server.ExecuteCommand($"mp_teammates_are_enemies {(_prevTeammatesAreEnemies.Value ? 1 : 0)}");
                _prevTeammatesAreEnemies = null;
            }
            if (_prevSolidTeammates.HasValue) {
                Server.ExecuteCommand($"mp_solid_teammates {(_prevSolidTeammates.Value ? 1 : 0)}");
                _prevSolidTeammates = null;
            }
            foreach (var fc in _mgr.All) RestoreNormalLoadout(fc);
            _combatState.Clear();
        } catch (Exception ex) { Log.Error($"CleanupReveal: {ex.Message}"); }
        Stage = RevealStage.Idle;
        _mgr.Telemetry.Write("reveal_cleanup", new Dictionary<string, object?> {
            { "totalKills", _botsKilledThisReveal } });
        _botsKilledThisReveal = 0;
    }

    private void RestoreNormalLoadout(FakeClient fc)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(fc.Slot);
            if (c == null || !c.IsValid) return;
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) return;

            StripAllWeapons(c);
            // Vanilla baseline: a pistol + rifle for the team. Bot AI
            // will pick up dropped weapons if available; this just gives
            // them something to work with.
            c.GiveNamedItem("weapon_glock");

            // Reset speed multiplier.
            Schema.SetSchemaValue<float>(pawn.Handle, "CCSPlayerPawn",
                "m_flVelocityModifier", 1.0f);
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_flVelocityModifier");
        } catch (Exception ex) { Log.Error($"RestoreNormalLoadout slot={fc.Slot}: {ex.Message}"); }
    }

    // ──────────────────────────────────────────────────────────────────
    // Tick logic — drives stage transitions + per-tick overrides
    // ──────────────────────────────────────────────────────────────────
    public void OnTick()
    {
        if (Stage == RevealStage.Idle) return;

        switch (Stage)
        {
            case RevealStage.Stage1: TickStage1(); break;
            case RevealStage.Stage2: TickStage2(); break;
        }

        // Stage 3 trigger: 0 living humans.
        if (Stage == RevealStage.Stage1 || Stage == RevealStage.Stage2)
        {
            if (LivingHumansCount() == 0 && _humansAtStart > 0) EnterStage3();
        }
    }

    private void TickStage1()
    {
        // Stage 2 transition condition.
        var elapsedTicks = Server.TickCount - _stageStartTick;
        var elapsedSec = elapsedTicks / 64;
        var killThreshold = _mgr.Config.Stage2Kills > 0
            ? _mgr.Config.Stage2Kills
            : Math.Max(1, (_mgr.Config.FleetSize + 1) / 2);
        if (elapsedSec >= _mgr.Config.Stage2TimeSeconds || _botsKilledThisReveal >= killThreshold)
        {
            EnterStage2();
            return;
        }

        // Weapon-lock guard (advisor flag): if a knife-rush bot has
        // picked up a dropped gun, strip and re-give knife.
        foreach (var fc in _mgr.All)
        {
            if (!_combatState.TryGetValue(fc.Slot, out var cs)) continue;
            if (cs.StageWhenSet != RevealStage.Stage1) continue;
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var active = c.PlayerPawn?.Value?.WeaponServices?.ActiveWeapon?.Value;
                if (active == null) continue;
                if (active.DesignerName != "weapon_knife")
                {
                    StripAllWeapons(c);
                    c.GiveNamedItem("weapon_knife");
                }
            } catch { }
        }
    }

    /// <summary>
    /// Stage 2 max duration before auto-cleanup (handles "no humans"
    /// edge case — without humans, the natural Stage-3 trigger never
    /// fires). Generous: lets the m249 spectacle play out.
    /// </summary>
    private const int Stage2MaxDurationSec = 60;

    private void TickStage2()
    {
        // ONLY a timeout check — no per-tick weapon overrides. Earlier
        // versions wrote Clip1/AccuracyPenalty per tick, which raced
        // with mp_restartgame's respawn machinery and crashed the server
        // (smoke run 2026-05-02). Bots have enough ammo from the m249's
        // native 100-round mag + reserve for a 60-second finale.
        var elapsedSec = (Server.TickCount - _stageStartTick) / 64;
        if (elapsedSec >= Stage2MaxDurationSec) EnterStage3();
    }

    // ──────────────────────────────────────────────────────────────────
    // Event hooks
    // ──────────────────────────────────────────────────────────────────
    public void OnPlayerDeath(int victimSlot, bool victimIsBot)
    {
        if (Stage == RevealStage.Idle) return;
        if (victimIsBot)
        {
            _botsKilledThisReveal++;
            return;
        }
        // Human died — slowmo death cam (Stage 2 only).
        if (Stage == RevealStage.Stage2)
        {
            Server.ExecuteCommand("host_timescale 0.3");
            Server.RunOnTick(Server.TickCount + (int)(2 * 64 * 0.3),
                () => Server.ExecuteCommand("host_timescale 1.0"));
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────
    private static void StripAllWeapons(CCSPlayerController c)
    {
        var pawn = c.PlayerPawn?.Value;
        var weapons = pawn?.WeaponServices?.MyWeapons;
        if (weapons == null) return;
        // Iterate copy — RemoveItemByDesignerName mutates the collection.
        var names = weapons
            .Where(h => h.Value != null)
            .Select(h => h.Value!.DesignerName)
            .ToList();
        foreach (var name in names)
        {
            try { c.RemoveItemByDesignerName(name); } catch { }
        }
    }

    /// <summary>
    /// Count living humans on the server. CRITICAL: cannot rely on
    /// <c>c.IsBot</c> or <c>c.AuthorizedSteamID</c> — InsanityHider has
    /// flipped m_bFakePlayer=0 for managed bots, and they carry synthetic
    /// SteamIDs. Both checks would mis-classify our bots as humans.
    /// Source-of-truth: <see cref="FakeClientManager.FindBySlot"/> —
    /// if a slot has a managed FakeClient entry, it's our bot, not human.
    /// </summary>
    private int LivingHumansCount()
    {
        int n = 0;
        foreach (var c in Utilities.GetPlayers())
        {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (_mgr.FindBySlot((int)c.Slot) != null) continue;  // managed bot
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.LifeState != 0) continue;  // 0 = LIFE_ALIVE
            n++;
        }
        return n;
    }

    /// <summary>
    /// Print a chat line that LOOKS like it came from the bot.
    /// MVP implementation: server-prefixed message formatted with the
    /// bot name + "1". UserMessage SayText2 broadcast for fully-faked
    /// chat is a follow-up; works well enough for v0.6.0-beta.
    /// </summary>
    private static void SayAsBot(FakeClient fc, string text)
    {
        try {
            // Format mimics in-game chat: " bot_name : text"
            // The leading space is what CSSharp PrintToChatAll outputs.
            Server.PrintToChatAll($" {fc.Name}{ChatColors.Default} : {text}");
        } catch (Exception ex) { Log.Debug($"SayAsBot: {ex.Message}"); }
    }
}
