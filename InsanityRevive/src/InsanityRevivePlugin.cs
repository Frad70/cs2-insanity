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
        _manager = new FakeClientManager(_config, _telemetry);
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
            } catch (Exception ex) { Log.Debug($"EventPlayerDeath dispatch: {ex.Message}"); }
            return HookResult.Continue;
        });

        Log.Info($"loaded — telemetry={_telemetry.Path} session={_telemetry.SessionId} " +
                 $"detour={_manager.DetourInstalled} steamIdMode={_manager.SteamIds.Mode}");

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

    [ConsoleCommand("insanity_kick_bots", "Kick all fake bots managed by this plugin")]
    [RequiresPermissions("@css/cheats")]
    public void OnKickBots(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        var n = _manager.DespawnAll("admin_kick");
        info.ReplyToCommand($"[Insanity] kicked {n} fake bots");
    }

    [ConsoleCommand("insanity_status", "Print fake-client manager status")]
    [RequiresPermissions("@css/generic")]
    public void OnStatus(CCSPlayerController? caller, CommandInfo info)
    {
        if (_manager == null) { info.ReplyToCommand("[Insanity] not loaded"); return; }
        info.ReplyToCommand($"[Insanity] bots={_manager.All.Count} detour={_manager.DetourInstalled} " +
                            $"steamIdMode={_manager.SteamIds.Mode} telemetry={_telemetry?.Path}");
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
                                $"profile=base{fc.Profile.BaseLatencyMs}/jit{fc.Profile.JitterRangeMs}");
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
