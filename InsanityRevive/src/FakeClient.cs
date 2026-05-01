using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace InsanityRevive;

public enum FakeTeam { CT = 3, T = 2 }

// One bot. Owns the per-bot subsystems but knows nothing about how it
// was spawned, despawned, or scheduled — that's the Manager's job.
public sealed class FakeClient
{
    public int    Id        { get; }
    public string Name      { get; private set; }
    public ulong  SteamId64 { get; }
    public FakeTeam Team    { get; private set; }
    public int    Slot      { get; internal set; }
    public bool   Alive     { get; internal set; }

    public NetworkProfile    Profile     { get; }
    public NetworkSimulator  Simulator   { get; }
    public InputBuffer       Buffer      { get; }
    public PingDisplay       PingView    { get; }

    // 1s rolling stats for net_summary record
    private int _summaryWindowSamples;
    private long _summaryLatencyAcc;
    private int _summaryLossEvents;

    public FakeClient(int id, string name, ulong steamId, FakeTeam team, NetworkProfile profile)
    {
        Id        = id;
        Name      = name;
        SteamId64 = steamId;
        Team      = team;
        Slot      = -1;
        Alive     = false;
        Profile   = profile;
        Simulator = new NetworkSimulator(profile, steamId ^ 0xFEEDFACECAFEBEEFUL);
        Buffer    = new InputBuffer();
        PingView  = new PingDisplay();
    }

    // controller is looked up by the Manager once per tick (not cached:
    // entity handles are unstable across respawn / mapchange).
    public void Tick(int currentTick, CCSPlayerController? controller)
    {
        Simulator.Tick();

        if (Simulator.LossThisTick) Buffer.DropOldest();

        PingView.RecordSample(Simulator.CurrentLatencyMs);
        PingView.MaybeWrite(controller);

        _summaryWindowSamples++;
        _summaryLatencyAcc += Simulator.CurrentLatencyMs;
        if (Simulator.LossThisTick) _summaryLossEvents++;
    }

    public (int avgPing, double lossRate) DrainSummary()
    {
        if (_summaryWindowSamples == 0) return (Simulator.CurrentLatencyMs, 0.0);
        var avg = (int)(_summaryLatencyAcc / _summaryWindowSamples);
        var rate = (double)_summaryLossEvents / _summaryWindowSamples;
        _summaryWindowSamples = 0;
        _summaryLatencyAcc = 0;
        _summaryLossEvents = 0;
        return (avg, rate);
    }

    public void OverwriteIdentityOnController(CCSPlayerController c)
    {
        if (c == null || !c.IsValid) return;
        OverwriteNameOnController(c);
        // m_steamID is server-side only (CSSharp warns "not networked"
        // when SetStateChanged is called on it), so the synthetic ID
        // never reaches clients. The engine continues to report the
        // engine-issued BOT id for fake clients in the netchan layer.
        // We still write it so server-side code that reads SteamID via
        // schema sees our value, but skip the SetStateChanged call.
        try { c.SteamID = SteamId64; }
        catch (Exception ex) { Log.Debug($"steamID overwrite slot={Slot}: {ex.Message}"); }

        // Tried and reverted — none affect the scoreboard BOT icon:
        //   m_iCompetitiveRanking/RankType/Wins — networked, gives no
        //     visible rank icon for CCSPlayerController bots in CS2.
        //   m_iEFlags & ~EFL_FAKE_PLAYER — server-side only.
        //   m_iConnected = Connected — networked but ignored by HUD.
        //   m_flSmoothedPing — server-side only.
        // The icon is gated by engine-side IsFakeClient(slot), reachable
        // only via memory patch / Metamod companion — out of scope here.
    }

    public void OverwriteNameOnController(CCSPlayerController c)
    {
        if (c == null || !c.IsValid) return;
        try
        {
            c.PlayerName = Name;
            Utilities.SetStateChanged(c, "CBasePlayerController", "m_iszPlayerName", 0);
        }
        catch (Exception ex) { Log.Debug($"name overwrite slot={Slot}: {ex.Message}"); }
    }
}
