using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

/// <summary>
/// Smooth predictive aim. Goal-based lerp (not per-tick jitter): every ~200ms
/// we re-pick a target spot (lead + small lateral bias) and from there the
/// crosshair eases toward it tick-by-tick. Result on the radar: smooth tracking,
/// no twitchy zig-zag.
/// </summary>
public class AimController
{
    public bool Enabled = true;
    public bool LeadEnabled = true;
    public bool PrefireOffsetEnabled = true;
    public float ScanRadius = 4500f;
    public float FovDot = 0.18f;          // ~80° half-cone

    /// 0..1 — how aggressively the crosshair eases toward the goal each tick.
    /// Smaller = smoother / lazier; larger = snappier. We tune per-preset.
    public float SnapPerTick = 0.30f;

    /// Lateral bias (degrees) baked into the goal — not per-tick, so it doesn't jitter.
    public float MaxBiasDeg = 0.5f;

    /// How long a goal stays fixed before we recompute it (sec). Larger = even smoother.
    public float GoalRefreshSec = 0.22f;

    public readonly Dictionary<int, (int targetSlot, float untilTime)> ForcedTarget = new();

    private readonly Random _rng = new();
    private readonly Dictionary<int, float> _leadByBot = new();

    private class Goal
    {
        public float Yaw, Pitch;
        public float Expires;
        public int TargetSlot = -1;
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
                _goals[bot.Slot] = goal = new Goal { Expires = 0 };

            if (goal.Expires < now || goal.TargetSlot != target.Slot)
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

                // small bias baked into goal (not per-tick)
                gYaw   += ((float)_rng.NextDouble() * 2f - 1f) * MaxBiasDeg;
                gPitch += ((float)_rng.NextDouble() * 2f - 1f) * (MaxBiasDeg * 0.6f);

                goal.Yaw = gYaw;
                goal.Pitch = gPitch;
                goal.TargetSlot = target.Slot;
                goal.Expires = now + GoalRefreshSec + ((float)_rng.NextDouble() - 0.5f) * 0.10f;
            }

            // ---- Lerp current → goal ----
            var ea = pawn.EyeAngles;
            float curYaw = ea.Y;
            float curPitch = ea.X;
            float dyaw = NormalizeAngle(goal.Yaw - curYaw);
            float dpitch = goal.Pitch - curPitch;
            ea.Y = NormalizeAngle(curYaw + dyaw * SnapPerTick);
            ea.X = MathF.Max(-89f, MathF.Min(89f, curPitch + dpitch * SnapPerTick));
        }
    }

    private static float NormalizeAngle(float a)
    {
        while (a > 180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    public void Forget(int slot) { _leadByBot.Remove(slot); _goals.Remove(slot); ForcedTarget.Remove(slot); }
}
