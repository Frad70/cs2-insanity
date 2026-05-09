// =============================================================================
// AimController.cs — per-bot driver for the v7 per-slot aim override pool.
//
// Architecture (post-2026-05-09 rework, after sample-write phase pattern
// proved to add unacceptable 110ms aim lag):
//
//   * Pool layout v7 widened AimSlot from 24 → 32 bytes, adding bt_target_*
//     fields. C++ AimHook handler captures BT's freshly-set m_lookPitch/Yaw
//     into those fields BEFORE applying our override.
//   * AimController reads bt_target_* from the pool each tick — clean BT
//     target with 1-tick lag (15.6ms @ 64 tps), no stale m_lookPitch issue.
//   * Computes override = bt_target + per-tick noise scaled by
//     (1 − CurrentAimSkill/100), writes to AimSlot.
//   * C++ handler clobbers m_lookPitch/m_angEyeAngles with our override —
//     smoother body of UpdateLookAngles sees override == lookP and lerps
//     to a fixed point: bot eye locks to (target + noise).
//
// Net effect: bot eye is at BT's intended target offset by noise each tick,
// reaction lag = 1 tick (essentially imperceptible — humans have 50–250ms
// reaction lag on aim redirects).
//
// Idle gate: when bt_target hasn't changed (BT not redirecting) for
// IdleThreshold ticks, disarm so the engine smoother runs natural. Avoids
// freezing eye at a stale target indefinitely.
//
// History: identity-passthrough (Etap B) read m_lookPitch directly + had
// only m_angEyeAngles override → smoother dampened our writes. Sample-write
// phase pattern (Etap B+) added m_lookPitch override + cached target across
// SamplePeriod=8 ticks → bots played with 110ms reaction lag, kennyS skill=95
// fragged less than donk skill=51 because lag dominated noise advantage.
// v7 feedback-channel design: 1-tick lag, full noise effect, full lock.
// =============================================================================

using System;
using CounterStrikeSharp.API.Core;

namespace InsanityRevive;

public sealed class AimController
{
    /// <summary>Global off-switch. When true, all AimControllers no-op
    /// in Tick. Useful for diagnostic — set via rcon to test perslot
    /// override in isolation without our identity-passthrough writes
    /// stomping on it.</summary>
    public static bool GlobalDisable { get; set; } = false;

    private float _prevBtP;
    private float _prevBtY;
    private int   _idleTicks;
    private bool  _armed;
    private int   _lastSlot = -1;  // slot we last wrote to — clear on slot change

    // xorshift32 state for per-bot, per-tick aim noise. Seeded lazily from
    // BotProfile.Seed so two bots with the same skill/profile don't share
    // an RNG sequence (which would cluster their misses identically).
    private uint _rng;
    private bool _rngSeeded;

    /// <summary>How many consecutive ticks of |dBtTarget| ≤ MovementEpsilon
    /// before we disarm and let the engine smoother run unmolested.
    /// 32 ticks = 0.5 sec @ 64 tps. Bot needs to be holding a steady aim
    /// for half a second before we let go — picks up active tracking
    /// (constantly redirecting) but releases on standing-around-spawn.</summary>
    private const int IdleThreshold = 32;

    /// <summary>Threshold below which a per-tick BT-target delta counts as
    /// "no movement". 0.01° matches the same epsilon AimLookflowProbe uses
    /// for "idle" classification.</summary>
    private const float MovementEpsilon = 0.01f;

    /// <summary>Maximum per-axis additive aim error applied to the engine
    /// target before writing to the override pool. The actual error is scaled
    /// by (1 − CurrentAimSkill/100), so skill=100 → 0°, skill=50 → 2.5°,
    /// skill=0 → 5°. Picked to be visibly bad at low skill (a 5° flick miss
    /// at 30 m is ~2.6 m, full-body offset) but not silly: BT will still
    /// converge on the player when skill is high. Etap C+ will replace
    /// uniform noise with per-target scatter and twitch reaction.</summary>
    private const float MaxAimErrorDeg = 5f;

    public bool Armed => _armed;
    public int  IdleTicks => _idleTicks;

    /// <summary>Drive one tick. Pool may be null (early boot before manager
    /// owns its mmap); in that case do nothing — the engine smoother runs.</summary>
    public void Tick(int slot, CCSPlayerController? ctrl, PoolMmap? pool, BotProfile? profile)
    {
        if (GlobalDisable) { Disarm(pool); return; }
        if (pool == null || !pool.IsOpen) { _armed = false; return; }
        // Ctrl + pawn + bot all have to be live; bots in spec / mid-respawn
        // briefly drop out and we should disarm so the engine takes over.
        if (ctrl == null || !ctrl.IsValid) { Disarm(pool); return; }
        var pawn = ctrl.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) { Disarm(pool); return; }
        var bot = pawn.Bot;
        if (bot == null || bot.Handle == IntPtr.Zero) { Disarm(pool); return; }

        // If the slot moved (mapchange / reuse), clear the old AimSlot so we
        // don't leave a dangling override for the previous occupant.
        if (_lastSlot >= 0 && _lastSlot != slot && _armed)
        {
            pool.ClearAimSlot(_lastSlot);
            _armed = false;
        }
        _lastSlot = slot;

        ulong botKey = (ulong)bot.Handle.ToInt64();

        // Make sure the bot_key is registered in the pool BEFORE the C++
        // handler tries to write bt_target into our slot. WriteAimSlot
        // with a placeholder enabled=false override establishes the entry;
        // we'll then read bt_target back. On the very first tick of a
        // bot's life, bt_target won't exist yet — handle the NaN case below.
        if (!_armed)
        {
            pool.WriteAimSlot(slot, botKey, enabled: false, pitch: 0f, yaw: 0f);
        }

        // Read BT's intended target via the v7 feedback channel — populated
        // by the C++ handler last tick (or this tick, depending on tick
        // ordering between UpdateLookAngles and CSSharp.OnTick).
        var (btP, btY) = pool.ReadBotTarget(slot);
        if (float.IsNaN(btP) || float.IsNaN(btY))
        {
            // First tick — no BT target captured yet. Skip; next tick
            // we'll have one.
            return;
        }
        btP = NormalizeAngle(btP);  // m_lookPitch sometimes wraparound

        // Idle gate — track whether BT is actively redirecting aim.
        float dP = btP - _prevBtP;
        float dY = AngleDelta(btY, _prevBtY);
        _prevBtP = btP;
        _prevBtY = btY;
        if (MathF.Abs(dP) < MovementEpsilon && MathF.Abs(dY) < MovementEpsilon)
            _idleTicks++;
        else
            _idleTicks = 0;
        if (_idleTicks >= IdleThreshold)
        {
            Disarm(pool);
            return;
        }

        // Apply skill-scaled noise. skill=100 → 0° (perfect);
        // skill=50 → ±MaxAimErrorDeg/2; skill=0 → ±MaxAimErrorDeg.
        if (!_rngSeeded)
        {
            // Mix profile seed with slot so two bots from cloned profiles
            // (rare but possible) don't get the same noise sequence.
            ulong s = (profile?.Seed ?? 0xDEADBEEFCAFEBABEUL) ^ ((ulong)(uint)slot << 32) ^ 0xA1B2C3D4E5F60718UL;
            _rng = (uint)(s ^ (s >> 32));
            if (_rng == 0) _rng = 0xCAFEBABE;
            _rngSeeded = true;
        }
        float skill = profile?.CurrentAimSkill ?? 50f;
        float errorFactor = MathF.Max(0f, 1f - skill / 100f);
        float errorDeg = errorFactor * MaxAimErrorDeg;
        float noiseP = NextFloatM1to1() * errorDeg;
        float noiseY = NextFloatM1to1() * errorDeg;
        float overP = MathF.Max(-89f, MathF.Min(89f, btP + noiseP));
        float overY = btY + noiseY;

        pool.WriteAimSlot(slot, botKey, enabled: true, pitch: overP, yaw: overY);
        _armed = true;
    }

    /// <summary>Force-disarm: clear our AimSlot if armed. Idempotent. Called
    /// when the bot loses its pawn / is despawned, on slot change, or after
    /// IdleThreshold ticks of no look movement.</summary>
    public void Disarm(PoolMmap? pool)
    {
        if (!_armed) return;
        if (pool != null && pool.IsOpen && _lastSlot >= 0) pool.ClearAimSlot(_lastSlot);
        _armed = false;
        _idleTicks = 0;
    }

    private static float AngleDelta(float a, float b)
    {
        float d = a - b;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    /// <summary>Fold any angle into [-180, 180]. Used on lookP before
    /// pitch-clamp so wraparound values (e.g. 357° meaning -3°) don't
    /// fold to ±89° straight up/down after Min/Max.</summary>
    private static float NormalizeAngle(float a)
    {
        float n = a;
        while (n >  180f) n -= 360f;
        while (n < -180f) n += 360f;
        return n;
    }

    /// <summary>xorshift32 step + map to [-1, 1) float. Allocation-free,
    /// fine quality for visual aim noise (not crypto). State must be
    /// nonzero — caller seeds non-zero in Tick.</summary>
    private float NextFloatM1to1()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        // Use 24 high bits for mantissa precision; map [0, 2^24) → [-1, 1).
        return (_rng >> 8) * (2f / 16777216f) - 1f;
    }
}
