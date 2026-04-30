namespace InsanityRevive;

/// <summary>
/// Per-bot weapon-buy preferences. Real MM players have favorites — one
/// always buys AWP, one only buys AK or M4, one buys Deagle for personality.
/// This module assigns each bot a buy preference set the first time they
/// spawn and consults it later when overriding default buy commands.
///
/// v0.11 — scaffold. The host plugin will eventually issue
/// `bot_buy_AWP @bot` / equivalent commands at start of buy time.
/// </summary>
public class BuyPreferences
{
    public enum PreferredPrimary
    {
        AKorM4,         // standard rifle — engine default
        AWP,            // wants the AWP every full buy
        AutoSniper,     // SCAR / G3SG (rare)
        SMG,            // MP9/MAC10/UMP — eco buyer
        Shotgun,        // Mag-7/XM1014 (rare)
        Scout,          // SSG-08
        ChickenLover,   // famas/galil — broke / ESL persona
    }

    public enum PreferredSecondary
    {
        Default,        // glock/usp at start, p250 mid
        Deagle,         // always deagle
        Tec9,           // T-side: tec9 spam
        FiveSeven,      // CT: 5-7
        DualBerettas,   // always dualies (degenerate)
        CZ75,           // pocket auto
    }

    public class BotBuyPrefs
    {
        public PreferredPrimary Primary;
        public PreferredSecondary Secondary;
        public bool BuysFlashes;
        public bool BuysSmokes;
        public bool BuysMolotovs;
        public bool BuysHE;
        public bool BuysArmor;
        public bool BuysHelmet;
        public bool BuysDefuser;        // CT only
        public float BuyForceTendency;  // 0..1 — how often to force-buy
        /// Some bots buy nothing weird, others buy random (XM1014 on full buy etc).
        public bool ChaoticBuys;
    }

    private readonly Dictionary<int, BotBuyPrefs> _prefs = new();

    public BotBuyPrefs GetOrRoll(int slot, BotPersona persona, Random rng)
    {
        if (_prefs.TryGetValue(slot, out var existing)) return existing;

        var bp = new BotBuyPrefs();

        // Primary roll — biased by archetype
        float primaryRoll = (float)rng.NextDouble();
        switch (persona.Archetype)
        {
            case BotArchetype.AwperPassive:
            case BotArchetype.AwperAggro:
                bp.Primary = primaryRoll < 0.85 ? PreferredPrimary.AWP
                            : primaryRoll < 0.95 ? PreferredPrimary.Scout
                            : PreferredPrimary.AKorM4;
                break;
            case BotArchetype.HeadshotOnly:
                bp.Primary = primaryRoll < 0.40 ? PreferredPrimary.Scout
                            : primaryRoll < 0.95 ? PreferredPrimary.AKorM4
                            : PreferredPrimary.AutoSniper;
                break;
            case BotArchetype.Entry:
            case BotArchetype.IGL:
            case BotArchetype.Anchor:
            case BotArchetype.Lurker:
            case BotArchetype.Support:
                bp.Primary = primaryRoll < 0.85 ? PreferredPrimary.AKorM4
                            : primaryRoll < 0.92 ? PreferredPrimary.SMG
                            : primaryRoll < 0.97 ? PreferredPrimary.Scout
                            : PreferredPrimary.Shotgun;
                break;
            default:
                bp.Primary = primaryRoll < 0.92 ? PreferredPrimary.AKorM4
                            : primaryRoll < 0.96 ? PreferredPrimary.SMG
                            : PreferredPrimary.ChickenLover;
                break;
        }

        // Secondary roll — by persona Style for color
        float secRoll = (float)rng.NextDouble();
        bp.Secondary = persona.Style switch
        {
            BotStyle.DemonRager => secRoll < 0.55 ? PreferredSecondary.Deagle
                                : secRoll < 0.75 ? PreferredSecondary.DualBerettas
                                : PreferredSecondary.Default,
            BotStyle.KidRage    => secRoll < 0.40 ? PreferredSecondary.Deagle
                                : secRoll < 0.55 ? PreferredSecondary.Tec9
                                : PreferredSecondary.Default,
            BotStyle.GenZ       => secRoll < 0.30 ? PreferredSecondary.Deagle
                                : PreferredSecondary.Default,
            BotStyle.ESL        => secRoll < 0.20 ? PreferredSecondary.CZ75
                                : PreferredSecondary.Default,
            _                   => secRoll < 0.15 ? PreferredSecondary.Deagle
                                : PreferredSecondary.Default,
        };

        // Utility — high-skill / IGL buys more nades
        float skill = persona.Skill;
        bp.BuysFlashes  = rng.NextDouble() < 0.55 + (skill - 1.0f) * 0.20f;
        bp.BuysSmokes   = rng.NextDouble() < 0.65 + (skill - 1.0f) * 0.20f;
        bp.BuysMolotovs = rng.NextDouble() < 0.30 + (skill - 1.0f) * 0.15f;
        bp.BuysHE       = rng.NextDouble() < 0.40;
        bp.BuysArmor    = rng.NextDouble() < 0.95;
        bp.BuysHelmet   = bp.BuysArmor && rng.NextDouble() < 0.85;
        bp.BuysDefuser  = rng.NextDouble() < 0.90;     // most CTs buy

        bp.BuyForceTendency = persona.Archetype switch
        {
            BotArchetype.Entry      => 0.55f,
            BotArchetype.AwperAggro => 0.40f,
            BotArchetype.Anchor     => 0.20f,
            BotArchetype.BaitOMatic => 0.10f,
            _                       => 0.30f,
        };
        bp.BuyForceTendency += ((float)rng.NextDouble() - 0.5f) * 0.20f;
        bp.BuyForceTendency = Math.Clamp(bp.BuyForceTendency, 0.05f, 0.85f);

        bp.ChaoticBuys = rng.NextDouble() < 0.04;     // 4% have weird buys

        _prefs[slot] = bp;
        return bp;
    }

    public void Forget(int slot) => _prefs.Remove(slot);

    /// <summary>Returns a CSSharp-compatible weapon name for the buy command,
    /// based on prefs. team is used to disambiguate (AK vs M4).</summary>
    public string ResolvePrimaryWeapon(BotBuyPrefs bp, CounterStrikeSharp.API.Modules.Utils.CsTeam team)
    {
        bool isCT = team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist;
        return bp.Primary switch
        {
            PreferredPrimary.AWP          => "weapon_awp",
            PreferredPrimary.AutoSniper   => isCT ? "weapon_scar20" : "weapon_g3sg1",
            PreferredPrimary.SMG          => isCT ? "weapon_mp9" : "weapon_mac10",
            PreferredPrimary.Shotgun      => isCT ? "weapon_mag7" : "weapon_sawedoff",
            PreferredPrimary.Scout        => "weapon_ssg08",
            PreferredPrimary.ChickenLover => isCT ? "weapon_famas" : "weapon_galilar",
            _                             => isCT ? "weapon_m4a1" : "weapon_ak47",
        };
    }

    public string ResolveSecondaryWeapon(BotBuyPrefs bp, CounterStrikeSharp.API.Modules.Utils.CsTeam team)
    {
        bool isCT = team == CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist;
        return bp.Secondary switch
        {
            PreferredSecondary.Deagle        => "weapon_deagle",
            PreferredSecondary.Tec9          => isCT ? "weapon_fiveseven" : "weapon_tec9",
            PreferredSecondary.FiveSeven     => "weapon_fiveseven",
            PreferredSecondary.DualBerettas  => "weapon_elite",
            PreferredSecondary.CZ75          => "weapon_cz75a",
            _                                => isCT ? "weapon_usp_silencer" : "weapon_glock",
        };
    }
}
