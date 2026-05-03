using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
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

    /// <summary>
    /// Consecutive ticks where LivingHumansCount() returned 0 during
    /// Stage 1+2. Stage 3 only fires after this exceeds
    /// <see cref="ZeroHumansDampenTicks"/> — rides out respawn flicker
    /// (mp_restartgame briefly leaves the human's pawn in transient
    /// LifeState != 0 state, which would false-trigger Stage 3).
    /// </summary>
    private int _zeroHumansTickCount;
    private const int ZeroHumansDampenTicks = 64;  // 1 sec @ 64 Hz

    /// <summary>Bots whose loadout we've forced — used for Stage 1 weapon-lock + Stage 3 restore.</summary>
    private readonly Dictionary<int, BotCombatState> _combatState = new();

    /// <summary>
    /// Pre-reveal team membership per bot slot. Captured at Stage 1
    /// entry BEFORE any team flip; restored at CleanupReveal so the
    /// fleet returns to whatever T/CT distribution it had before reveal.
    /// Bots that get bounced to spectator due to team-cap overflow also
    /// have their original team recorded here for clean restore.
    /// </summary>
    private readonly Dictionary<int, int> _botPrevTeams = new();

    /// <summary>
    /// CS2 competitive 5v5 team cap. Per-team max, including humans.
    /// Hardcoded for v0.6.0.5 — if `+game_alias` ever changes from
    /// competitive to deathmatch/etc, revisit. Bots beyond this cap
    /// get sent to spectator at Stage 1 entry; they re-join their
    /// original team at CleanupReveal.
    /// </summary>
    private const int TeamCap = 5;

    /// <summary>
    /// Stage 1 minimum duration. Hard gate before Stage 2 can fire,
    /// regardless of how many bots have died. Prevents pathologically
    /// short Stage 1 (e.g. 4 bots die in 10 sec → Stage 2 immediately,
    /// felt rushed). User-spec v0.6.0.5: 30 sec minimum.
    /// </summary>
    private const int Stage1MinDurationSec = 30;

    /// <summary>
    /// Stage 1 maximum duration. Hard cap; Stage 2 fires regardless of
    /// kill count once this elapses. Prevents Stage 1 dragging out if
    /// bots can't reach humans (open map, human in safe spot).
    /// </summary>
    private const int Stage1MaxDurationSec = 60;

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

        // Each bot fires 15 "1" lines with INDEPENDENT random delay
        // 0.2-0.5 sec between consecutive messages. Per-bot total span
        // ≈ 3-7.5 sec; across 8 bots staggering naturally → ~120 chat
        // events spread over 5-8 seconds (overflows Stage 0 into early
        // Stage 1, which is intentional — the chat-flood masks the
        // moment of swarm-teleport so it feels like the chaos started
        // mid-sentence). Each message via UserMessage SayText2 broadcast.
        const int SpamMessagesPerBot = 15;
        foreach (var fc in _mgr.All)
        {
            var capturedFc = fc;
            int cumulativeTicks = 0;
            for (int j = 0; j < SpamMessagesPerBot; j++)
            {
                int delayTicks = cumulativeTicks;
                Server.RunOnTick(Server.TickCount + delayTicks,
                    () => SayAsBot(capturedFc, "1"));
                // Roll next delay 0.2-0.5 sec uniform.
                double sec = 0.2 + _rng.NextDouble() * 0.3;
                cumulativeTicks += (int)(sec * 64);
            }
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

        // (4) Capture each bot's PRE-REVEAL team. Don't SwitchTeam yet —
        //     v0.6.0.6 fix: SwitchTeam BEFORE mp_restartgame raced with the
        //     restart's own team-rebalance logic, leaving ~half the bots on
        //     their original team and the rest in spectator-but-mid-respawn
        //     limbo. New flow: capture teams now, mp_restartgame, then 1
        //     sec later do team flip in a clean round state.
        _botPrevTeams.Clear();
        foreach (var fc in _mgr.All) {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                int prevTeam = (int)c.TeamNum;
                if (prevTeam >= 2) _botPrevTeams[fc.Slot] = prevTeam;
            } catch (Exception ex) { Log.Debug($"capture team slot={fc.Slot}: {ex.Message}"); }
        }

        // (5) Clean round restart for fresh respawn state.
        Server.ExecuteCommand("mp_restartgame 1");

        // (6) +1.5s after restart: do team flip with cap awareness and
        //     belt-and-suspenders schema-write fallback.
        Server.RunOnTick(Server.TickCount + (int)(64 * 1.5), () => {
            if (Stage != RevealStage.Stage1) return;
            FlipTeamsWithCap(botTeam);
        });

        // (7) +5s after restart: teleport swarm to human centroid, then
        //     apply knife rush. Allows team flip to settle (3.5s buffer
        //     after FlipTeams).
        Server.RunOnTick(Server.TickCount + 64 * 5, () => {
            if (Stage != RevealStage.Stage1) return;
            DeploySwarmAndKnifeRush();
        });
    }

    /// <summary>
    /// Move all bots to <paramref name="botTeam"/> until cap is reached,
    /// rest to spectator.
    ///
    /// HISTORY: v0.6.0.6 attempted a Schema.SetSchemaValue&lt;byte&gt; +
    /// SetStateChanged fallback for m_iTeamNum when SwitchTeam appeared to
    /// fail verification. CSSharp warned "Field CCSPlayerController:
    /// m_iTeamNum is not networked, but SetStateChanged was called on it"
    /// and the server CRASHED on the next tick (dump 21:45:24). m_iTeamNum
    /// IS server-state but writing it via the schema bypass path corrupts
    /// engine team-counter accounting. Reverted to plain SwitchTeam +
    /// log-only verification for diagnostics. If a switch fails, accept
    /// it — better an unflipped bot than a crashed server.
    /// </summary>
    private void FlipTeamsWithCap(int botTeam)
    {
        var humans = LivingHumanControllers();
        int humansOnTargetTeam = humans.Count(h => (int)h.TeamNum == botTeam);
        int availableSlots = Math.Max(0, TeamCap - humansOnTargetTeam);
        int sentToTarget = 0;
        int sentToSpec = 0;
        int verifyMismatch = 0;
        foreach (var fc in _mgr.All) {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;

                int target = sentToTarget < availableSlots ? botTeam : (int)CsTeam.Spectator;
                c.SwitchTeam((CsTeam)target);

                // Verify is LOG-ONLY now — no fallback write. If SwitchTeam
                // is queue-based and didn't apply yet, c.TeamNum may not
                // reflect the new value for a tick. We don't try to force.
                if ((int)c.TeamNum != target) verifyMismatch++;

                if (target == botTeam) sentToTarget++;
                else sentToSpec++;
            } catch (Exception ex) { Log.Debug($"FlipTeams slot={fc.Slot}: {ex.Message}"); }
        }
        Log.Info($"Stage 1 team flip: {sentToTarget} → team {botTeam}, {sentToSpec} → spectator, " +
                 $"{verifyMismatch} immediate-verify-mismatch (may resolve next tick) " +
                 $"(cap={TeamCap}, humans on target={humansOnTargetTeam})");
    }

    /// <summary>
    /// Distance (Hammer Units) from human centroid where the bot
    /// cluster materializes. v0.6.0.3 had 300 HU (~5m world); friend
    /// playtest reported "way too far" — bots wandered for too long
    /// before reaching, lost the surprise. v0.6.0.4 cut to 80 HU
    /// (~1.3m), giving 0.5-1 sec to react. Knife-only design assumes
    /// the human can shoot back before contact; if bots pile too fast
    /// raise to 100-150 HU.
    /// </summary>
    private const float SwarmOffsetDistance = 80f;

    private void DeploySwarmAndKnifeRush()
    {
        var humansNow = LivingHumanControllers();
        Vector? centroid = humansNow.Count > 0 ? ComputeCentroid(humansNow) : null;

        // Pick a single 2D direction biased toward the human's current
        // FACING — random within ±90° of where the human is looking. Z
        // stays at human's centroid Z. Rationale: human's view direction
        // is statistically "open space" (you don't usually stare at a
        // wall 1ft from your face), so clustering somewhere in the half-
        // circle in front of them lands in playable terrain. Spawn-in-
        // wall avoidance without a working TraceRay wrapper in CSSharp
        // 1.0.367. Behind-the-back ambush sacrificed for survivability.
        Vector? clusterOrigin = null;
        if (centroid != null) {
            // Reference yaw: first human's view direction.
            float yawDeg = 0f;
            try {
                var refPawn = humansNow[0].PlayerPawn?.Value;
                if (refPawn != null && refPawn.IsValid)
                    yawDeg = refPawn.EyeAngles.Y;
            } catch { /* fall through with yawDeg = 0 */ }

            // Random offset in [-π/2, +π/2] from forward.
            double yawRad = yawDeg * Math.PI / 180.0;
            double offsetRad = (_rng.NextDouble() - 0.5) * Math.PI;
            double finalRad = yawRad + offsetRad;

            clusterOrigin = new Vector(
                centroid.X + (float)(Math.Cos(finalRad) * SwarmOffsetDistance),
                centroid.Y + (float)(Math.Sin(finalRad) * SwarmOffsetDistance),
                centroid.Z);
        }

        var bots = _mgr.All.ToList();
        for (int i = 0; i < bots.Count; i++) {
            var fc = bots[i];
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid) continue;
                if (pawn.LifeState != 0) continue;  // dead bot — skip swarm-tp

                // Per-bot stagger inside cluster: 4-wide rows, ±1.5 unit
                // spread. With mp_solid_teammates=0 they can occupy the
                // same point, but visual variation reads as "8 bots", not
                // "one bot quintuple-stacked".
                if (clusterOrigin != null) {
                    float dx = (i % 4) - 1.5f;
                    float dy = (i / 4) - 1.0f;
                    var pos = new Vector(
                        clusterOrigin.X + dx,
                        clusterOrigin.Y + dy,
                        clusterOrigin.Z);
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
            // ZOMBIE FILTER (v0.6.0.8): engine clients lingering from prior
            // reveal cycles or mp_restartgame respawn churn show up in
            // GetPlayers() with no Steam authorization. Exclude them so
            // FlipTeamsWithCap doesn't burn target-team cap on phantoms,
            // and so humansAtStart actually counts real players.
            // Real humans always have AuthorizedSteamID after spawn.
            if (c.AuthorizedSteamID == null) continue;
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

            // Restore each bot to its pre-reveal team. Bots that were in
            // spectator at reveal entry stay there (we didn't capture
            // them in _botPrevTeams). Bots flipped to spectator due to
            // team-cap overflow re-join their original team here.
            foreach (var (slot, prevTeam) in _botPrevTeams) {
                try {
                    var c = Utilities.GetPlayerFromSlot(slot);
                    if (c != null && c.IsValid)
                        c.SwitchTeam((CsTeam)prevTeam);
                } catch (Exception ex) { Log.Debug($"Restore team slot={slot}: {ex.Message}"); }
            }
            _botPrevTeams.Clear();

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

        // Stage 3 trigger: 0 living humans, sustained for ≥1 sec.
        // Dampening required because mp_restartgame at Stage 1/2 entry
        // briefly puts ALL pawns (including humans) in transient
        // respawn state where LifeState != 0 → LivingHumansCount() == 0
        // for a few ticks. Without dampening, Stage 3 fires immediately
        // after mp_restartgame even though the human is alive.
        if (Stage == RevealStage.Stage1 || Stage == RevealStage.Stage2)
        {
            if (LivingHumansCount() == 0 && _humansAtStart > 0) {
                _zeroHumansTickCount++;
                if (_zeroHumansTickCount >= ZeroHumansDampenTicks)
                    EnterStage3();
            } else {
                _zeroHumansTickCount = 0;
            }
        }
    }

    private void TickStage1()
    {
        // CRITICAL ORDERING (v0.6.0.6 fix): knife enforcement runs FIRST,
        // BEFORE any time-based gates. v0.6.0.5 had the 30s min-duration
        // gate as an early-return at the top, which silently disabled
        // knife enforcement during the first 30 seconds of Stage 1 —
        // bots that respawned mid-Stage-1 (after a death-respawn cycle
        // triggered by mp_restartgame or game logic) kept their default
        // pistol+knife loadout, never got stripped. User observed "half
        // bots with knives, half with pistols". This fixes it: enforce
        // every tick regardless of elapsed time.
        EnforceKnifeOnAll();

        // Stage 2 trigger logic (v0.6.0.5 user-spec):
        //   - HARD MINIMUM Stage1MinDurationSec.
        //   - After minimum: fire on EITHER 50% bots dead OR 60s timeout.
        var elapsedTicks = Server.TickCount - _stageStartTick;
        var elapsedSec = elapsedTicks / 64;

        if (elapsedSec < Stage1MinDurationSec) return;  // hard gate (transition only)

        var killThreshold = _mgr.Config.Stage2Kills > 0
            ? _mgr.Config.Stage2Kills
            : Math.Max(1, (_mgr.Config.FleetSize + 1) / 2);
        bool killsDone = _botsKilledThisReveal >= killThreshold;
        bool maxReached = elapsedSec >= Stage1MaxDurationSec;
        if (killsDone || maxReached)
        {
            EnterStage2();
            return;
        }
    }

    /// <summary>
    /// Strips and re-equips weapon_knife on every living bot — runs
    /// every Stage 1 tick. Idempotent (no-op if bot already holds knife).
    /// </summary>
    private void EnforceKnifeOnAll()
    {
        // (Old comment block from TickStage1 still applies here:)
        //
        // Continuous knife-only enforcement on ALL living bots — not
        // gated by _combatState membership. Catches:
        //  - bots whose ApplyKnifeRush hasn't run yet (5-sec swarm-deploy
        //    window after mp_restartgame, where defaults could otherwise
        //    show pistols on screen)
        //  - bots that respawned mid-stage and got default loadout
        //  - bots that picked up a dropped weapon from an earlier kill
        // Cost is fleet_size × 64 Hz reads/writes — trivial.
        foreach (var fc in _mgr.All)
        {
            try {
                var c = Utilities.GetPlayerFromSlot(fc.Slot);
                if (c == null || !c.IsValid) continue;
                var pawn = c.PlayerPawn?.Value;
                if (pawn == null || !pawn.IsValid) continue;
                if (pawn.LifeState != 0) continue;  // dead — wait for next round

                var active = pawn.WeaponServices?.ActiveWeapon?.Value;
                if (active == null) {
                    c.GiveNamedItem("weapon_knife");
                    continue;
                }
                if (active.DesignerName != "weapon_knife")
                {
                    StripAllWeapons(c);
                    c.GiveNamedItem("weapon_knife");
                }
            } catch (Exception ex) { Log.Debug($"TickStage1 enforce slot={fc.Slot}: {ex.Message}"); }
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

        // FIX (v0.6.0.6): re-derive bot-ness from pool authoritatively
        // instead of trusting caller's flag. The caller (InsanityRevivePlugin
        // dispatcher) computes `victimIsBot = c.IsBot OR FindBySlot != null`,
        // but `c.IsBot` is FALSE for our bots (we flipped m_bFakePlayer=0)
        // AND FindBySlot can return null in transient states (mid-Despawn,
        // mid-mapchange). When the cached flag is wrong, slow-mo fired on
        // bot deaths. Source-of-truth check: IS the slot in our managed
        // pool right now? If yes → bot. Else → real human.
        bool actuallyManagedBot = _mgr.FindBySlot(victimSlot) != null;

        if (actuallyManagedBot)
        {
            _botsKilledThisReveal++;
            return;
        }

        // True human died — slowmo death cam (Stage 2 only).
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
    /// Count living humans on the server. Two-stage filter:
    ///   (1) <see cref="FakeClientManager.FindBySlot"/> != null  — drop
    ///       OUR managed bots (m_bFakePlayer=0 + synthetic Steam ID hides
    ///       them from c.IsBot/AuthorizedSteamID).
    ///   (2) AuthorizedSteamID == null — drop ZOMBIE engine clients
    ///       (v0.6.0.8 hardening): lingering CServerSideClient from prior
    ///       reveal cycles / mp_restartgame churn that aren't connected
    ///       enough to have Steam auth. Real humans always have it.
    /// Combination is correct because step (1) runs first — so step (2)
    /// only filters non-managed clients, where AuthorizedSteamID is the
    /// canonical "real human" signal.
    /// </summary>
    private int LivingHumansCount()
    {
        int n = 0;
        foreach (var c in Utilities.GetPlayers())
        {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (_mgr.FindBySlot((int)c.Slot) != null) continue;  // managed bot
            if (c.AuthorizedSteamID == null) continue;            // zombie/unauth
            var pawn = c.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid) continue;
            if (pawn.LifeState != 0) continue;  // 0 = LIFE_ALIVE
            n++;
        }
        return n;
    }

    /// <summary>
    /// Broadcast a chat line that appears as if the bot itself typed it
    /// (real chat — team-color name + message, NOT server-prefixed).
    /// Uses the SayText2 user message protobuf with the bot's controller
    /// entity index as sender, so receiving clients render it identically
    /// to a human player saying the line.
    ///
    /// Falls back to <see cref="Server.PrintToChatAll"/> if UserMessage
    /// construction fails (proto field name drift across CSSharp versions
    /// is a known risk).
    /// </summary>
    private static void SayAsBot(FakeClient fc, string text)
    {
        CCSPlayerController? c = null;
        try { c = Utilities.GetPlayerFromSlot(fc.Slot); } catch { }
        if (c == null || !c.IsValid) return;

        try {
            var um = UserMessage.FromPartialName("CMsgSayText2");
            // Most CS2 chat plugins target all connected players for
            // global chat. Recipients API exposes AddAllPlayers().
            um.Recipients.AddAllPlayers();
            um.SetInt("entityindex", (int)c.Index);
            um.SetBool("chat", true);
            // messagename = format string. CS2's Cstrike_Chat_All uses
            // \x01 reset color, \x09 team color, etc. Inline format:
            // "{name} :  {text}" with name colored to bot's team.
            um.SetString("messagename",
                $"\x01\x09{fc.Name}\x01 :  {text}");
            um.SetString("param1", "");
            um.SetString("param2", "");
            um.SetString("param3", "");
            um.SetString("param4", "");
            um.Send();
            return;
        } catch (Exception ex) {
            Log.Debug($"SayAsBot UserMessage: {ex.Message}; falling back to PrintToChatAll");
        }
        try {
            Server.PrintToChatAll($" {fc.Name}{ChatColors.Default} : {text}");
        } catch { }
    }
}
