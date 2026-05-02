using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace InsanityRevive;

public sealed class BotIdentity
{
    public required string Name { get; init; }
    public required ulong SteamId { get; init; }
    public required FakeTeam Team { get; init; }
    public required NetworkProfile Profile { get; init; }
}

// Singleton. Owns lifecycle, the BotNavIgnore patch, the detour, the
// per-tick fan-out, and the mapchange-survival cache.
public sealed class FakeClientManager : IDisposable
{
    private static readonly string[] NamePool = new[]
    {
        "kennyS","Magisk","ZywOo","s1mple","NiKo","device","electronic","blameF",
        "frozen","huNter","rain","Twistzz","ropz","Brollan","Aleksib","broky",
        "FaNg","donk","sh1ro","jks","Boombl4","Ax1Le","Hobbit","n0rb3r7",
        "Spinx","torzsi","stavn","TeSeS","kyxsan","snappi","k1to","sjuush",
    };

    public Telemetry Telemetry { get; }
    public Config Config { get; }
    public ISteamIdProvider SteamIds { get; }
    public ProcessUsercmdsDetour Detour { get; }
    public bool DetourInstalled => Detour.Installed;

    private readonly MemoryPatch _navPatch = new("BotNavIgnore");
    private readonly PoolMmap _pool = new();
    private readonly Dictionary<int, FakeClient> _byId = new();
    private readonly HashSet<ulong> _usedSteamIds = new();
    private readonly List<BotIdentity> _survivalCache = new();
    // Identities surviving a mapchange. OnClientPutInServer pops from
    // here first so restored bots keep their pre-mapchange identity;
    // when empty, fresh identities are minted instead.
    private readonly Queue<BotIdentity> _restoreQueue = new();
    // Counter for source="command" vs "engine_quota" tagging. Spawn()
    // bumps it; AdoptController consumes one. Refused bot_adds may leak
    // a count → next engine bot is mis-tagged. Accepted as race-free.
    private int _commandSpawnsPending;
    private int _nextId = 1;
    private int _tick;
    private int _ticksSinceSummary;
    private bool _navPatched;

    public IReadOnlyCollection<FakeClient> All => _byId.Values;

    public FakeClientManager(Config cfg, Telemetry telemetry)
    {
        Config = cfg;
        Telemetry = telemetry;
        SteamIds = SteamIdProviderFactory.Create(cfg, telemetry.SessionId);
        Detour = new ProcessUsercmdsDetour();
    }

    public void OnLoad(string csVersion)
    {
        // PoolMmap drives the InsanityHider C++ side: it reads the pool
        // on OnClientConnected and patches m_bFakePlayer for managed slots.
        if (!_pool.Open("/tmp/insanityrevive_fake_slots.bin"))
            Log.Warn("PoolMmap not opened — bots will keep BOT-icon");

        var detourOk = Detour.Install();
        Log.Info($"detour ProcessUsercmds: {(detourOk ? "ok" : Detour.InstallError)}");
        Telemetry.Write("detour_install", new Dictionary<string, object?> {
            { "target", "ProcessUsercmds" }, { "success", detourOk },
            { "reason", detourOk ? "ok" : (Detour.InstallError ?? "unknown") },
            { "behavior", "accounting_only" } });

        bool patchOk = false; string patchReason = "disabled_in_cfg";
        if (Config.ApplyBotNavPatch)
        {
            Log.Warn("BotNavIgnore patch ENABLED — version-fragile, may crash");
            patchOk = _navPatch.Apply("BotNavIgnore", new byte[] { 0xEB });
            _navPatched = patchOk;
            patchReason = patchOk ? "ok" : (_navPatch.Error ?? "unknown");
            Log.Info($"patch BotNavIgnore: {patchReason}");
        }
        Telemetry.Write("patch_install", new Dictionary<string, object?> {
            { "target", "BotNavIgnore" }, { "success", patchOk }, { "reason", patchReason } });
        Telemetry.Write("boot", new Dictionary<string, object?> {
            { "mode", "A+" }, { "variant", "accounting_only" },
            { "steamIdMode", SteamIds.Mode }, { "csVersion", csVersion } });
    }

    public void OnUnload()
    {
        foreach (var id in _byId.Keys.ToArray()) Despawn(id, "shutdown");
        Detour.Uninstall(); _navPatch.Undo();
        _pool.Close();
    }

    // Spawn: pick next persona, push to FIFO (consumed by C++ Hider's
    // CFC PRE override). Engine processes bot_add and asks for fake-client
    // creation; C++ pops our persona and replaces engine's bot_names.txt
    // pick natively, so userinfo broadcast carries the persona name.
    public void Spawn(FakeTeam team)
    {
        var persona = PickName(_nextId + _commandSpawnsPending);
        if (!_pool.PushFifo(persona))
        {
            Log.Warn($"Spawn: FIFO full ({PoolMmap.FifoCapacity}), drop");
            return;
        }
        _commandSpawnsPending++;
        Server.ExecuteCommand(team == FakeTeam.CT ? "bot_add ct" : "bot_add t");
    }

    public void SetHiderActive(bool active)
    {
        _pool.WriteActive(active);
        Log.Info($"InsanityHider {(active ? "enabled" : "disabled")} (pool kill-switch)");
    }

    public bool IsHiderActive() => _pool.ReadActive();

    // Late-adopt: when a bot connects without our Spawn() pre-mark
    // (engine_quota / autoteambalance / mp_warmup / manual bot_add),
    // C++ Hider's OCC post-hook fires too early to see pool[slot]==1.
    // We mark the pool here, between OCC and the C++ Hider's CPiS post-
    // hook — the latter then fires with pool[slot]==1 and writes byte 160.
    public void OnClientConnected(int slot)
    {
        // Pool managed/name marking moved to C++ Hider (CFC PRE → OCC mark).
        // This listener is now a no-op for the pool path; kept as a hook so
        // the listener registration doesn't 404 in the plugin.
    }

    public void OnClientPutInServer(int slot)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c == null || !c.IsValid || c.IsHLTV) return;

            // Real human guard — they have a Steam-authorized SteamID even
            // after our byte-160 flip. Bots never have one. Defends against
            // orphaned pool[slot]=1 marks from previous bots whose Despawn
            // didn't fire (engine kicks etc.).
            if (c.AuthorizedSteamID != null) return;

            // Gate on pool, not c.IsBot. By the time this listener fires,
            // C++ Hider's CPiS post-hook has already flipped byte 160 for
            // managed slots — c.IsBot would return False. Pool flag is the
            // source of truth for "managed bot" at this point.
            if (_pool.Read(slot) == 0) return;
            if (_byId.Values.Any(b => b.Slot == slot)) return;
            BotIdentity? restore = _restoreQueue.Count > 0 ? _restoreQueue.Dequeue() : null;
            AdoptController(c, restore);
        } catch (Exception ex) { Log.Error($"OnClientPutInServer slot={slot}: {ex.Message}"); }
    }

    public void OnClientDisconnect(int slot)
    {
        try {
            var fc = _byId.Values.FirstOrDefault(b => b.Slot == slot);
            if (fc != null) {
                Despawn(fc.Id, "client_disconnect");
            } else {
                // Orphan cleanup: pool[slot] may have been marked in earlier
                // session (mapchange, plugin reload, etc.) without a matching
                // _byId entry. Without this, the next client to land on this
                // slot — possibly a real human — would inherit the mark and
                // get adopted as a bot. Belt-and-braces with the Authorized
                // SteamID gate in OnClientPutInServer.
                if (_pool.Read(slot) != 0) {
                    _pool.Write(slot, 0);
                    _pool.WriteName(slot, "");
                    Log.Info($"orphan pool cleanup slot={slot}");
                }
            }
        } catch (Exception ex) { Log.Error($"OnClientDisconnect slot={slot}: {ex.Message}"); }
    }

    public void AdoptExistingBots()
    {
        // Hot reload: bots may already have m_bFakePlayer=0 written by a
        // previous Hider session (c.IsBot=False). Accept either signal —
        // the engine's bot bit (live state) OR our pool mark (persisted).
        foreach (var c in Utilities.GetPlayers())
        {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (!c.IsBot && _pool.Read(c.Slot) == 0) continue;
            if (_byId.Values.Any(b => b.Slot == c.Slot)) continue;
            // Make sure pool reflects management before AdoptController.
            if (_pool.Read(c.Slot) == 0) _pool.Write(c.Slot, 1);
            AdoptController(c, _restoreQueue.Count > 0 ? _restoreQueue.Dequeue() : null);
        }
    }

    private void AdoptController(CCSPlayerController ctrl, BotIdentity? restore)
    {
        var id = _nextId++;
        var team = restore?.Team ?? (ctrl.TeamNum == 3 ? FakeTeam.CT : FakeTeam.T);
        bool restored = restore != null;
        // Prefer the name CSSharp's OnClientConnected listener wrote into the
        // pool (so engine-side m_Name and our Schema overwrite agree). Fall
        // back to PickName(id) only if the pool slot wasn't pre-named.
        var poolName = _pool.ReadName(ctrl.Slot);
        var name    = restored ? restore!.Name
                              : (string.IsNullOrEmpty(poolName) ? PickName(id) : poolName);
        var steamId = restored ? restore!.SteamId : SteamIds.Generate(ctrl.Slot);
        var profile = restored ? restore!.Profile : NetworkProfile.Generate(steamId);
        var source = _commandSpawnsPending > 0 ? "command" : "engine_quota";
        if (source == "command") _commandSpawnsPending--;
        var fc = new FakeClient(id, name, steamId, team, profile)
            { Slot = ctrl.Slot, Alive = true };
        _byId[id] = fc;
        _usedSteamIds.Add(steamId);
        // Publish persona name into pool so C++ Hider can call CUtlString::
        // Set on engine-side m_Name. Kills the engine vs Schema mismatch
        // that drove the BOT-icon fallback in the Panorama scoreboard.
        _pool.WriteName(ctrl.Slot, name);
        fc.OverwriteIdentityOnController(ctrl);

        // Engine re-stamps name from bot_names.txt during post-spawn;
        // re-write at +4/+16 ticks (name-only) to outlast that.
        var capFc = fc; var capSlot = fc.Slot;
        Server.RunOnTick(Server.TickCount + 4,  () => ReassertIdentity(capFc, capSlot));
        Server.RunOnTick(Server.TickCount + 16, () => ReassertIdentity(capFc, capSlot));

        // Persist identity for next mapchange.
        _survivalCache.Add(restore ?? new BotIdentity {
            Name = name, SteamId = steamId, Team = team, Profile = profile });
        Telemetry.Write("fake_spawn", new Dictionary<string, object?> {
            { "botId", id }, { "name", name }, { "steamId", steamId.ToString() },
            { "team", team.ToString() }, { "slot", fc.Slot },
            { "profileId", profile.Seed.ToString("x16") },
            { "mode", SteamIds.Mode }, { "source", source }, { "restored", restored } });
    }

    private string PickName(int id)
    {
        var basic = NamePool[id % NamePool.Length];
        var inUse = new HashSet<string>(_byId.Values.Select(b => b.Name), StringComparer.Ordinal);
        if (!inUse.Contains(basic)) return basic;
        for (var i = 2; i < 99; i++)
            if (!inUse.Contains($"{basic}{i}")) return $"{basic}{i}";
        return $"{basic}{id}";
    }

    public void Despawn(int id, string reason)
    {
        if (!_byId.TryGetValue(id, out var fc)) return;
        _byId.Remove(id);
        _pool.Write(fc.Slot, 0);  // un-mark so future engine clients aren't accidentally hidden
        _pool.WriteName(fc.Slot, "");
        Telemetry.Write("fake_despawn", new Dictionary<string, object?> {
            { "botId", id }, { "reason", reason }, { "name", fc.Name }, { "slot", fc.Slot } });
        try {
            var ctrl = Utilities.GetPlayerFromSlot(fc.Slot);
            if (ctrl != null && ctrl.IsValid && ctrl.IsBot)
                Server.ExecuteCommand($"bot_kick {fc.Name}");
        } catch { }
    }

    public int DespawnAll(string reason)
    {
        var n = _byId.Count;
        foreach (var id in _byId.Keys.ToArray()) Despawn(id, reason);
        return n;
    }

    public void OnMapStart()
    {
        // survival_cache → restore_queue: first N respawned bots inherit
        // their pre-mapchange identities.
        _byId.Clear(); _restoreQueue.Clear();
        foreach (var ident in _survivalCache) _restoreQueue.Enqueue(ident);
        _survivalCache.Clear();
    }

    public void OnTick()
    {
        _tick++; _ticksSinceSummary++;
        foreach (var fc in _byId.Values)
        {
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(fc.Slot); } catch { }
            if (c == null || !c.IsValid) { fc.Simulator.Tick(); continue; }
            fc.Tick(_tick, c);
            EmitPerTickTelemetry(fc);
        }
        if (_ticksSinceSummary < 64) return;
        _ticksSinceSummary = 0;
        foreach (var fc in _byId.Values)
        {
            var (avg, loss) = fc.DrainSummary();
            Telemetry.Write("net_summary", new Dictionary<string, object?> {
                { "botId", fc.Id }, { "avgPingMs", avg },
                { "jitterMs", fc.Profile.JitterRangeMs }, { "lossRate60s", loss } });
        }
    }

    private void EmitPerTickTelemetry(FakeClient fc)
    {
        if (fc.Simulator.SpikeStartedThisTick)
            Telemetry.Write("net_spike", new Dictionary<string, object?> {
                { "botId", fc.Id }, { "peakMs", fc.Simulator.LastSpikePeakMs },
                { "durationMs", fc.Simulator.LastSpikeDurationMs }, { "tick", _tick } });
        if (fc.Simulator.LossThisTick)
            Telemetry.Write("net_loss", new Dictionary<string, object?>
                { { "botId", fc.Id }, { "tick", _tick } });
        var bufLoss = fc.Buffer.LastDropReasonLoss;
        var bufOver = fc.Buffer.LastDropReasonOverflow;
        if (bufLoss > 0) Telemetry.Write("buffer_drop", new Dictionary<string, object?>
            { { "botId", fc.Id }, { "reason", "loss" }, { "tick", _tick } });
        if (bufOver > 0) Telemetry.Write("buffer_drop", new Dictionary<string, object?>
            { { "botId", fc.Id }, { "reason", "overflow" }, { "tick", _tick } });
        if (bufLoss > 0 || bufOver > 0) fc.Buffer.Clear();
    }

    private void ReassertIdentity(FakeClient fc, int slot)
    {
        if (!_byId.ContainsKey(fc.Id)) return;
        if (fc.Slot != slot) return;
        try
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c == null || !c.IsValid) return;
            // c.IsBot is now False for managed bots (C++ Hider already wrote
            // m_bFakePlayer=0); pool flag is the trustworthy "managed bot"
            // signal. Same gate fix as OnClientPutInServer.
            if (_pool.Read(slot) == 0 && !c.IsBot) return;
            if (string.Equals(c.PlayerName, fc.Name, StringComparison.Ordinal)) return;
            fc.OverwriteNameOnController(c); // name-only — see FakeClient.cs
        }
        catch (Exception ex) { Log.Debug($"reassert slot={slot}: {ex.Message}"); }
    }

    public void Dispose() { OnUnload(); }
}
