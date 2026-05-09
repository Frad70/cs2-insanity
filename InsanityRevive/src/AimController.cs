// =============================================================================
// AimController.cs — per-bot driver for the v6 per-slot aim override pool.
//
// Reads the engine's CCSBot.m_lookPitch/m_lookYaw each tick (proven by
// AimLookflowProbe to be the BT's intended look target — not a copy of the
// smoother output) and writes them back into the shared pool's AimSlot[slot]
// entry. The C++ AimHook PRE-detour reads those values and writes them onto
// CCSPlayerPawn.m_angEyeAngles, replacing the engine smoother with our value.
//
// Etap B (this file's first form): IDENTITY PASSTHROUGH. The override value
// equals the engine's own target → bot plays exactly as it would without the
// hook. The point is to verify the read/write loop doesn't break gameplay
// before we start perturbing values in Etap C (BotProfile-driven aim error).
//
// Idle gate: when the bot is not actively redirecting aim (look fields
// stationary for IdleThreshold consecutive ticks), clear the AimSlot so the
// engine's smoother runs on its own. Without the gate, an idle bot would
// have its eye frozen at the last forwarded value forever — fine when look
// genuinely doesn't move, but if anything in the engine's pipeline updates
// eye independently of look (it shouldn't, but the slot-0 edge case from
// the lookflow probe says "don't trust this 100%"), our stale write would
// fight it.
// =============================================================================

using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace InsanityRevive;

public sealed class AimController
{
    private static int _lookPOff = -1;
    private static int _lookYOff = -1;

    private float _prevLookP;
    private float _prevLookY;
    private int   _idleTicks;
    private bool  _armed;
    private int   _lastSlot = -1;  // slot we last wrote to — clear on slot change

    // xorshift32 state for per-bot, per-tick aim noise. Seeded lazily from
    // BotProfile.Seed so two bots with the same skill/profile don't share
    // an RNG sequence (which would cluster their misses identically).
    private uint _rng;
    private bool _rngSeeded;

    /// <summary>How many consecutive ticks of |dLook| ≤ MovementEpsilon before
    /// we stop overriding and let the engine smoother run unmolested.</summary>
    private const int IdleThreshold = 16;  // 0.25 sec at 64 tps

    /// <summary>Threshold below which a per-axis tick delta counts as "no
    /// movement". 0.01° matches the same epsilon AimLookflowProbe uses to
    /// label a tick "idle" in its dump filter.</summary>
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
    public unsafe void Tick(int slot, CCSPlayerController? ctrl, PoolMmap? pool, BotProfile? profile)
    {
        if (pool == null || !pool.IsOpen) { _armed = false; return; }
        if (_lookPOff < 0)
        {
            _lookPOff = Schema.GetSchemaOffset("CCSBot", "m_lookPitch");
            _lookYOff = Schema.GetSchemaOffset("CCSBot", "m_lookYaw");
        }
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

        float lookP = *(float*)((byte*)bot.Handle.ToPointer() + _lookPOff);
        float lookY = *(float*)((byte*)bot.Handle.ToPointer() + _lookYOff);

        float dP = lookP - _prevLookP;
        float dY = AngleDelta(lookY, _prevLookY);
        _prevLookP = lookP;
        _prevLookY = lookY;
        if (MathF.Abs(dP) < MovementEpsilon && MathF.Abs(dY) < MovementEpsilon)
            _idleTicks++;
        else
            _idleTicks = 0;

        if (_idleTicks >= IdleThreshold)
        {
            Disarm(pool);
            return;
        }

        // Etap C: additive uniform aim error scaled by (1 − skill/100).
        // skill=100 → no noise (perfect aim, identity passthrough);
        // skill=50  → ±2.5° per axis;
        // skill=0   → ±5° per axis (visibly bad).
        // Yaw is the more visible axis (horizontal sway); pitch we
        // additionally clamp into legal-eye range to avoid wraparound
        // (engine occasionally reports lookP=357.9-style values).
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
        float noiseP = (NextFloatM1to1()) * errorDeg;
        float noiseY = (NextFloatM1to1()) * errorDeg;
        float overP = MathF.Max(-89f, MathF.Min(89f, lookP + noiseP));
        float overY = lookY + noiseY;

        ulong botKey = (ulong)bot.Handle.ToInt64();
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
