using System;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;

namespace InsanityRevive;

[MinimumApiVersion(220)]
public sealed class InsanityRevivePlugin : BasePlugin
{
    public override string ModuleName    => "InsanityRevive";
    public override string ModuleVersion => "v3.0.0-base";
    public override string ModuleAuthor  => "frad70";

    private Telemetry?           _telemetry;
    private Config?              _config;
    private FakeClientManager?   _manager;

    public override void Load(bool hotReload)
    {
        _config = new Config();
        Log.SetLevel(_config.LogLevel);

        _telemetry = new Telemetry(_config.TelemetryPath);
        _manager = new FakeClientManager(this, _config, _telemetry);
        _manager.OnLoad(Api.GetVersionString());

        // Without this, the server hibernates when no real players are
        // connected — game frames stop, Server.NextFrame queue stalls,
        // and bot_add adopt callbacks never fire. Bots only "appear"
        // once a human joins. Force ticks to keep flowing.
        Server.ExecuteCommand("sv_hibernate_when_empty 0");

        RegisterListener<Listeners.OnTick>(_manager.OnTick);
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            try { _manager?.OnMapStart(); }
            catch (Exception ex) { Log.Error($"OnMapStart: {ex.Message}"); }
        });
        RegisterListener<Listeners.OnClientConnected>(slot => _manager?.OnClientConnected(slot));
        RegisterListener<Listeners.OnClientPutInServer>(slot => _manager?.OnClientPutInServer(slot));
        RegisterListener<Listeners.OnClientDisconnect>(slot => _manager?.OnClientDisconnect(slot));

        // P/12 Reveal Finale (v0.6.0-beta): EventPlayerDeath dispatches to
        // RevealController for bot-kill counter (Stage 2 trigger threshold)
        // and human-death detection (slowmo death cam in Stage 2 + Stage 3
        // trigger when last human dies).
        RegisterEventHandler<EventPlayerDeath>((@event, info) => {
            try {
                var victim = @event.Userid;
                if (victim == null || !victim.IsValid) return HookResult.Continue;
                var isBot = victim.IsBot
                    || (_manager?.FindBySlot((int)victim.Slot) != null);
                _manager?.Reveal.OnPlayerDeath((int)victim.Slot, isBot);

                // BotProfile dynamic-state notify: tilt the victim if it's
                // a managed bot, reward the attacker if it's also a bot.
                // Modules reading CurrentAimSkill / CurrentReactionMs see
                // the resulting drift in following ticks.
                var victimFc = _manager?.FindBySlot((int)victim.Slot);
                victimFc?.Profile.NotifyEvent("Death");
                var attacker = @event.Attacker;
                if (attacker != null && attacker.IsValid && attacker.Slot != victim.Slot) {
                    var attackerFc = _manager?.FindBySlot((int)attacker.Slot);
                    attackerFc?.Profile.NotifyEvent("Kill");
                }
            } catch (Exception ex) { Log.Debug($"EventPlayerDeath dispatch: {ex.Message}"); }
            return HookResult.Continue;
        });

        Log.Info($"loaded — telemetry={_telemetry.Path} session={_telemetry.SessionId} " +
                 $"detour={_manager.DetourInstalled} steamIdMode={_manager.SteamIds.Mode}");

        // Vanilla `bot_kick` (no args) kicks every bot, but engine state
        // doesn't tell FleetManager — Reconcile() repopulates within ~1s.
        // Intercept the bare form, drain the fleet through the plugin,
        // and pin FleetSize=0 so Reconcile holds the empty state until
        // the user restores it via `insanity_fleet_size N`. Targeted
        // form (`bot_kick <name>`) is left alone — that's how we kick
        // individual bots ourselves and how admins surgically drop one.
        AddCommandListener("bot_kick", (caller, info) => {
            if (_manager == null) return HookResult.Continue;
            if (info.ArgCount > 1) return HookResult.Continue; // targeted, leave alone
            try {
                var n = _manager.DespawnAll("vanilla_bot_kick");
                _manager.Config.SetFleetSizeOverride(0);
                Log.Info($"vanilla bot_kick intercepted: drained {n}, fleet pinned to 0 — `insanity_fleet_size N` to restore");
            } catch (Exception ex) { Log.Error($"bot_kick listener: {ex.Message}"); }
            return HookResult.Continue;
        }, HookMode.Pre);

        // Hot reload: OnClientPutInServer will not fire for bots that
        // are already on the server, so adopt them now in one pass.
        if (hotReload) {
            _manager.AdoptExistingBots();
            // OnMapStart won't fire on hot-reload (map already loaded),
            // so arm FleetManager directly. Reconcile will run from
            // Tick at 1Hz and spawn up to FleetSize.
            _manager.Fleet.OnMapStartComplete();
        }
    }

    public override void Unload(bool hotReload)
    {
        try { _manager?.Dispose(); } catch { }
        try { _telemetry?.Dispose(); } catch { }
        _manager = null; _telemetry = null;
    }

    [ConsoleCommand("insanity_spawn_bots", "Spawn N fake bots split across teams")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[count]")]
    public void OnSpawnBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var n = _config?.DefaultBotCount ?? 5;
        if (info.ArgCount > 1 && int.TryParse(info.GetArg(1), out var parsed)) n = Math.Clamp(parsed, 1, 32);

        for (var i = 0; i < n; i++)
        {
            var team = (i % 2 == 0) ? FakeTeam.CT : FakeTeam.T;
            _manager.Spawn(team);
        }
        info.ReplyToCommand($"[Insanity] queued {n} bot_add commands; see scoreboard in ~1s");
    }

    [ConsoleCommand("insanity_kick_bots", "Kick all fake bots; pins FleetSize=0 unless 'respawn' arg given")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[respawn]")]
    public void OnKickBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        bool respawn = info.ArgCount > 1
            && info.GetArg(1).Trim().Equals("respawn", StringComparison.OrdinalIgnoreCase);
        var n = _manager.DespawnAll(respawn ? "admin_kick_respawn" : "admin_kick_drain");
        if (!respawn)
        {
            // Pin to 0 so FleetManager.Reconcile holds the empty state.
            // Without this the fleet repopulates within 1 second.
            _manager.Config.SetFleetSizeOverride(0);
            info.ReplyToCommand($"[Insanity] kicked {n} fake bots; fleet drained — use `insanity_fleet_size N` to restore");
        }
        else
        {
            // `respawn` is an explicit user intent to "return to normal size".
            // If a prior drain (vanilla bot_kick or `insanity_kick_bots`) left
            // override pinned to 0, FleetSize would still report 0 and the
            // fleet wouldn't repopulate — message would lie. Clear override
            // first so the cfg-file FleetSize takes over.
            _manager.Config.SetFleetSizeOverride(null);
            info.ReplyToCommand($"[Insanity] kicked {n} fake bots; fleet will respawn (size={_manager.Config.FleetSize})");
        }
    }

    [ConsoleCommand("insanity_fleet_size", "Set FleetSize override at runtime (0..16); 'default' clears override")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "<0..16|default>")]
    public void OnFleetSize(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (info.ArgCount < 2)
        {
            var ovr = _manager.Config.HasFleetSizeOverride
                ? _manager.Config.FleetSizeOverride!.Value.ToString()
                : "(none — using cfg)";
            info.ReplyToCommand($"[Insanity] fleet size={_manager.Config.FleetSize} override={ovr} active={_manager.All.Count} pending={_manager.PendingPersonaCount}");
            return;
        }
        var arg = info.GetArg(1).Trim();
        if (arg.Equals("default", StringComparison.OrdinalIgnoreCase) || arg == "-1")
        {
            _manager.Config.SetFleetSizeOverride(null);
            info.ReplyToCommand($"[Insanity] fleet size override cleared — using cfg ({_manager.Config.FleetSize})");
            return;
        }
        if (!int.TryParse(arg, out var n))
        {
            info.ReplyToCommand($"[Insanity] usage: insanity_fleet_size <0..16|default>");
            return;
        }
        var clamped = Math.Clamp(n, 0, 16);
        _manager.Config.SetFleetSizeOverride(clamped);
        info.ReplyToCommand($"[Insanity] fleet size override = {clamped} (was active={_manager.All.Count} pending={_manager.PendingPersonaCount})");
    }

    [ConsoleCommand("insanity_status", "Print fake-client manager status")]
    [RequiresPermissions("@css/generic")]
    public void OnStatus(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var ovrLabel = _manager.Config.HasFleetSizeOverride
            ? $" (override={_manager.Config.FleetSizeOverride})"
            : "";
        info.ReplyToCommand($"[Insanity] bots={_manager.All.Count} pending={_manager.PendingPersonaCount} " +
                            $"target={_manager.Config.FleetSize}{ovrLabel} " +
                            $"detour={_manager.DetourInstalled} " +
                            $"steamIdMode={_manager.SteamIds.Mode} telemetry={_telemetry?.Path}");
        if (_manager.All.Count == 0
            && _manager.Config.HasFleetSizeOverride
            && _manager.Config.FleetSizeOverride == 0)
        {
            info.ReplyToCommand("  (fleet drained — `insanity_fleet_size N` or `insanity_kick_bots respawn` to restore)");
        }
        foreach (var fc in _manager.All.Take(16))
        {
            string schemaName = "?";
            try
            {
                var c = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(fc.Slot);
                if (c != null && c.IsValid) schemaName = c.PlayerName ?? "<null>";
            } catch { }
            info.ReplyToCommand($"  #{fc.Id} target={fc.Name} schemaName={schemaName} slot={fc.Slot} " +
                                $"ping={fc.PingView.LastWrittenPing}ms " +
                                $"net={fc.Network.Type} arch={fc.Profile.Archetype} skill={fc.Profile.SkillRating} " +
                                $"mood={fc.Profile.Mood} tilt={fc.Profile.Tilt}");
        }
        info.ReplyToCommand($"[Insanity] hider active={_manager.IsHiderActive()}");
    }

    // P/12 Reveal Finale entry. Two registrations:
    //   - `insanity_reveal` — rcon / server console
    //   - `css_reveal`      — chat trigger `!reveal` (CSSharp's css_ prefix
    //                          maps `css_NAME` to `!NAME` chat command)
    // Permission @css/root — admin-only.
    [ConsoleCommand("insanity_reveal", "Trigger reveal finale state machine")]
    [ConsoleCommand("css_reveal", "Trigger reveal finale state machine (chat: !reveal)")]
    [RequiresPermissions("@css/root")]
    public void OnReveal(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var prevStage = _manager.Reveal.Stage;
        _manager.Reveal.Start();
        info.ReplyToCommand($"[Insanity] reveal: prev={prevStage} → Stage0");
    }

    // P/12 Stage 4 APOCALYPSE manual trigger (v0.7.0-beta — 2026-05-08).
    // Requires reveal already active (Stage 1/2/3); transitions to Stage 4.
    [ConsoleCommand("insanity_reveal_apocalypse", "Trigger Stage 4 (C4 suicide bots) — requires active reveal")]
    [ConsoleCommand("css_reveal_apocalypse", "Trigger Stage 4 (chat: !reveal_apocalypse)")]
    [RequiresPermissions("@css/root")]
    public void OnRevealApocalypse(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var prevStage = _manager.Reveal.Stage;
        bool ok = _manager.Reveal.StartApocalypse();
        info.ReplyToCommand(ok
            ? $"[Insanity] APOCALYPSE: prev={prevStage} → Stage4"
            : $"[Insanity] APOCALYPSE refused (stage={prevStage}); start a reveal first");
    }

    // ──────────────────────────────────────────────────────────────────
    // Stage 4 probes (temporary live-verification commands; see Probe.cs)
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("insanity_probe_glow", "Probe 1: tint a bot's pawn red via m_clrRender")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot> [r g b]")]
    public void OnProbeGlow(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[probe] usage: insanity_probe_glow <slot> [r g b]");
            return;
        }
        byte r = 255, g = 0, b = 0;
        if (info.ArgCount >= 5
            && byte.TryParse(info.GetArg(2), out var pr)
            && byte.TryParse(info.GetArg(3), out var pg)
            && byte.TryParse(info.GetArg(4), out var pb)) { r = pr; g = pg; b = pb; }
        info.ReplyToCommand($"[probe] {Probe.Glow(slot, r, g, b)}");
    }

    [ConsoleCommand("insanity_probe_c4", "Probe 2: GiveNamedItem(weapon_c4) on a bot")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot>")]
    public void OnProbeC4(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[probe] usage: insanity_probe_c4 <slot>");
            return;
        }
        info.ReplyToCommand($"[probe] {Probe.GiveC4(slot)}");
    }

    [ConsoleCommand("insanity_probe_hurtzero", "Probe 3: arm/disarm BotDamagePatch as alt damage filter")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 0, usage: "[arm|disarm]")]
    public void OnProbeHurtZero(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "arm";
        var msg = arg == "disarm" ? Probe.HurtZeroDisarm(_manager) : Probe.HurtZeroArmOnce(_manager);
        info.ReplyToCommand($"[probe] {msg}");
    }



    // ──────────────────────────────────────────────────────────────────
    // BotProfile inspection (the umbrella structure introduced 2026-05-08)
    // ──────────────────────────────────────────────────────────────────

    [ConsoleCommand("insanity_profile", "Print full BotProfile dump for a bot")]
    [ConsoleCommand("css_profile", "Print BotProfile (chat: !profile <slot>)")]
    [RequiresPermissions("@css/cheats")]
    [CommandHelper(minArgs: 1, usage: "<slot>")]
    public void OnProfile(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (!int.TryParse(info.GetArg(1), out var slot)) {
            info.ReplyToCommand("[Insanity] usage: insanity_profile <slot>");
            return;
        }
        var fc = _manager.FindBySlot(slot);
        if (fc == null) {
            info.ReplyToCommand($"[Insanity] no managed bot at slot {slot}");
            return;
        }
        info.ReplyToCommand($"[Insanity] BotProfile for #{fc.Id} {fc.Name} (slot={slot}):");
        foreach (var line in fc.Profile.DebugDump().Split('\n'))
            info.ReplyToCommand(line);
        info.ReplyToCommand($"  simulator:    {fc.Simulator.DebugStateString()}");
    }

    [ConsoleCommand("insanity_hider_active", "Toggle InsanityHider BOT-icon hiding (0/1)")]
    [RequiresPermissions("@css/generic")]
    public void OnHiderActive(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        if (info.ArgCount < 2)
        {
            info.ReplyToCommand($"[Insanity] hider active={_manager.IsHiderActive()} (usage: insanity_hider_active 0|1)");
            return;
        }
        bool on = info.GetArg(1).Trim() is "1" or "true" or "on";
        _manager.SetHiderActive(on);
        info.ReplyToCommand($"[Insanity] hider active={on}");
    }
}
