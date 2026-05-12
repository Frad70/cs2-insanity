// =============================================================================
// SchemaSafety — guard rail around Schema.SetSchemaValue + Utilities.SetStateChanged
// =============================================================================
//
// CRASH INCIDENT LOG (do NOT remove these entries — future authors need
// to know why these fields are off-limits):
//
//   v0.6.0.6  (2026-05-02) — `m_iTeamNum` SetStateChanged on
//                            CCSPlayerController. CSSharp warned "Field
//                            CCSPlayerController:m_iTeamNum is not
//                            networked, but SetStateChanged was called on
//                            it." Server crashed on the next tick.
//                            Fix path: use `c.SwitchTeam((CsTeam)team)`
//                            instead — proper engine path that goes
//                            through CCSPlayerController::SwitchTeam,
//                            which handles team-counter accounting.
//
//   parallel  (2026-05-03) — `m_angEyeAngles` SetStateChanged on
//                            CCSPlayerPawnBase / CCSPlayerPawn. Same
//                            "not networked" warning, same crash. Caused
//                            DLL drift incident at 14:21:04 (deployed
//                            DLL didn't match monorepo build, traced to
//                            an out-of-tree perfect-aim experiment).
//                            Fix path: if perfect-aim ever needed,
//                            try `Schema.SetSchemaValue<QAngle>(pawn,
//                            "CCSPlayerPawn", "v_angle", angle)` WITHOUT
//                            SetStateChanged. UNVERIFIED — needs probe.
//
//   v0.6.0.9  (2026-05-03) — `m_bHasHelmet` SetStateChanged on
//                            CCSPlayerPawn. "is not networked" warning,
//                            crash at 14:53:19. Same patho as the two
//                            above. Reverted in v0.6.0.11.
//
//   v0.6.0.11 (2026-05-03) — `m_ArmorValue` defensively removed alongside
//                            m_bHasHelmet (uncertain status, removed to
//                            be safe). If armor is needed for a stage,
//                            try giving `item_assaultsuit` via
//                            GiveNamedItem instead — proper item path.
//
// PROVEN-SAFE FIELDS (write + SetStateChanged don't crash):
//   - CCSPlayerPawn.m_flVelocityModifier  (since v0.6.0.2-beta)
//   - CCSPlayerController.m_iPing         (PingDisplay, since v0.5.0)
//   - CBasePlayerController.m_iszPlayerName, extraOffset=0 (FakeClient
//                                                            since v0.4.x)
//
// USAGE:
//   - For new schema writes, prefer typed setters where they exist
//     (e.g. `c.Ping = N`, `pawn.MovementServices.Buttons = ...`).
//   - When a typed setter doesn't exist, use SchemaSafety.WriteAndMark<T>
//     instead of calling Schema.SetSchemaValue<>+SetStateChanged
//     directly. The helper trips on the deny-list above and refuses
//     with a clear error log line — no crash, no silent corruption.
// =============================================================================

using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;

namespace InsanityRevive;

public static class SchemaSafety
{
    // Each entry is "ClassName.FieldName" — used for fields that must be
    // refused under one specific class string but are still safe under
    // others (none today, but kept for future incidents that prove
    // class-specific).
    private static readonly HashSet<string> _denyClassField = new(StringComparer.Ordinal)
    {
    };

    // Field-name-only deny set. The engine's "is not networked" → server
    // crash failure mode is keyed on the underlying field, not the class
    // string the caller hands to Schema. So enumerate the field once here
    // and the deny check fires regardless of which parent-class alias
    // (CBaseEntity, CBasePlayerPawn, CCSPlayerPawnBase, CCSPlayerPawn,
    // CCSPlayerController, …) the caller routes through. This closes the
    // gap from issue #23 where the original "ClassName.FieldName" set had
    // partial parent-class coverage and a future call site routing through
    // an unlisted alias could re-trigger the v0.6.0.6 / .9 / .11 crash class.
    //
    // Trade-off: a legitimate same-named field on an unrelated schema class
    // gets falsely refused. The crashing-field pool here is tiny and these
    // names (m_iTeamNum, m_angEyeAngles, m_bHasHelmet, m_ArmorValue) don't
    // overlap with any field we currently want to write under any class.
    private static readonly HashSet<string> _denyField = new(StringComparer.Ordinal)
    {
        "m_iTeamNum",      // crashes via SetStateChanged. Use SwitchTeam instead.
        "m_angEyeAngles",  // crashes. Use v_angle (untested) or skip.
        "m_bHasHelmet",    // crashes (v0.6.0.9). Use GiveNamedItem("item_assaultsuit").
        "m_ArmorValue",    // defensively removed (v0.6.0.11). Use item give.
    };

    /// <summary>
    /// True if writing this field crashes the server (per incident log).
    /// Catches every parent-class alias the engine accepts for the field,
    /// since the underlying crash is field-keyed, not class-keyed.
    /// </summary>
    public static bool IsDenied(string schemaClass, string fieldName)
        => _denyField.Contains(fieldName)
        || _denyClassField.Contains($"{schemaClass}.{fieldName}");

    /// <summary>
    /// Schema.SetSchemaValue&lt;T&gt; with deny-list gate. Returns true on
    /// success, false on deny or exception. Callers should treat false
    /// as "skip the corresponding SetStateChanged too".
    /// </summary>
    public static bool Write<T>(IntPtr handle, string schemaClass, string fieldName, T value) where T : unmanaged
    {
        if (IsDenied(schemaClass, fieldName))
        {
            Log.Error($"SchemaSafety: REFUSED Write<{typeof(T).Name}>({schemaClass}.{fieldName}) — known-crash field. See SchemaSafety.cs incident log.");
            return false;
        }
        try
        {
            Schema.SetSchemaValue<T>(handle, schemaClass, fieldName, value);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"SchemaSafety: Write<{typeof(T).Name}>({schemaClass}.{fieldName}) threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Utilities.SetStateChanged with deny-list gate. The 3-arg form
    /// (no extraOffset). Returns true on success.
    /// </summary>
    public static bool MarkChanged(CBaseEntity entity, string schemaClass, string fieldName)
    {
        if (entity == null) return false;
        if (IsDenied(schemaClass, fieldName))
        {
            Log.Error($"SchemaSafety: REFUSED MarkChanged({schemaClass}.{fieldName}) — known-crash field.");
            return false;
        }
        try
        {
            Utilities.SetStateChanged(entity, schemaClass, fieldName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"SchemaSafety: MarkChanged({schemaClass}.{fieldName}) threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Same as MarkChanged but with the extraOffset parameter, for fields
    /// like m_iszPlayerName that were historically written with offset 0.
    /// </summary>
    public static bool MarkChanged(CBaseEntity entity, string schemaClass, string fieldName, int extraOffset)
    {
        if (entity == null) return false;
        if (IsDenied(schemaClass, fieldName))
        {
            Log.Error($"SchemaSafety: REFUSED MarkChanged({schemaClass}.{fieldName}, extraOffset={extraOffset}) — known-crash field.");
            return false;
        }
        try
        {
            Utilities.SetStateChanged(entity, schemaClass, fieldName, extraOffset);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"SchemaSafety: MarkChanged({schemaClass}.{fieldName}) threw: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Convenience: write a value AND fire SetStateChanged on the same
    /// field. Both go through the deny gate. If the write fails the
    /// SetStateChanged is skipped. Use when you have the entity AND
    /// the entity's handle (typical: `pawn` and `pawn.Handle`).
    /// </summary>
    public static bool WriteAndMark<T>(CBaseEntity entity, IntPtr handle, string schemaClass, string fieldName, T value) where T : unmanaged
    {
        if (!Write(handle, schemaClass, fieldName, value)) return false;
        return MarkChanged(entity, schemaClass, fieldName);
    }
}
