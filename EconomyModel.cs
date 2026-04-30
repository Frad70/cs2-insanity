using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace InsanityRevive;

/// <summary>
/// Per-team economy tracking & buy/save predictions. Driven by EventRoundEnd
/// and the bot's individual money snapshot. Read by DecisionEngine and used
/// to decorate bot behavior — e.g. mark bots as "eco-ing" so they don't push
/// a hopeless save round.
///
/// v0.11 — scaffolded, not yet wired by InsanityRevive.cs (parallel agent
/// branch will wire it once v0.10 movement settles).
/// </summary>
public class EconomyModel
{
    /// <summary>Per-round computed buy plan for the team.</summary>
    public enum BuyPlan
    {
        FullBuy,        // primary + nade + armor for everyone
        ForceBuy,       // partial, accept under-equipped engagement
        Eco,            // pistols only, save money
        SemiEco,        // some bots upgrade, others save
        AntiEco,        // expecting opponent eco — flash + smoke heavy
        Unknown,        // first round / not enough info
    }

    public class TeamEconomy
    {
        public int RoundsLostInARow;        // for "loss bonus" heuristics
        public int CurrentTeamMoneyTotal;
        public int CurrentTeamEquipmentValue;
        public BuyPlan PlannedBuy = BuyPlan.Unknown;
        public bool LastRoundWasGun;        // if true & lost, eco bonus likely
    }

    private readonly Dictionary<CsTeam, TeamEconomy> _teams = new();

    public TeamEconomy GetTeam(CsTeam team)
    {
        if (!_teams.TryGetValue(team, out var t))
        {
            t = new TeamEconomy();
            _teams[team] = t;
        }
        return t;
    }

    /// <summary>Snapshot the team economy at the start of buy time. Pulls live
    /// money from each player. Decides BuyPlan based on totals + history.</summary>
    public void SnapshotForRound(CsTeam team)
    {
        var e = GetTeam(team);
        int total = 0, equip = 0, players = 0, brokeBots = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.Team != team) continue;
            var ms = p.InGameMoneyServices;
            int money = ms?.Account ?? 0;
            total += money;
            players++;
            if (money < 2400) brokeBots++;
            // equipment value: we approximate as inventory weapon prices,
            // but this requires schema lookups. For now, ignore — total
            // money is our main signal.
        }
        e.CurrentTeamMoneyTotal = total;
        if (players == 0) { e.PlannedBuy = BuyPlan.Unknown; return; }
        int avg = total / players;

        // Determine plan
        if (avg >= 4500 && brokeBots <= 1) e.PlannedBuy = BuyPlan.FullBuy;
        else if (avg >= 2800 && brokeBots <= players / 2) e.PlannedBuy = BuyPlan.ForceBuy;
        else if (avg >= 1800) e.PlannedBuy = BuyPlan.SemiEco;
        else if (avg < 1500) e.PlannedBuy = BuyPlan.Eco;
        else e.PlannedBuy = BuyPlan.Eco;

        // Override: if we have more than enough AND opponent is suspected to eco,
        // shift to AntiEco — but we don't know opponent state yet, so we don't
        // do this without a CounterStrike-side signal. Skip for now.
    }

    /// <summary>Bot's individual stance for this round. Drives "should I push?"
    /// and "should I drop money?" decisions.</summary>
    public bool BotShouldEco(CCSPlayerController bot)
    {
        var ms = bot.InGameMoneyServices;
        if (ms == null) return false;
        var team = bot.Team;
        if (team <= CsTeam.Spectator) return false;

        var econ = GetTeam(team);
        // If team plan is Eco, every bot ecos
        if (econ.PlannedBuy == BuyPlan.Eco) return true;
        // If SemiEco, broker bots eco, richer bots full-buy
        if (econ.PlannedBuy == BuyPlan.SemiEco)
            return ms.Account < 3000;
        // Force buy — nobody ecos
        return false;
    }

    /// <summary>Called on EventRoundEnd to bump streaks.</summary>
    public void OnRoundEnd(CsTeam winner)
    {
        var win = GetTeam(winner);
        win.RoundsLostInARow = 0;

        var loserT = (winner == CsTeam.Terrorist) ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        var lose = GetTeam(loserT);
        lose.RoundsLostInARow++;
    }

    /// <summary>Convenience — tag the round as a "gun round" for both teams,
    /// so a future loss-bonus calculation works. Caller decides this from
    /// average equipment value at round-start (we don't compute it here).</summary>
    public void MarkLastRoundType(bool wasGunRound)
    {
        foreach (var t in _teams.Values) t.LastRoundWasGun = wasGunRound;
    }

    /// <summary>How aggressive should anti-eco players be? More aggressive when
    /// expecting an under-equipped opponent (recent loss streak on opponent =
    /// likely eco). Returns multiplier 1.0..1.6.</summary>
    public float AntiEcoBoldness(CsTeam myTeam)
    {
        var oppT = (myTeam == CsTeam.Terrorist) ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        var opp = GetTeam(oppT);
        if (opp.RoundsLostInARow >= 2 && opp.LastRoundWasGun)
            return 1.0f + 0.2f * MathF.Min(3, opp.RoundsLostInARow);
        return 1.0f;
    }
}
