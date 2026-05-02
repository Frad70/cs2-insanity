using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace InsanityRevive;

// Singleton. Owns lifecycle, the BotNavIgnore patch, the detour, the
// per-tick fan-out, and the PersonaRegistry-driven mapchange survival.
//
// v0.5.1+ identity model:
//   - Persona (in PersonaRegistry, JSON-backed) — STABLE across mapchange,
//     plugin reload, and server restart. Carries Name + SteamId + future-
//     phase fields. Identified by monotonic int Id.
//   - FakeClient (in _byId) — VOLATILE, scoped to the current spawn. Holds
//     Slot + per-bot subsystems (Simulator, Buffer, etc.). Links back to
//     its Persona by PersonaId.
//   - _pendingPersonaIds — FIFO of persona ids issued via Spawn(personaId)
//     but not yet adopted. AdoptController dequeues to match the engine's
//     bot_add → CFC PRE → OCC → CPiS arrival order.
public sealed class FakeClientManager : IDisposable
{
    // Fallback name corpus. Used when AcquireForSpawn needs to mint a new
    // persona and the registry is empty (or all personas are reserved).
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
    public PersonaRegistry Registry => _registry;

    private readonly MemoryPatch _navPatch = new("BotNavIgnore");
    private readonly PoolMmap _pool = new();
    private readonly Dictionary<int, FakeClient> _byId = new();
    private readonly HashSet<ulong> _usedSteamIds = new();
    private readonly PersonaRegistry _registry;
    // Personas issued via Spawn() but not yet adopted via AdoptController.
    // FIFO order matches engine's bot_add processing — first push, first adopt.
    private readonly Queue<int> _pendingPersonaIds = new();
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
        _registry = new PersonaRegistry();
    }

    public void OnLoad(string csVersion)
    {
        // PoolMmap drives the InsanityHider C++ side.
        if (!_pool.Open("/tmp/insanityrevive_fake_slots.bin"))
            Log.Warn("PoolMmap not opened — bots will keep BOT-icon");

        // Persistent persona registry — stable identity across server
        // restarts, mapchanges, and plugin reloads.
        _registry.Load();

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
            { "steamIdMode", SteamIds.Mode }, { "csVersion", csVersion },
            { "personaRegistryCount", _registry.Count },
            { "personaRegistryPath", _registry.Path } });
    }

    public void OnUnload()
    {
        foreach (var id in _byId.Keys.ToArray()) Despawn(id, "shutdown");
        Detour.Uninstall(); _navPatch.Undo();
        // Defensive save — every mutation already flushes, but a final
        // pass guards against a race where the last mutation didn't reach
        // disk before shutdown.
        _registry.Save();
        _pool.Close();
    }

    // Spawn flow:
    //   personaId == null  → AcquireForSpawn picks a dormant persona
    //                        (LRU + Id tie-break) or mints a new one.
    //   personaId != null  → restore-style: explicit persona by id, used
    //                        from OnMapStart respawn batch.
    // Pushes persona.Name to FIFO (consumed by C++ Hider's CFC PRE
    // override) and enqueues persona.Id into _pendingPersonaIds so the
    // upcoming OCC/CPiS can bind the correct persona.
    public void Spawn(FakeTeam team, int? personaId = null)
    {
        Persona persona;
        if (personaId.HasValue)
        {
            persona = _registry.GetById(personaId.Value)
                      ?? throw new InvalidOperationException(
                          $"Spawn: persona id={personaId.Value} not in registry");
            persona.LastSeenAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _registry.Save();
        }
        else
        {
            // Reserve names that are currently active OR in-flight as pending
            // so the same name isn't issued twice in a batch.
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _registry.Active) reserved.Add(p.Name);
            foreach (var pid in _pendingPersonaIds)
            {
                var p2 = _registry.GetById(pid);
                if (p2 != null) reserved.Add(p2.Name);
            }
            persona = _registry.AcquireForSpawn(NamePool, reserved);
        }

        if (!_pool.PushFifo(persona.Name))
        {
            Log.Warn($"Spawn: FIFO full ({PoolMmap.FifoCapacity}), drop persona='{persona.Name}'");
            return;
        }
        _pendingPersonaIds.Enqueue(persona.Id);
        _commandSpawnsPending++;
        Server.ExecuteCommand(team == FakeTeam.CT ? "bot_add ct" : "bot_add t");
        Telemetry.Write("spawn_request", new Dictionary<string, object?> {
            { "personaId", persona.Id }, { "name", persona.Name },
            { "team", team.ToString() }, { "explicit", personaId.HasValue } });
    }

    public void SetHiderActive(bool active)
    {
        _pool.WriteActive(active);
        Log.Info($"InsanityHider {(active ? "enabled" : "disabled")} (pool kill-switch)");
    }

    public bool IsHiderActive() => _pool.ReadActive();

    public void OnClientConnected(int slot)
    {
        // Pool managed/name marking is owned by C++ Hider (CFC PRE → OCC mark).
        // This listener is a no-op for the pool path; kept registered so
        // future per-connect logic has a plug-in point.
    }

    public void OnClientPutInServer(int slot)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c == null || !c.IsValid || c.IsHLTV) return;

            // Real human guard — they have a Steam-authorized SteamID even
            // after our byte-160 flip. Bots never have one. Defends against
            // orphaned pool[slot]=1 marks from previous bots whose Despawn
            // didn't fire.
            if (c.AuthorizedSteamID != null) return;

            // Pool flag is the source of truth. By CPiS time, C++ Hider's
            // CPiS post-hook has already flipped byte 160 for managed slots
            // — c.IsBot would return False here.
            if (_pool.Read(slot) == 0) return;
            if (_byId.Values.Any(b => b.Slot == slot)) return;

            AdoptController(c);
        } catch (Exception ex) { Log.Error($"OnClientPutInServer slot={slot}: {ex.Message}"); }
    }

    public void OnClientDisconnect(int slot)
    {
        try {
            // Mapchange survival (v0.5.1+): C++ Hider sets the pool's
            // mapchange flag at IMetamodListener::OnLevelShutdown — earlier
            // than the synthetic OnClientDisconnect cascade fired by
            // PlayerManager::OnLevelEnd inside the StartupServer hook chain
            // for the new map. With the flag set, this is NOT a real
            // disconnect: the engine carries CServerSideClient through as
            // a zombie that will be reactivated on the new map. Preserving
            // _byId, pool[managed]/[name], and registry ActiveOnSlot lets
            // OnMapStart snapshot and re-spawn fresh bots with matching
            // personas.
            if (_pool.IsMapchangeInProgress()) {
                Telemetry.Write("disconnect_skipped_mapchange", new Dictionary<string, object?> {
                    { "slot", slot } });
                return;
            }

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
            AdoptController(c);
        }
    }

    private void AdoptController(CCSPlayerController ctrl)
    {
        var slot = ctrl.Slot;
        var poolName = _pool.ReadName(slot);

        // Resolve persona:
        //  (1) If we have a pending persona id from a recent Spawn() call,
        //      dequeue it. The pool name SHOULD match persona.Name — log if not.
        //  (2) Else (engine_quota path), look up registry by pool name —
        //      a previous-session persona with this name may already exist.
        //  (3) Else mint via AcquireForSpawn using poolName as preferred.
        Persona? persona = null;
        if (_pendingPersonaIds.Count > 0)
        {
            var pid = _pendingPersonaIds.Dequeue();
            persona = _registry.GetById(pid);
            if (persona != null && !string.IsNullOrEmpty(poolName)
                && !string.Equals(poolName, persona.Name, StringComparison.Ordinal))
            {
                Log.Warn($"AdoptController: pool name '{poolName}' != pending persona " +
                         $"'{persona.Name}' (id={persona.Id}, slot={slot}) — using persona");
            }
        }
        if (persona == null && !string.IsNullOrEmpty(poolName))
        {
            persona = _registry.All.FirstOrDefault(p =>
                p.Name == poolName && !p.IsActive);
        }
        if (persona == null)
        {
            // Engine_quota path with no prior persona record. Mint via
            // registry — preferring the name C++ Hider chose from its
            // fallback roster (visible in pool name).
            var reserved = new HashSet<string>(
                _registry.Active.Select(p => p.Name), StringComparer.Ordinal);
            var preferred = !string.IsNullOrEmpty(poolName)
                ? new[] { poolName }
                : NamePool;
            persona = _registry.AcquireForSpawn(preferred, reserved);
        }

        // Persona's stable SteamId — synthesize on first bind, persist.
        if (persona.SteamId64 == 0)
        {
            persona.SteamId64 = SteamIds.Generate(slot);
            _registry.Save();
        }

        // Bind to slot in registry (also updates LastSeenAt).
        _registry.BindToSlot(persona.Id, slot);

        // Build live FakeClient — volatile slot-bound state.
        var id = _nextId++;
        var team = (ctrl.TeamNum == 3 ? FakeTeam.CT : FakeTeam.T);
        var profile = NetworkProfile.Generate(persona.SteamId64);
        var source = _commandSpawnsPending > 0 ? "command" : "engine_quota";
        if (source == "command") _commandSpawnsPending--;

        var fc = new FakeClient(id, persona.Id, persona.Name, persona.SteamId64, team, profile)
            { Slot = slot, Alive = true };
        _byId[id] = fc;
        _usedSteamIds.Add(persona.SteamId64);
        // Publish persona name into pool so C++ Hider's CPiS safety-net
        // can also re-overwrite engine-side m_Name on mapchange-rebuilt
        // CServerSideClient instances (defensive — primary path is CFC PRE).
        _pool.WriteName(slot, persona.Name);
        fc.OverwriteIdentityOnController(ctrl);

        // Engine re-stamps name from bot_names.txt during post-spawn —
        // re-write at +4/+16 ticks (name-only) to outlast that.
        var capFc = fc; var capSlot = fc.Slot;
        Server.RunOnTick(Server.TickCount + 4,  () => ReassertIdentity(capFc, capSlot));
        Server.RunOnTick(Server.TickCount + 16, () => ReassertIdentity(capFc, capSlot));

        Telemetry.Write("fake_spawn", new Dictionary<string, object?> {
            { "botId", id }, { "personaId", persona.Id },
            { "name", persona.Name }, { "steamId", persona.SteamId64.ToString() },
            { "team", team.ToString() }, { "slot", fc.Slot },
            { "profileId", profile.Seed.ToString("x16") },
            { "mode", SteamIds.Mode }, { "source", source },
            { "registryReuse", persona.LastSeenAt != persona.CreatedAt } });
    }

    public void Despawn(int id, string reason)
    {
        if (!_byId.TryGetValue(id, out var fc)) return;
        _byId.Remove(id);
        _registry.ReleaseSlot(fc.PersonaId);
        _pool.Write(fc.Slot, 0);  // un-mark so future engine clients aren't accidentally hidden
        _pool.WriteName(fc.Slot, "");
        Telemetry.Write("fake_despawn", new Dictionary<string, object?> {
            { "botId", id }, { "personaId", fc.PersonaId }, { "reason", reason },
            { "name", fc.Name }, { "slot", fc.Slot } });
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
        try {
            // (1) Snapshot active bots BEFORE clearing. _byId entries were
            //     preserved through the synthetic disconnect cascade because
            //     OnClientDisconnect skipped Despawn while pool.IsMapchangeInProgress.
            var snapshot = _byId.Values
                .Select(b => new RespawnEntry(b.PersonaId, b.Team))
                .ToList();

            // (2) Snapshot zombie slots from pool managed[] BEFORE wipe.
            //     These are the slots whose CServerSideClient instances are
            //     stuck in CHANGELEVEL → CONNECTED state on the new map.
            //     Utilities.GetPlayers() doesn't surface them (no CCSPlayer
            //     Controller for non-active clients), so we MUST use the pool.
            var zombieSlots = new List<int>();
            for (int slot = 0; slot < PoolMmap.Slots; slot++) {
                if (_pool.Read(slot) != 0) zombieSlots.Add(slot);
            }

            // (3) Wipe in-memory state — slots will be re-bound on adopt.
            _byId.Clear();
            _registry.ClearAllActiveSlots();
            _pendingPersonaIds.Clear();

            // (4) Wipe pool managed[] + names — old slot indices are about
            //     to be invalid. CFC PRE / OCC will re-mark fresh slots when
            //     respawned bots arrive.
            foreach (var slot in zombieSlots) {
                _pool.Write(slot, 0);
                _pool.WriteName(slot, "");
            }

            // (5) Clear mapchange flag — synthetic disconnect cascade is done.
            //     Any further OnClientDisconnect goes through real-path.
            _pool.WriteMapchangeFlag(false);

            // (6) Kick zombie engine clients by slot id. For fake-clients,
            //     userid == slot; engine ignores invalid slots without error.
            int kicks = 0;
            foreach (var slot in zombieSlots) {
                try {
                    Server.ExecuteCommand($"kickid {slot}");
                    kicks++;
                } catch (Exception ex) {
                    Log.Debug($"OnMapStart kickid slot={slot}: {ex.Message}");
                }
            }

            Telemetry.Write("mapchange_respawn", new Dictionary<string, object?> {
                { "snapshotCount", snapshot.Count }, { "kicks", kicks },
                { "zombieSlots", string.Join(",", zombieSlots) } });

            // (7) Schedule respawn AFTER kicks settle. Engine processes
            //     kickid in current tick window; spawn a few ticks later
            //     so engine slots are free.
            var tickFire = Server.TickCount + 8;
            foreach (var s in snapshot) {
                var capPid = s.PersonaId; var capTeam = s.Team;
                Server.RunOnTick(tickFire, () => {
                    try { Spawn(capTeam, capPid); }
                    catch (Exception ex) {
                        Log.Error($"OnMapStart respawn pid={capPid}: {ex.Message}");
                    }
                });
            }
        } catch (Exception ex) { Log.Error($"OnMapStart: {ex.Message}"); }
    }

    private readonly record struct RespawnEntry(int PersonaId, FakeTeam Team);

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
