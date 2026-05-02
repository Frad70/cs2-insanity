using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace InsanityRevive;

/// <summary>
/// Patches bot-vs-bot damage reception to zero.
///
/// Why this exists (v0.6.0.2-beta): the FF fix in v0.6.0.1-beta forced
/// <c>mp_teammates_are_enemies 1</c> at Stage 1 entry so a same-team
/// human takes damage from bots. Side-effect: bots also see EACH OTHER
/// as enemies and prefer the 7 nearest "enemies" (other bots) over the
/// 1 distant human, then proceed to mulch the fleet during Stage 1
/// knife rush. Empirically observed 2026-05-02.
///
/// Fix: hook <c>CBaseEntity::TakeDamage</c> pre-call. If both attacker
/// and victim are managed bots (i.e. their controller slots are in
/// <see cref="FakeClientManager"/>'s registry), zero out the damage and
/// return <see cref="HookResult.Changed"/>. The damage event still fires
/// (visual flinch, blood, ricochet sound — bots still LOOK like they're
/// fighting each other) but no HP is lost.
///
/// Always-on (registered at plugin Load, unhooked at Unload). During
/// normal fleet operation pre-reveal, default mp_friendlyfire=0 already
/// blocks same-team damage, so this hook is a no-op there. During reveal
/// Stage 1+2 it's the load-bearing fix.
///
/// Bot-vs-human and human-vs-bot damage is unaffected (only one side is
/// a managed bot, so the gate trips false and damage flows normally).
/// </summary>
public sealed class BotDamagePatch
{
    private readonly FakeClientManager _mgr;
    private bool _hooked;

    public BotDamagePatch(FakeClientManager mgr) => _mgr = mgr;

    public void Install()
    {
        if (_hooked) return;
        try {
            VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(OnTakeDamage, HookMode.Pre);
            _hooked = true;
            Log.Info("BotDamagePatch installed (CBaseEntity_TakeDamageOldFunc PRE-hook)");
        } catch (Exception ex) {
            Log.Error($"BotDamagePatch install: {ex.Message}");
        }
    }

    public void Uninstall()
    {
        if (!_hooked) return;
        try {
            VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Unhook(OnTakeDamage, HookMode.Pre);
        } catch (Exception ex) {
            Log.Debug($"BotDamagePatch unhook: {ex.Message}");
        }
        _hooked = false;
    }

    private HookResult OnTakeDamage(DynamicHook hook)
    {
        try {
            var info = hook.GetParam<CTakeDamageInfo>(1);
            // Damage info has Attacker (CHandle<CBaseEntity>). Resolve
            // to CCSPlayerPawn → CCSPlayerController → slot.
            var attackerEnt = info.Attacker.Value;
            if (attackerEnt is not CCSPlayerPawn attackerPawn) return HookResult.Continue;
            var attackerCtrl = attackerPawn.Controller.Value as CCSPlayerController;
            if (attackerCtrl == null || !attackerCtrl.IsValid) return HookResult.Continue;

            // Victim is the entity whose TakeDamage is being called — param 0
            // is the implicit `this` pointer in the virtual call (CEntityInstance).
            var victimInst = hook.GetParam<CEntityInstance>(0);
            // CCSPlayerPawn IS-A CEntityInstance — direct cast.
            if (victimInst is not CCSPlayerPawn victimPawn) return HookResult.Continue;
            var victimCtrl = victimPawn.Controller.Value as CCSPlayerController;
            if (victimCtrl == null || !victimCtrl.IsValid) return HookResult.Continue;

            // Self-damage (grenades, falls, world): allow — the attacker
            // and victim being "the same managed bot" should still take
            // legit env damage. Only block bot-on-OTHER-bot damage.
            if (attackerCtrl.Slot == victimCtrl.Slot) return HookResult.Continue;

            bool attackerIsManagedBot = _mgr.FindBySlot((int)attackerCtrl.Slot) != null;
            bool victimIsManagedBot   = _mgr.FindBySlot((int)victimCtrl.Slot) != null;

            if (attackerIsManagedBot && victimIsManagedBot)
            {
                info.Damage = 0f;
                return HookResult.Changed;
            }
        } catch (Exception ex) {
            Log.Debug($"OnTakeDamage filter: {ex.Message}");
        }
        return HookResult.Continue;
    }
}
