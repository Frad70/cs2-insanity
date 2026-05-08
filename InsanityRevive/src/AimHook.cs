// =============================================================================
// AimHook.cs — Step 0 final probe / Step 1 foundation.
//
// PRE-hook on libserver.so:CCSBot::UpdateLookAngles, the per-tick aim driver
// for in-game bots. Disassembly extract (2026-05-08) of the function shows:
//
//   reads:  m_lookPitch (this+0x594C, float)
//           m_lookYaw   (this+0x5954, float)
//   writes: m_lookPitchVel (this+0x5950)
//           m_lookYawVel   (this+0x5958)
//           computed angles → this+0x54F0 (×3 in different code paths)
//
// Plugin-side writes to m_lookPitch/Yaw from Listeners.OnTick lose the race
// because that listener fires AFTER bot AI's per-tick subsystems. Hooking
// PRE-UpdateLookAngles is the only point where writing m_lookPitch/Yaw is
// guaranteed to be read THIS tick before any smoothing.
//
// API:
//   AimHook.Install() / Uninstall()  — wire / unwire the detour
//   AimHook.SetGlobalOverride(pitch?, yaw?)  — write these to every CCSBot
//                that calls UpdateLookAngles. NaN = "leave field alone".
//   AimHook.PerSlotOverride(slot, pitch, yaw) — TODO once we have a stable
//                CCSBot* → slot map.
//
// Diagnostic: counters for invocations + writes; first 8 fires log details.
// =============================================================================

using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace InsanityRevive;

public static class AimHook
{
    public static bool   Installed       { get; private set; }
    public static string? InstallError   { get; private set; }
    public static long   InvocationCount { get; private set; }
    public static long   WriteCount      { get; private set; }
    public static long   LogStripeCount  { get; private set; }

    /// <summary>Pitch override target in degrees. NaN = no override.</summary>
    public static float OverridePitch { get; private set; } = float.NaN;
    /// <summary>Yaw override target in degrees. NaN = no override.</summary>
    public static float OverrideYaw   { get; private set; } = float.NaN;

    private const int LookPitchOffset = 0x594C;
    private const int LookYawOffset   = 0x5954;
    /// <summary>Log first N hook fires in detail; afterwards every Mth.</summary>
    private const int InitialDetailedLogs = 8;
    private const int LogEvery            = 200;

    private static MemoryFunctionVoid<IntPtr>? _hook;

    public static bool Install()
    {
        if (Installed) return true;
        try
        {
            _hook = new MemoryFunctionVoid<IntPtr>("CCSBot_UpdateLookAngles");
            _hook.Hook(OnPreUpdateLookAngles, HookMode.Pre);
            Installed = true;
            InstallError = null;
            Log.Info("AimHook installed (PRE CCSBot::UpdateLookAngles)");
            return true;
        }
        catch (Exception ex)
        {
            Installed = false;
            InstallError = ex.Message;
            Log.Error($"AimHook install failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public static void Uninstall()
    {
        if (!Installed || _hook == null) return;
        try { _hook.Unhook(OnPreUpdateLookAngles, HookMode.Pre); }
        catch (Exception ex) { Log.Debug($"AimHook unhook: {ex.Message}"); }
        Installed = false;
        Log.Info("AimHook uninstalled");
    }

    public static void SetGlobalOverride(float? pitch, float? yaw)
    {
        OverridePitch = pitch ?? float.NaN;
        OverrideYaw   = yaw   ?? float.NaN;
        Log.Info($"AimHook override: pitch={OverridePitch} yaw={OverrideYaw} " +
                 $"(NaN = leave alone). Active fires={InvocationCount}");
    }

    public static string DebugStatus()
        => $"installed={Installed} fires={InvocationCount} writes={WriteCount} " +
           $"override=(p={OverridePitch:F1}, y={OverrideYaw:F1}) error={InstallError ?? "n/a"}";

    private static unsafe HookResult OnPreUpdateLookAngles(DynamicHook h)
    {
        InvocationCount++;
        try
        {
            // First param is `this` — pointer to CCSBot. Disassembly:
            //   b41c5e: mov %rdi, %rbx   ; preserves this in rbx for fn body.
            // CSSharp's DynamicHook wraps this — GetParam<IntPtr>(0) returns rdi.
            var thisPtr = h.GetParam<IntPtr>(0);
            if (thisPtr == IntPtr.Zero) return HookResult.Continue;

            bool wantWrite = !float.IsNaN(OverridePitch) || !float.IsNaN(OverrideYaw);

            // Capture current values for log diff.
            float prevPitch = 0f, prevYaw = 0f;
            try
            {
                prevPitch = *((float*)((byte*)thisPtr.ToPointer() + LookPitchOffset));
                prevYaw   = *((float*)((byte*)thisPtr.ToPointer() + LookYawOffset));
            }
            catch { /* read-back can fault if pointer is invalid */ }

            if (wantWrite)
            {
                if (!float.IsNaN(OverridePitch))
                    *((float*)((byte*)thisPtr.ToPointer() + LookPitchOffset)) = OverridePitch;
                if (!float.IsNaN(OverrideYaw))
                    *((float*)((byte*)thisPtr.ToPointer() + LookYawOffset))   = OverrideYaw;
                WriteCount++;
            }

            // Logging policy: detail-log first 8 fires, then every 200 fires.
            // Avoid 64Hz × N-bots flood — that would be >500 lines/sec.
            bool shouldLog = InvocationCount <= InitialDetailedLogs
                          || (InvocationCount % LogEvery) == 0;
            if (shouldLog)
            {
                LogStripeCount++;
                Log.Info($"AimHook fire#{InvocationCount} this=0x{thisPtr.ToInt64():X} " +
                         $"prev=({prevPitch:F1},{prevYaw:F1}) " +
                         $"override=({OverridePitch:F1},{OverrideYaw:F1}) " +
                         $"wrote={wantWrite} writes={WriteCount}");
            }
        }
        catch (Exception ex)
        {
            // First few exceptions get logged; suppress further to avoid spam.
            if (InvocationCount <= 4)
                Log.Error($"AimHook fire#{InvocationCount} threw: {ex.GetType().Name}: {ex.Message}");
        }
        return HookResult.Continue;
    }
}
