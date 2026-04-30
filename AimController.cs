using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

/// <summary>
/// Smooth predictive aim with per-bot personality. Goal-based lerp (not
/// per-tick jitter): every ~GoalRefreshSec we re-pick a target spot
/// (lead + small lateral bias) and from there the crosshair eases toward it
/// tick-by-tick. v0.8 — adds per-bot AimProfile so each bot feels different
/// (low-skill: loose, lazy, jittery; high-skill: crisp, eager, occasionally
/// overshoots, sometimes auto-corrects).
/// </summary>
public class AimController
{
    public bool Enabled = true;
    public bool LeadEnabled = true;
    public bool PrefireOffsetEnabled = true;
    public float ScanRadius = 4500f;
    public float FovDot = 0.18f;          // ~80° half-cone

    // Default values for bots without a registered persona profile.
    public float SnapPerTick = 0.30f;
    public float MaxBiasDeg = 0.5f;
    public float GoalRefreshSec = 0.22f;

    public readonly Dictionary<int, (int targetSlot, float untilTime)> ForcedTarget = new();

    private readonly Random _rng = new();
    private readonly Dictionary<int, float> _leadByBot = new();

    /// <summary>Per-bot aim profile snapshot. Set externally when persona is created.</summary>
    public class AimProfile
    {
        public float SnapPerTick = 0.30f;
        public float MaxBiasDeg = 0.5f;
        public float GoalRefreshSec = 0.22f;
        public float ReactionTimeSec = 0.18f;
        public float OvershootChance = 0.10f;
        public float OvershootDeg = 1.5f;
        public float TrackingNoiseDeg = 0.0f;
        public float MicroAdjustChance = 0.0f;
        public bool  SpraysWell = false;
        public float FlickStrength = 1.0f;
    }
    private readonly Dictionary<int, AimProfile> _profiles = new();

    public void SetProfile(int slot, AimProfile p) => _profiles[slot] = p;

    private AimProfile GetProfile(int slot)
    {
        if (_profiles.TryGetValue(slot, out var p)) return p;
        return new AimProfile
        {
            SnapPerTick = SnapPerTick,
            MaxBiasDeg = MaxBiasDeg,
            GoalRefreshSec = GoalRefreshSec,
        };
    }

    private class Goal
    {
        public float Yaw, Pitch;
        public float Expires;
        public int TargetSlot = -1;
        public float NoticedAt = -999f;
        public bool Engaged = false;
        public bool Overshot = false;
        public float MicroAdjustAt = -1f;
    }
    private readonly Dictionary<int, Goal> _goals = new();

    public void Tick()
    {
        if (!Enabled) return;
        var alive = Utilities.GetPlayers().Where(p =>
        {
            if (!p.IsValid) return false;
            var pp = p.PlayerPawn?.Value;
            return pp?.IsValid == true && pp.LifeState == (byte)LifeState_t.LIFE_ALIVE;
        }).ToList();
        if (alive.Count == 0) return;

        var now = CounterStrikeSharp.API.Server.CurrentTime;

        foreach (var bot in alive)
        {
            if (!bot.IsBot) continue;
            var pawn = bot.PlayerPawn?.Value;
            if (pawn == null) continue;
            if (bot.Team <= CsTeam.Spectator) continue;
            var origin = pawn.AbsOrigin;
            if (origin == null) continue;
            var eyeZ = origin.Z + 64f;
            var profile = GetProfile(bot.Slot);

            // ---- Resolve target: forced override OR FOV-scanned closest enemy ----
            CCSPlayerController? target = null;
            if (ForcedTarget.TryGetValue(bot.Slot, out var ft) && now <= ft.untilTime)
            {
                foreach (var p in alive)
                    if (p.Slot == ft.targetSlot) { target = p; break; }
            }
            else if (ForcedTarget.ContainsKey(bot.Slot))
            {
                ForcedTarget.Remove(bot.Slot);
            }

            if (target is null)
            {
                float bestSq = ScanRadius * ScanRadius;
                var ea0 = pawn.EyeAngles;
                var yawRad = ea0.Y * MathF.PI / 180f;
                var pitchRad = ea0.X * MathF.PI / 180f;
                var fX = MathF.Cos(pitchRad) * MathF.Cos(yawRad);
                var fY = MathF.Cos(pitchRad) * MathF.Sin(yawRad);
                var fZ = -MathF.Sin(pitchRad);
                foreach (var en in alive)
                {
                    if (en == bot || en.Team == bot.Team || en.Team <= CsTeam.Spectator) continue;
                    var ep = en.PlayerPawn?.Value; if (ep == null) continue;
                    var epos = ep.AbsOrigin; if (epos == null) continue;
                    var dx = epos.X - origin.X;
                    var dy = epos.Y - origin.Y;
                    var dz = (epos.Z + 56f) - eyeZ;
                    var d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 > bestSq) continue;
                    var inv = 1f / MathF.Max(1e-3f, MathF.Sqrt(d2));
                    var dot = (dx * inv) * fX + (dy * inv) * fY + (dz * inv) * fZ;
                    if (dot < FovDot) continue;
                    bestSq = d2;
                    target = en;
                }
            }

            if (target is null) { _goals.Remove(bot.Slot); continue; }

            // ---- Compute / refresh goal ----
            if (!_goals.TryGetValue(bot.Slot, out var goal))
                _goals[bot.Slot] = goal = new Goal { Expires = 0, NoticedAt = now };

            bool freshTarget = goal.TargetSlot != target.Slot;
            if (freshTarget)
            {
                goal.NoticedAt = now;
                goal.Engaged = false;
            }

            // Reaction-time gate
            if (!goal.Engaged)
            {
                if (now - goal.NoticedAt < profile.ReactionTimeSec)
                    continue;
                goal.Engaged = true;
                if (_rng.NextDouble() < profile.OvershootChance)
                    goal.Overshot = true;
            }

            if (goal.Expires < now || freshTarget)
            {
                if (!_leadByBot.TryGetValue(bot.Slot, out var lead))
                {
                    lead = 0.10f + (float)_rng.NextDouble() * 0.20f;
                    _leadByBot[bot.Slot] = lead;
                }
                var tp = target.PlayerPawn!.Value!;
                var to = tp.AbsOrigin!;
                var tv = tp.AbsVelocity!;
                float predX = to.X, predY = to.Y, predZ = to.Z + 56f;
                if (LeadEnabled)
                {
                    predX += tv.X * lead;
                    predY += tv.Y * lead;
                    predZ += tv.Z * lead;
                }
                if (PrefireOffsetEnabled)
                {
                    var sp = MathF.Sqrt(tv.X * tv.X + tv.Y * tv.Y);
                    if (sp > 80f)
                    {
                        var nudge = 6f + (float)_rng.NextDouble() * 6f;
                        predX += (tv.X / sp) * nudge;
                        predY += (tv.Y / sp) * nudge;
                    }
                }
                var ddx = predX - origin.X;
                var ddy = predY - origin.Y;
                var ddz = predZ - eyeZ;
                var horiz = MathF.Sqrt(ddx * ddx + ddy * ddy);
                float gYaw = MathF.Atan2(ddy, ddx) * 180f / MathF.PI;
                float gPitch = -MathF.Atan2(ddz, horiz) * 180f / MathF.PI;

                gYaw   += ((float)_rng.NextDouble() * 2f - 1f) * profile.MaxBiasDeg;
                gPitch += ((float)_rng.NextDouble() * 2f - 1f) * (profile.MaxBiasDeg * 0.6f);

                if (goal.Overshot)
                {
                    var sign = (_rng.NextDouble() < 0.5) ? 1f : -1f;
                    gYaw   += sign * profile.OvershootDeg;
                    gPitch += sign * profile.OvershootDeg * 0.5f;
                    goal.Overshot = false;
                }

                if (_rng.NextDouble() < profile.MicroAdjustChance)
                    goal.MicroAdjustAt = now + 0.10f + (float)_rng.NextDouble() * 0.18f;

                goal.Yaw = gYaw;
                goal.Pitch = gPitch;
                goal.TargetSlot = target.Slot;
                goal.Expires = now + profile.GoalRefreshSec
                              + ((float)_rng.NextDouble() - 0.5f) * 0.10f;
            }

            // ---- Lerp current → goal ----
            var ea = pawn.EyeAngles;
            float curYaw = ea.Y;
            float curPitch = ea.X;
            float dyaw = NormalizeAngle(goal.Yaw - curYaw);
            float dpitch = goal.Pitch - curPitch;

            float snap = profile.SnapPerTick;
            float angDist = MathF.Sqrt(dyaw * dyaw + dpitch * dpitch);
            if (angDist > 8f)
                snap *= profile.FlickStrength;

            if (goal.MicroAdjustAt > 0 && now >= goal.MicroAdjustAt && angDist < 3.0f)
            {
                snap = MathF.Min(0.95f, snap * 2.0f);
                goal.MicroAdjustAt = -1f;
            }

            float newYaw   = NormalizeAngle(curYaw + dyaw * snap);
            float newPitch = MathF.Max(-89f, MathF.Min(89f, curPitch + dpitch * snap));

            if (profile.TrackingNoiseDeg > 0.001f)
            {
                newYaw   += ((float)_rng.NextDouble() * 2f - 1f) * profile.TrackingNoiseDeg;
                newPitch += ((float)_rng.NextDouble() * 2f - 1f) * profile.TrackingNoiseDeg * 0.6f;
                newPitch  = MathF.Max(-89f, MathF.Min(89f, newPitch));
            }

            ea.Y = newYaw;
            ea.X = newPitch;
        }
    }

    private static float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    public void Forget(int slot)
    {
        _leadByBot.Remove(slot);
        _goals.Remove(slot);
        ForcedTarget.Remove(slot);
        _profiles.Remove(slot);
    }
}
