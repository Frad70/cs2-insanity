// v0.10.0 — Movement realism (peek/walk/crouch timing).
// Buttons-only. NO velocity writes. The v0.6.6 drift bug came from per-tick
// velocity nudges; that mistake is not repeated here.
//
// What this file adds (all gated behind _movementRealismEnabled, default ON):
//   1. Walk-vs-run: passive archetypes (Lurker / AwperPassive / Anchor / Support)
//      occasionally hold IN_SPEED (shift-walk, 4096) for 0.4-1.2s while moving.
//   2. Crouch pulse: when an Awper or Anchor archetype acquires a fresh target at
//      >800 distance, briefly pulse IN_DUCK (4) for 0.4-0.8s. Skipped for Entry.
//   3. Pre-aim before peek: Lurker / AwperPassive bots, when stopped (speed<30)
//      and no current target, set eye angles to last movement-heading ± 15..40°.
//   4. Crouch-jump simulation: rare, max 1x/round/bot, when crossing a doorway
//      (heuristic: speed>100, distance<700 on fresh target). One tick of IN_JUMP
//      followed 60ms later by ~0.45s of IN_DUCK.
//   5. Entry-fragger swing: when an Entry archetype bot has a low-HP teammate
//      ahead and a fresh target appears, override their reaction-time to 80ms
//      for that engagement only.
//   6. Shoulder peek: bots with Skill < 0.7 occasionally get a 0.2s "fake peek"
//      where ForcedTarget is suppressed (slot -1) so they don't fire.

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

public partial class InsanityRevive
{
    /// v0.10: OR all active button-pulse bits onto the bot's MovementServices.Buttons
    /// each tick the corresponding "until" timer is in the future. Same pattern as the
    /// pre-existing _attackUntil block; centralised so future button additions are easy.
    private void ApplyButtonPulses(CCSPlayerController bot, CCSPlayerPawn pawn, float now)
    {
        try
        {
            var ms = pawn.MovementServices;
            if (ms == null) return;
            ulong bits = 0UL;
            if (_attackUntil.TryGetValue(bot.Slot, out var au) && now < au) bits |= IN_ATTACK;
            if (_walkUntil.TryGetValue(bot.Slot, out var wu)   && now < wu) bits |= IN_SPEED;
            if (_duckUntil.TryGetValue(bot.Slot, out var du)   && now < du) bits |= IN_DUCK;
            if (_jumpUntil.TryGetValue(bot.Slot, out var ju)   && now < ju) bits |= IN_JUMP;
            if (bits != 0UL) ms.Buttons.ButtonStates[0] |= bits;
        } catch { }
    }

    /// v0.10: per-tick movement realism rolls. Decides when to:
    ///   • shift-walk briefly (passive archetypes when moving)
    ///   • emit shoulder-peek (low-skill quirk: stop firing for ~0.2s)
    ///   • install pre-aim eye-angles when stopped with no current target
    /// Velocity is NEVER written here. Pure read + button/eye-angle output.
    private void MovementRealismTick(CCSPlayerController bot, CCSPlayerPawn pawn, float now)
    {
        if (!_botPersonas.TryGetValue(bot.Slot, out var persona)) return;

        var av = pawn.AbsVelocity;
        var speed = MathF.Sqrt(av.X * av.X + av.Y * av.Y);

        // Track movement heading while moving (used for pre-aim when stopped)
        if (speed > 60f)
        {
            var headYaw = MathF.Atan2(av.Y, av.X) * 180f / MathF.PI;
            _lastHeadingYaw[bot.Slot] = headYaw;
        }

        // ---- (1) Walk-vs-run: passive archetypes occasionally hold shift-walk ----
        bool walkActive = _walkUntil.TryGetValue(bot.Slot, out var wuPrev) && now < wuPrev;
        bool walkRecent = _walkUntil.TryGetValue(bot.Slot, out wuPrev) && now < wuPrev + 0.5f;
        if (speed > 80f && !walkActive && !walkRecent)
        {
            float pctPerSec = persona.Archetype switch
            {
                BotArchetype.Lurker        => 0.35f,
                BotArchetype.AwperPassive  => 0.30f,
                BotArchetype.Anchor        => 0.18f,
                BotArchetype.Support       => 0.10f,
                BotArchetype.Entry         => 0.0f,    // Entries swing, never creep
                BotArchetype.AwperAggro    => 0.05f,
                _                          => 0.08f,
            };
            // Already in active engagement → less likely to suddenly shift-walk
            if (_combatUntil.TryGetValue(bot.Slot, out var cu) && now < cu)
                pctPerSec *= 0.5f;
            if (Roll(pctPerSec * 0.030f))
            {
                var dur = 0.4f + (float)_rng.NextDouble() * 0.8f;
                _walkUntil[bot.Slot] = now + dur;
            }
        }

        // ---- (2) Pre-aim while stopped & no target ----
        if ((persona.Archetype == BotArchetype.Lurker || persona.Archetype == BotArchetype.AwperPassive)
            && speed < 30f
            && !_aim.ForcedTarget.ContainsKey(bot.Slot)
            && (!_lookUntil.TryGetValue(bot.Slot, out var luEx) || now > luEx)
            && (!_shoulderPeekUntil.TryGetValue(bot.Slot, out var spu) || now > spu))
        {
            // Throttle: refresh pre-aim every 0.8-1.6s.
            if (!_preAimRefreshAt.TryGetValue(bot.Slot, out var nextAt) || now >= nextAt)
            {
                _preAimRefreshAt[bot.Slot] = now + 0.8f + (float)_rng.NextDouble() * 0.8f;
                float baseYaw = _lastHeadingYaw.TryGetValue(bot.Slot, out var h) ? h : pawn.EyeAngles.Y;
                var offset = (_rng.Next(2) == 0 ? -1f : 1f) * (15f + (float)_rng.NextDouble() * 25f);
                _lookUntil[bot.Slot] = now + 0.7f + (float)_rng.NextDouble() * 0.6f;
                _forceLook[bot.Slot] = (baseYaw + offset, -3f + (float)_rng.NextDouble() * 6f);
            }
        }

        // ---- (3) Shoulder peek: very low-skill bots fake-peek without firing ----
        if (persona.Skill < 0.7f
            && speed > 40f
            && (!_shoulderPeekUntil.TryGetValue(bot.Slot, out var sp2) || now > sp2 + 6f))
        {
            // ~3% per second total (≈ 0.09% per 30ms tick)
            if (Roll(0.030f * 0.030f))
            {
                var peekDur = 0.20f + (float)_rng.NextDouble() * 0.10f;
                _shoulderPeekUntil[bot.Slot] = now + peekDur;
                // Suppress aim acquisition for the brief window (slot -1 = no valid target)
                _aim.ForcedTarget[bot.Slot] = (-1, now + peekDur);
            }
        }
    }

    /// v0.10: AimController callback — fires when a bot's goal flips to a NEW target slot.
    /// Drives crouch-pulse (Awper/Anchor at long range), entry-fragger reaction-time
    /// override (when a low-HP teammate is ahead), and the rare crouch-jump.
    private void OnAimFreshTarget(int botSlot, int targetSlot, float distance)
    {
        if (!_movementRealismEnabled) return;
        var bot = Utilities.GetPlayerFromSlot(botSlot);
        if (bot is null || !bot.IsValid || !bot.IsBot) return;
        if (!_botPersonas.TryGetValue(botSlot, out var persona)) return;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn?.IsValid != true) return;
        if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

        var now = Server.CurrentTime;

        // ---- Long-range crouch pulse for Awper / Anchor (Entry skipped — they swing) ----
        if (distance > 800f
            && (persona.Archetype == BotArchetype.AwperPassive
                || persona.Archetype == BotArchetype.AwperAggro
                || persona.Archetype == BotArchetype.Anchor))
        {
            if (Roll(0.65f))
                _duckUntil[botSlot] = now + 0.4f + (float)_rng.NextDouble() * 0.4f;   // 0.4-0.8s
        }

        // ---- Entry-rush: Entry archetype + low-HP teammate ahead → fast reaction ----
        if (persona.Archetype == BotArchetype.Entry)
        {
            var origin = pawn.AbsOrigin;
            if (origin != null)
            {
                var yawRad = pawn.EyeAngles.Y * MathF.PI / 180f;
                var fX = MathF.Cos(yawRad);
                var fY = MathF.Sin(yawRad);
                bool teammateInFrontHurt = false;
                foreach (var mate in Utilities.GetPlayers())
                {
                    if (!mate.IsValid || mate == bot) continue;
                    if (mate.Team != bot.Team) continue;
                    var mp = mate.PlayerPawn?.Value;
                    if (mp?.IsValid != true || mp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
                    if (mp.Health > 60) continue;     // not low HP
                    var mo = mp.AbsOrigin; if (mo == null) continue;
                    var dx = mo.X - origin.X; var dy = mo.Y - origin.Y;
                    var d2 = dx * dx + dy * dy;
                    if (d2 > 700f * 700f) continue;
                    var inv = 1f / MathF.Max(1e-3f, MathF.Sqrt(d2));
                    var dot = (dx * inv) * fX + (dy * inv) * fY;
                    if (dot > 0.30f) { teammateInFrontHurt = true; break; }
                }
                if (teammateInFrontHurt)
                {
                    _aim.ReactionTimeOverrideSec[botSlot] = 0.080f;
                    _entryRushUntil[botSlot] = now + 1.5f;
                    AddTimer(1.6f, () =>
                    {
                        if (_entryRushUntil.TryGetValue(botSlot, out var eu) && Server.CurrentTime >= eu)
                            _aim.ReactionTimeOverrideSec.Remove(botSlot);
                    });
                }
            }
        }

        // ---- Crouch-jump simulation: rare, 1x/round/bot, on close fresh target while moving ----
        if (distance < 700f && !_crouchJumpUsedThisRound.GetValueOrDefault(botSlot, false))
        {
            var av = pawn.AbsVelocity;
            var spd = MathF.Sqrt(av.X * av.X + av.Y * av.Y);
            if (spd > 100f && Roll(0.020f))     // 2% per qualifying engagement
            {
                _crouchJumpUsedThisRound[botSlot] = true;
                _jumpUntil[botSlot] = now + 0.040f;     // single 33Hz tick of +jump
                AddTimer(0.060f, () =>
                {
                    if (!bot.IsValid) return;
                    _duckUntil[botSlot] = Server.CurrentTime + 0.45f;
                });
            }
        }
    }
}
