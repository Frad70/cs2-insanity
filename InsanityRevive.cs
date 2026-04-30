using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace InsanityRevive;

[CounterStrikeSharp.API.Core.Attributes.MinimumApiVersion(304)]
public partial class InsanityRevive : BasePlugin
{
    public override string ModuleName => "INSANITY REVIVE";
    public override string ModuleVersion => "0.21.0";
    public override string ModuleAuthor => "frad70 + Claude";
    public override string ModuleDescription => "Predictive aim + per-bot personas, social bots, zone-aware callouts (smoke/molly/flash/plant/time/low-HP), echo/rebuke chains, IGL strats, body-block FF consequences.";

    // -------- per-bot state --------
    private readonly Dictionary<int, BotPersona> _botPersonas = new();
    private readonly Dictionary<int, float> _lastChatTime = new();
    private readonly Dictionary<int, float> _lastHearTime = new();
    private readonly Dictionary<int, float> _typingUntil = new();
    private readonly Dictionary<int, float> _combatUntil = new();
    private readonly Dictionary<int, int>   _strafeDir = new();
    private readonly Dictionary<int, float> _lookUntil = new();
    private readonly Dictionary<int, (float yaw, float pitch)> _forceLook = new();

    // Active "fire button held" window per bot
    private readonly Dictionary<int, float> _attackUntil = new();

    // ---- v0.10 movement realism: per-bot pulse windows for buttons ----
    // Same pattern as _attackUntil — each tick we OR the button bit while now < until.
    private readonly Dictionary<int, float> _walkUntil = new();        // IN_SPEED (shift-walk)
    private readonly Dictionary<int, float> _duckUntil = new();        // IN_DUCK
    private readonly Dictionary<int, float> _jumpUntil = new();        // IN_JUMP (single-tick crouch-jump)
    private readonly Dictionary<int, float> _entryRushUntil = new();   // entry-fragger fast-react window
    private readonly Dictionary<int, bool>  _crouchJumpUsedThisRound = new();
    private readonly Dictionary<int, float> _shoulderPeekUntil = new();// brief "peek then back" window
    private readonly Dictionary<int, float> _lastHeadingYaw = new();   // last yaw while moving
    private readonly Dictionary<int, float> _preAimRefreshAt = new();  // throttle pre-aim updates

    // CS2 button bits (PlayerControllerInput / movement service)
    private const ulong IN_ATTACK = 1UL;
    private const ulong IN_JUMP   = 1UL << 1;     // 2
    private const ulong IN_DUCK   = 1UL << 2;     // 4
    private const ulong IN_SPEED  = 1UL << 12;    // 4096 — shift-walk modifier

    // streaks reset per round
    private readonly Dictionary<int, int> _killsThisRound = new();
    private readonly Dictionary<int, int> _deathsThisMatch = new();

    // FF ledger reset per round
    private readonly Dictionary<(int v, int a), int> _ffDamageRound = new();
    private readonly Dictionary<int, float> _lastFFChatTime = new();

    // Social: track recent toxic chatter for argue/rebuke chain
    private readonly Dictionary<int, float> _lastToxicChatTime = new();
    private readonly Dictionary<int, string> _lastToxicChatLine = new();

    // AFK simulation
    private readonly Dictionary<int, float> _afkUntil = new();

    // Body-block detection
    private readonly Dictionary<int, float> _lastMovingTime = new();
    private readonly Dictionary<int, float> _lastBumpTime = new();
    private readonly Dictionary<int, int> _bumpsThisRound = new();

    // Vote / rage cooldowns to keep them rare
    private float _lastVoteCallTime = -999f;
    private float _lastRageQuitTime = -999f;
    private bool _inFreezePeriod = false;     // toggled by RoundStart/FreezeEnd

    private readonly Random _rng = new();
    private readonly AimController _aim = new();

    // v0.11.0 — independent service modules
    private readonly DecisionEngine  _decisions = new();
    private readonly ClutchBehavior  _clutch    = new();
    private readonly EconomyModel    _econ      = new();
    private readonly BuyPreferences  _buyPrefs  = new();
    /// v0.14: bots that have already announced their clutch this round.
    private readonly HashSet<int>    _preClutchAnnouncedThisRound = new();
    /// v0.21: bot death-times for spec-mock timing
    private readonly Dictionary<int, float> _botDeathTime = new();
    /// v0.21: dead bots that already spec-mocked this round
    private readonly HashSet<int>    _specMockedThisRound = new();

    // -------- global tunables --------
    public string CurrentPreset { get; private set; } = "Insane";
    private bool _toxicChat = true;
    private bool _hearingEnabled = true;
    private bool _strafingEnabled = false;     // disabled in 0.6.6 — caused drift bug
    private bool _movementRealismEnabled = true;  // v0.10 — buttons-only walk/duck/jump pulses + pre-aim
    private bool _typingTimeEnabled = true;
    private bool _wrongChatEnabled = true;
    private bool _antics = true;
    private bool _useNativeChat = true;       // ExecuteClientCommandFromServer "say"/"say_team"

    // CHAT BASE PROBABILITIES — these get multiplied by per-bot Talkativeness factor.
    // Tuned so that with a normal 9-bot team, average team produces a small handful of
    // chat lines per round, not a wall of noise.
    private float _killChatPct      = 0.20f;
    private float _deathChatPct     = 0.10f;
    private float _hearingChatPct   = 0.06f;
    private float _roundStartPct    = 0.16f;
    private float _winChatPct       = 0.30f;
    private float _loseChatPct      = 0.14f;
    private float _plantChatPct     = 0.40f;
    private float _wrongChatPct     = 0.05f;
    private float _rebukeChainPct   = 0.45f;     // chance any teammate will rebuke a toxic chatter
    private float _mockDyingPct     = 0.15f;
    private float _streakHypePct    = 0.18f;
    private float _argueChancePct   = 0.10f;
    private float _chatCooldownSec  = 7.0f;

    /// v0.12: per-bot buy override — issue persona-driven `buy weapon_X`
    /// commands at start of buy time. Default OFF since it races engine-AI;
    /// user can enable with `css_buy_override 1`.
    private bool _buyOverrideEnabled = false;

    // 0.4 dials
    private float _afkOnSpawnPct        = 0.04f;   // chance a bot starts the round AFK
    private float _afkMidRoundPct       = 0.015f;  // chance per minute that an alive bot goes AFK mid-round
    private float _ggPct                = 0.55f;   // chance friendly bots post GG at round end
    private float _bodyBumpPct          = 0.40f;   // chance a STUCK bot actually bumps the blocker
    private float _bodyBumpDamage       = 12f;     // hp per bump (low — they don't usually kill)
    private float _bodyBumpRequireSec   = 3.2f;    // how long stuck before bumping
    private float _voteOnFFRagePct      = 0.20f;   // when FF rage chats, chance of also issuing callvote
    private float _voteRandomPerMatchPct= 0.005f;  // random per-round chance some bot calls a vote on a teammate they "don't like"
    private float _ragequitPctPerRound  = 0.008f;  // ragequit chance any bot per round (capped by global cooldown)
    private float _pauseIdlePctPerSec   = 0.012f;  // chance per second of freeze-period banter line
    private float _pauseBaitPct         = 0.20f;   // of pause idle picks, chance it's a bait at human/teammate
    // v0.18: removed _friendlyChatBoost (was unused after Mood-aware pools landed)
    // v0.18: clutch refresh throttle — 5Hz instead of 33Hz (clutch state changes rarely)
    private float _lastClutchRefreshAt = 0f;
    /// v0.18: structured behavior log — features fire here so user can review post-hoc.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _behaviorLog = new();
    private float _lastBehaviorFlushAt = 0f;
    private const string BehaviorLogPath = "/home/frad70/cs2-server/insanity-revive.log";

    // Long-pause AFK boost — when real wall-clock between rounds is unusually long,
    // multiply next round's AFK roll. Capped at 8x so it doesn't always afk-storm.
    private float _lastRoundEndWall = 0f;
    private float _afkBoostNextRound = 1.0f;
    private const float NormalRoundGapSec = 15.0f;   // freezetime + restart-ish

    // First blood per round (for the "first blood" call)
    private bool _firstBloodDoneThisRound = false;

    // Vendetta — per-round, ~few % of bots pick a teammate they "hate" this round
    private readonly Dictionary<int, int> _grudgeTarget = new();   // bot.Slot → target.Slot
    private float _grudgePerRoundPct = 0.05f;

    // 0.7: per-bot per-target damage tally (for "low {zone} {n}x" callouts)
    private readonly Dictionary<(int bot, int target), int> _botDmgToTarget = new();
    private readonly Dictionary<int, float> _lastEngagementTime = new();
    private int _matchRoundCount = 0;       // increments on every round_start, reset on map change

    // Connection-loss / tech-pause sim
    private float _lastDisconnectTime = -999f;
    private bool  _techPauseActive = false;
    private int   _quotaBeforeDisc = -1;             // remember bot_quota so we can restore

    // Recent "sorry" (chat keyword from human / bot apology) softens FF retaliation
    private readonly Dictionary<(int v, int a), float> _recentSorry = new();
    private static readonly string[] SorryKeywords = { "sorry", "sry", "mb", " my bad", "пардон", "сорян", "прошу прощения" };

    // Per-team mini-vote on round start: maps team → (votes-A, votes-B, expiresAt)
    private readonly Dictionary<int, (int a, int b, float expires)> _siteVote = new();

    private float _hearingRadiusUnits = 1100f;

    // ---- v0.9: zone-callout dedup ----
    // key = zone family ("a"/"b"/"mid"/etc) → cooldown-expiry timestamp
    private readonly Dictionary<string, float> _zoneCalloutCooldown = new();
    // separate for "low {who}" callouts so they don't collide with positional zone keys
    private readonly Dictionary<int, float> _lowEnemyCallCooldown = new();   // victim.Slot → expiry
    private float _zoneCalloutMinCooldown = 7.0f;
    private float _zoneCalloutMaxCooldown = 12.0f;
    private bool  _calloutsEnabled = true;
    private bool  _timeCalloutDoneThisRound = false;

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("INSANITY REVIVE v{v} loading…", ModuleVersion);
        LogBehavior("BOOT", $"v{ModuleVersion} loaded; map={Server.MapName ?? "?"}");

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventPlayerFootstep>(OnPlayerFootstep);
        RegisterEventHandler<EventPlayerJump>(OnPlayerJump);
        RegisterEventHandler<EventWeaponZoom>(OnWeaponZoom);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrown);
        RegisterEventHandler<EventDecoyStarted>(OnDecoyStarted);
        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);
        // v0.9 callout triggers
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventInfernoStartburn>(OnInfernoStart);
        RegisterEventHandler<EventFlashbangDetonate>(OnFlashDetonate);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventRoundTimeWarning>(OnRoundTimeWarning);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);

        // 33 Hz tick — drives aim override + strafe + typing-freeze + look-force
        AddTimer(0.030f, OnTick, TimerFlags.REPEAT);

        // v0.10: AimController fires this when a bot first acquires a NEW target —
        // host plugin uses it to trigger crouch-pulse / entry-rush / crouch-jump.
        _aim.OnFreshTarget = OnAimFreshTarget;

        if (!hotReload)
            ApplyPreset(CurrentPreset, announce: false);
        else
            ReapplyConvarsOnly(CurrentPreset);

        // 0.7: per-bot skill applies to AimController per-tick by influencing lead/jitter via override.
        // Implemented by reading persona.Skill in a forced-target scenario isn't trivial without
        // touching AimController; we use Skill as a multiplier on chat probability and on
        // probabilistic checks elsewhere (e.g., low-call threshold). Hooked organically across the file.
        _matchRoundCount = 0;

        Logger.LogInformation("INSANITY REVIVE loaded. Preset = {p}", CurrentPreset);
    }

    public override void Unload(bool hotReload)
    {
        _botPersonas.Clear(); _lastChatTime.Clear(); _lastHearTime.Clear();
        _typingUntil.Clear(); _combatUntil.Clear(); _strafeDir.Clear();
        _lookUntil.Clear();   _forceLook.Clear();   _ffDamageRound.Clear();
        _lastFFChatTime.Clear(); _lastToxicChatTime.Clear(); _lastToxicChatLine.Clear();
        _killsThisRound.Clear(); _deathsThisMatch.Clear();
        _afkUntil.Clear(); _lastMovingTime.Clear(); _lastBumpTime.Clear(); _bumpsThisRound.Clear();
        _walkUntil.Clear(); _duckUntil.Clear(); _jumpUntil.Clear(); _entryRushUntil.Clear();
        _crouchJumpUsedThisRound.Clear(); _shoulderPeekUntil.Clear();
        _lastHeadingYaw.Clear(); _preAimRefreshAt.Clear();
        _zoneCalloutCooldown.Clear(); _lowEnemyCallCooldown.Clear();
        Logger.LogInformation("INSANITY REVIVE unloaded.");
    }

    // ================================================================================
    //  Commands & menu
    // ================================================================================

    [ConsoleCommand("css_bots", "Open the INSANITY radio menu")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void OnBotsCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null || !player.IsValid) return;
        if (!CounterStrikeSharp.API.Modules.Admin.AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            player.PrintToChat($" {ChatColors.Red}[INSANITY] need admin");
            return;
        }
        OpenRootMenu(player);
    }

    [ConsoleCommand("css_bots_preset", "Set bot difficulty preset")]
    [CommandHelper(minArgs: 1, usage: "<casual|normal|hard|insane|aimbot>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnBotsPresetCommand(CCSPlayerController? _, CommandInfo info) => ApplyPreset(info.GetArg(1), announce: true);

    [ConsoleCommand("css_buy_override", "Toggle per-bot persona-driven buy commands (0/1)")]
    [CommandHelper(minArgs: 1, usage: "<0|1>", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnBuyOverrideCommand(CCSPlayerController? _, CommandInfo info)
    {
        var arg = info.GetArg(1);
        if (arg == "1" || arg.Equals("on", StringComparison.OrdinalIgnoreCase)) _buyOverrideEnabled = true;
        else if (arg == "0" || arg.Equals("off", StringComparison.OrdinalIgnoreCase)) _buyOverrideEnabled = false;
        Server.PrintToChatAll($"[INSANITY] buy_override = {(_buyOverrideEnabled ? "ON" : "OFF")}");
    }

    private void OpenRootMenu(CCSPlayerController player)
    {
        var menu = new ChatMenu("INSANITY");
        menu.AddMenuOption("Difficulty preset", (p, _) => OpenPresetMenu(p));
        menu.AddMenuOption($"Toxic chat: {(_toxicChat ? "ON" : "off")}",       (p, _) => Toggle(p, ref _toxicChat,        "Toxic chat"));
        menu.AddMenuOption($"Native chat: {(_useNativeChat ? "ON" : "off")}",  (p, _) => Toggle(p, ref _useNativeChat,    "Native chat"));
        menu.AddMenuOption($"Typing freeze: {(_typingTimeEnabled ? "ON" : "off")}", (p, _) => Toggle(p, ref _typingTimeEnabled, "Typing freeze"));
        menu.AddMenuOption($"Wrong chat (global): {(_wrongChatEnabled ? "ON" : "off")}", (p, _) => Toggle(p, ref _wrongChatEnabled, "Wrong chat"));
        menu.AddMenuOption($"Hearing: {(_hearingEnabled ? "ON" : "off")}",     (p, _) => Toggle(p, ref _hearingEnabled,    "Hearing"));
        menu.AddMenuOption($"Strafing: {(_strafingEnabled ? "ON" : "off")}",   (p, _) => Toggle(p, ref _strafingEnabled,   "Strafing"));
        menu.AddMenuOption($"Movement realism: {(_movementRealismEnabled ? "ON" : "off")}", (p, _) => Toggle(p, ref _movementRealismEnabled, "Movement realism"));
        menu.AddMenuOption($"Antics: {(_antics ? "ON" : "off")}",              (p, _) => Toggle(p, ref _antics,            "Antics"));
        menu.AddMenuOption($"Predictive aim: {(_aim.Enabled ? "ON" : "off")}", (p, _) => { _aim.Enabled = !_aim.Enabled; AnnounceToggle(p.PlayerName, "Predictive aim", _aim.Enabled); });
        MenuManager.OpenChatMenu(player, menu);
    }
    private void Toggle(CCSPlayerController p, ref bool flag, string label) { flag = !flag; AnnounceToggle(p.PlayerName, label, flag); }
    private void AnnounceToggle(string who, string label, bool on) =>
        Server.PrintToChatAll($" {ChatColors.Lime}[INSANITY]{ChatColors.Default} {who} → {label}: {(on ? "on" : "off")}");

    private void OpenPresetMenu(CCSPlayerController player)
    {
        var menu = new ChatMenu("Difficulty preset");
        foreach (var p in new[] { "Casual", "Normal", "Hard", "Insane", "Aimbot" })
        {
            var pn = p;
            menu.AddMenuOption(pn + (CurrentPreset == pn ? " (current)" : ""), (_, __) => ApplyPreset(pn, announce: true));
        }
        MenuManager.OpenChatMenu(player, menu);
    }

    // ================================================================================
    //  Difficulty presets
    // ================================================================================

    // ------------------------------------------------------------------------
    //  Radio command helpers (CS2 single-shot console radios)
    // ------------------------------------------------------------------------
    private static readonly string[] Radio_EnemySpot = { "enemyspot" };
    private static readonly string[] Radio_NeedBackup = { "needbackup", "takingfire" };
    private static readonly string[] Radio_GetOut = { "getout" };
    private static readonly string[] Radio_HoldPos = { "holdpos", "regroup" };
    private static readonly string[] Radio_TakePoint = { "takepoint", "go", "stormfront" };
    private static readonly string[] Radio_Affirm = { "affirmative", "roger" };
    private static readonly string[] Radio_Negative = { "negative" };
    private static readonly string[] Radio_Compliment = { "compliment", "cheer" };
    private static readonly string[] Radio_FollowMe = { "followme", "sticktog" };
    private static readonly string[] Radio_FallBack = { "fallback" };
    private static readonly string[] Radio_InPosition = { "inposition", "reportingin" };
    private static readonly string[] Radio_EnemyDown = { "enemydown" };

    private void RadioFromBot(CCSPlayerController bot, string[] pool, float baseChance = 1.0f)
    {
        if (bot is null || !bot.IsValid || !bot.IsBot) return;
        if (!Roll(baseChance, bot)) return;
        var cmd = pool[_rng.Next(pool.Length)];
        AddTimer(0.05f + (float)_rng.NextDouble() * 0.6f, () =>
        {
            if (!bot.IsValid) return;
            try { bot.ExecuteClientCommandFromServer(cmd); } catch { }
        });
    }

    /// v0.12: at start of buy time, fire `buy weapon_X` commands per-bot
    /// based on their persona's BuyPreferences. Each bot gets its own buy
    /// plan: full / force / eco governed by EconomyModel.PlannedBuy and
    /// the bot's individual BuyForceTendency. Spread firing across 0-2.5s
    /// so it doesn't all happen on the same tick (looks more human).
    /// v0.20: at start of round, broke bots ask the richest teammate to drop them.
    /// Cap: 1 drop request per round (avoid spam). Mood-skewed wording.
    private void IssueDropRequests()
    {
        // Find broke bots and the richest teammate per team
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var roster = Utilities.GetPlayers().Where(p => p.IsValid && p.Team == team).ToList();
            if (roster.Count < 2) continue;
            var rich = roster
                .Where(p => (p.InGameMoneyServices?.Account ?? 0) >= 5000)
                .OrderByDescending(p => p.InGameMoneyServices?.Account ?? 0)
                .FirstOrDefault();
            if (rich == null) continue;
            var brokeBots = roster
                .Where(p => p.IsBot && p != rich && (p.InGameMoneyServices?.Account ?? 0) < 1500)
                .ToList();
            if (brokeBots.Count == 0) continue;

            // Pick one broke bot at random, mood-roll, fire
            var bot = brokeBots[_rng.Next(brokeBots.Count)];
            if (!_botPersonas.TryGetValue(bot.Slot, out var per)) continue;
            float chance = per.Mood switch
            {
                Friendliness.Friendly => 0.65f,
                Friendliness.Hostile  => 0.40f,
                _                     => 0.45f,
            };
            if (!Roll(chance)) continue;
            var refName = ChatStyles.RefName(rich.PlayerName, (int)rich.Team, _rng);
            LogBehavior("DROP_REQ", $"{bot.PlayerName} → {rich.PlayerName} mood={per.Mood}");
            AddTimer(0.4f + (float)_rng.NextDouble() * 1.2f, () =>
            {
                if (!bot.IsValid) return;
                ScheduleBotChat(bot, "", (_, __) => ChatStyles.PickDropRequest(per, refName, _rng),
                    teamOnly: true, isToxic: per.Mood == Friendliness.Hostile);
            });
        }
    }

    private void IssuePersonaBuyCommands()
    {
        var econT  = _econ.GetTeam(CsTeam.Terrorist);
        var econCT = _econ.GetTeam(CsTeam.CounterTerrorist);

        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (bot.Team <= CsTeam.Spectator) continue;
            if (!_botPersonas.TryGetValue(bot.Slot, out var per)) continue;
            var prefs = _buyPrefs.GetOrRoll(bot.Slot, per, _rng);
            var plan = (bot.Team == CsTeam.Terrorist ? econT : econCT).PlannedBuy;

            // Force-buy override: per-bot tendency can upgrade SemiEco → ForceBuy.
            if (plan == EconomyModel.BuyPlan.SemiEco
                && _rng.NextDouble() < prefs.BuyForceTendency)
                plan = EconomyModel.BuyPlan.ForceBuy;

            // Build the buy list
            var cmds = new List<string>();
            if (plan == EconomyModel.BuyPlan.FullBuy || plan == EconomyModel.BuyPlan.ForceBuy)
            {
                cmds.Add("buy " + _buyPrefs.ResolvePrimaryWeapon(prefs, bot.Team));
                if (prefs.BuysArmor)   cmds.Add(prefs.BuysHelmet ? "buy vesthelm" : "buy vest");
                if (bot.Team == CsTeam.CounterTerrorist && prefs.BuysDefuser) cmds.Add("buy defuser");
                if (prefs.BuysFlashes) cmds.Add("buy flashbang");
                if (prefs.BuysSmokes)  cmds.Add("buy smokegrenade");
                if (prefs.BuysMolotovs) cmds.Add(bot.Team == CsTeam.CounterTerrorist ? "buy incgrenade" : "buy molotov");
                if (prefs.BuysHE)      cmds.Add("buy hegrenade");
            }
            else if (plan == EconomyModel.BuyPlan.SemiEco)
            {
                cmds.Add("buy " + _buyPrefs.ResolveSecondaryWeapon(prefs, bot.Team));
                if (prefs.BuysArmor && _rng.NextDouble() < 0.6) cmds.Add("buy vest");
            }
            else // Eco
            {
                if (_rng.NextDouble() < 0.30) cmds.Add("buy " + _buyPrefs.ResolveSecondaryWeapon(prefs, bot.Team));
            }

            // Chaotic: 4% of bots — replace plan with a random weird buy
            if (prefs.ChaoticBuys && _rng.NextDouble() < 0.30)
            {
                cmds.Clear();
                var weird = new[] { "weapon_p90", "weapon_xm1014", "weapon_negev", "weapon_m249",
                                    "weapon_mag7", "weapon_ump45", "weapon_deagle" };
                cmds.Add("buy " + weird[_rng.Next(weird.Length)]);
                if (_rng.NextDouble() < 0.7) cmds.Add("buy vesthelm");
            }

            // Fire them spread across 0..2.5s
            for (int i = 0; i < cmds.Count; i++)
            {
                var cmd = cmds[i];
                AddTimer(0.10f + i * 0.15f + (float)_rng.NextDouble() * 0.25f, () =>
                {
                    if (!bot.IsValid) return;
                    try { bot.ExecuteClientCommandFromServer(cmd); } catch { }
                });
            }
        }
    }

    private static readonly Dictionary<string, (string profile, int diff, string aim)> _presets = new()
    {
        ["Casual"] = ("Low",    1, "bot_aim_mixed"),
        ["Normal"] = ("Medium", 2, "bot_aim_mixed"),
        ["Hard"]   = ("High",   3, "bot_aim_body"),
        ["Insane"] = ("Max",    3, "bot_aim_head"),
        ["Aimbot"] = ("Max",    3, "bot_aim_head"),
    };

    private void ApplyPreset(string name, bool announce)
    {
        var key = char.ToUpper(name[0]) + name[1..].ToLower();
        if (!_presets.TryGetValue(key, out var def)) { Server.PrintToChatAll($" {ChatColors.Red}[INSANITY] unknown preset: {name}"); return; }
        CurrentPreset = key;
        try
        {
            var overrides = "/home/frad70/cs2-server/game/csgo/overrides";
            var src = $"{overrides}/{def.profile}/botprofile.vpk";
            var active = $"{overrides}/botprofile.vpk";
            if (File.Exists(src)) { if (File.Exists(active)) File.Delete(active); File.Copy(src, active); }
        } catch (Exception ex) { Logger.LogError(ex, "swap profile failed"); }

        // Tighten aim params per preset (smooth-lerp model — no per-tick jitter)
        switch (key)
        {
            case "Casual":  _aim.LeadEnabled = false; _aim.PrefireOffsetEnabled = false; _aim.SnapPerTick = 0.06f; _aim.MaxBiasDeg = 2.5f; _aim.GoalRefreshSec = 0.40f; break;
            case "Normal":  _aim.LeadEnabled = true;  _aim.PrefireOffsetEnabled = false; _aim.SnapPerTick = 0.10f; _aim.MaxBiasDeg = 1.4f; _aim.GoalRefreshSec = 0.30f; break;
            case "Hard":    _aim.LeadEnabled = true;  _aim.PrefireOffsetEnabled = true;  _aim.SnapPerTick = 0.18f; _aim.MaxBiasDeg = 0.8f; _aim.GoalRefreshSec = 0.25f; break;
            case "Insane":  _aim.LeadEnabled = true;  _aim.PrefireOffsetEnabled = true;  _aim.SnapPerTick = 0.30f; _aim.MaxBiasDeg = 0.4f; _aim.GoalRefreshSec = 0.20f; break;
            case "Aimbot":  _aim.LeadEnabled = true;  _aim.PrefireOffsetEnabled = false; _aim.SnapPerTick = 0.55f; _aim.MaxBiasDeg = 0.0f; _aim.GoalRefreshSec = 0.12f; break;
        }

        ReapplyConvarsOnly(key);
        AddTimer(0.5f, () =>
        {
            var map = Server.MapName;
            if (!string.IsNullOrEmpty(map))
            {
                Server.PrintToChatAll($" {ChatColors.Lime}[INSANITY]{ChatColors.Default} preset: {ChatColors.Yellow}{CurrentPreset}{ChatColors.Default} — reloading {map}…");
                Server.ExecuteCommand($"changelevel {map}");
            }
        });
        if (announce) Server.PrintToChatAll($" {ChatColors.Lime}[INSANITY]{ChatColors.Default} preset → {ChatColors.Yellow}{CurrentPreset}");
    }

    private void ReapplyConvarsOnly(string presetName)
    {
        var key = char.ToUpper(presetName[0]) + presetName[1..].ToLower();
        if (!_presets.TryGetValue(key, out var def)) return;
        Server.ExecuteCommand($"bot_difficulty {def.diff}");
        Server.ExecuteCommand(def.aim);
        Server.ExecuteCommand("bot_join_after_player 0");
        Server.ExecuteCommand("bot_defer_to_human_goals 0");
        Server.ExecuteCommand("bot_defer_to_human_items 0");
        Server.ExecuteCommand("bot_chatter normal");
        Server.ExecuteCommand("bot_walk 0");
        Server.ExecuteCommand("bot_eco_limit 0");
        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_limitteams 0");
    }

    // ================================================================================
    //  Game event hooks
    // ================================================================================

    private HookResult OnPlayerSpawn(EventPlayerSpawn e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p?.IsValid != true) return HookResult.Continue;
        if (p.IsBot && !_botPersonas.ContainsKey(p.Slot))
        {
            var persona = ChatStyles.RandomPersona(_rng, p.PlayerName);
            _botPersonas[p.Slot] = persona;
            PushAimProfile(p.Slot, persona);
        }
        return HookResult.Continue;
    }

    /// Push the persona's aim parameters into the AimController so per-bot
    /// behavior is applied. Called whenever a persona is created or refreshed.
    /// v0.18: append a structured event to the behavior log queue.
    /// Flushed to disk every 2s by OnTick. User reviews post-hoc.
    private void LogBehavior(string kind, string detail)
    {
        try
        {
            _behaviorLog.Enqueue($"{DateTime.Now:HH:mm:ss} [{kind}] {detail}");
        } catch { }
    }

    private void FlushBehaviorLog()
    {
        try
        {
            if (_behaviorLog.IsEmpty) return;
            using var w = new System.IO.StreamWriter(BehaviorLogPath, append: true);
            while (_behaviorLog.TryDequeue(out var line))
                w.WriteLine(line);
        } catch { /* best-effort */ }
    }

    private void PushAimProfile(int slot, BotPersona persona)
    {
        // v0.16: apply DecisionEngine state modifiers — chad streak tightens
        // aim; hard tilt loosens it. Real-player effect: "in the zone" vs
        // "on tilt and missing easy ones".
        float snap   = persona.AimSnapPerTick;
        float bias   = persona.AimMaxBiasDeg;
        float refresh = persona.AimGoalRefreshSec;
        float react  = persona.AimReactionTimeSec;
        float noise  = persona.AimTrackingNoiseDeg;
        float flick  = persona.AimFlickStrength;
        if (_decisions.IsOnChadStreak(slot))
        {
            snap    *= 1.10f;
            bias    *= 0.80f;
            refresh *= 0.90f;
            react   *= 0.92f;
            noise   *= 0.70f;
            flick   *= 1.08f;
        }
        else if (_decisions.IsHardTilted(slot))
        {
            snap    *= 0.85f;
            bias    *= 1.25f;
            refresh *= 1.15f;
            react   *= 1.15f;
            noise   *= 1.30f;
            flick   *= 0.92f;
        }
        _aim.SetProfile(slot, new AimController.AimProfile
        {
            SnapPerTick       = snap,
            MaxBiasDeg        = bias,
            GoalRefreshSec    = refresh,
            ReactionTimeSec   = react,
            OvershootChance   = persona.AimOvershootChance,
            OvershootDeg      = persona.AimOvershootDeg,
            TrackingNoiseDeg  = noise,
            MicroAdjustChance = persona.AimMicroAdjustChance,
            SpraysWell        = persona.AimSpraysWell,
            FlickStrength     = flick,
        });
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect e, GameEventInfo info)
    {
        var p = e.Userid;
        if (p == null) return HookResult.Continue;
        _botPersonas.Remove(p.Slot); _lastChatTime.Remove(p.Slot); _lastHearTime.Remove(p.Slot);
        _typingUntil.Remove(p.Slot); _combatUntil.Remove(p.Slot); _strafeDir.Remove(p.Slot);
        _lookUntil.Remove(p.Slot);   _forceLook.Remove(p.Slot);   _killsThisRound.Remove(p.Slot);
        _deathsThisMatch.Remove(p.Slot); _lastToxicChatTime.Remove(p.Slot); _lastToxicChatLine.Remove(p.Slot);
        var keys = _ffDamageRound.Keys.Where(k => k.v == p.Slot || k.a == p.Slot).ToList();
        foreach (var k in keys) _ffDamageRound.Remove(k);
        _walkUntil.Remove(p.Slot); _duckUntil.Remove(p.Slot); _jumpUntil.Remove(p.Slot);
        _entryRushUntil.Remove(p.Slot); _crouchJumpUsedThisRound.Remove(p.Slot);
        _shoulderPeekUntil.Remove(p.Slot); _lastHeadingYaw.Remove(p.Slot); _preAimRefreshAt.Remove(p.Slot);
        _aim.Forget(p.Slot);
        // v0.11: clean up decision/clutch/buy state
        _decisions.Forget(p.Slot);
        _clutch.Forget(p.Slot);
        _buyPrefs.Forget(p.Slot);
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath e, GameEventInfo info)
    {
        var victim = e.Userid;
        var killer = e.Attacker;

        if (victim is { IsValid: true })
        {
            _deathsThisMatch.TryGetValue(victim.Slot, out var d);
            _deathsThisMatch[victim.Slot] = d + 1;
            // reset killer's per-round count contribution? no — kill streak counted on attacker side
        }

        // v0.11: feed DecisionEngine + ClutchBehavior with the death event
        bool ffKill = killer is { IsValid: true } && victim is { IsValid: true } && killer != victim
                      && killer.Team == victim.Team && killer.Team > CsTeam.Spectator;
        if (victim is { IsValid: true, IsBot: true })
        {
            // Was victim the last one alive on their team? Check survivors AFTER this death.
            int teamAlive = Utilities.GetPlayers().Count(p =>
                p.IsValid && p != victim && p.Team == victim.Team
                && p.PlayerPawn?.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE);
            bool dyingAsLastMan = teamAlive == 0;
            _decisions.OnBotDeath(victim.Slot, diedToTeammate: ffKill, dyingAsLastMan);
            _clutch.Resolve(victim.Slot, won: false);
            // v0.21: record death-time for spec-mock pacing
            _botDeathTime[victim.Slot] = Server.CurrentTime;
        }
        if (killer is { IsValid: true, IsBot: true } && killer != victim && !ffKill)
        {
            _decisions.OnBotKill(killer.Slot, e.Headshot, wasTeammate: false);
        }
        else if (killer is { IsValid: true, IsBot: true } && ffKill)
        {
            _decisions.OnBotKill(killer.Slot, e.Headshot, wasTeammate: true);
        }

        if (killer is { IsValid: true } && killer != victim && killer.IsBot)
        {
            _killsThisRound.TryGetValue(killer.Slot, out var k);
            _killsThisRound[killer.Slot] = k + 1;

            // Radio: enemy down (low-medium chance)
            RadioFromBot(killer, Radio_EnemyDown, baseChance: 0.30f);

            // First blood
            if (!_firstBloodDoneThisRound && _toxicChat && Roll(0.55f, killer))
            {
                _firstBloodDoneThisRound = true;
                ScheduleBotChat(killer, victim?.PlayerName ?? "kid",
                    (_, __) => ChatStyles.PickFirstBlood(_rng),
                    teamOnly: false, isToxic: true);
            }
            else if (_toxicChat && Roll(_killChatPct, killer))
            {
                // Use weapon/headshot specific pools occasionally
                Func<BotPersona, string, string> pickWith = ChatStyles.PickKillLine;
                var w = (e.Weapon ?? "").ToLowerInvariant();
                if (e.Headshot && Roll(0.45f))
                    pickWith = (_, __) => ChatStyles.PickOneTap(_rng);
                else if (w.Contains("knife"))
                    pickWith = (_, __) => ChatStyles.PickKnifeKill(_rng);
                else if (w.Contains("awp"))
                    pickWith = (_, __) => ChatStyles.PickAwpKill(_rng);

                ScheduleBotChat(killer, victim?.PlayerName ?? "kid", pickWith, teamOnly: false, isToxic: true);
            }

            // Streak hype — a teammate (different bot) gives praise
            if (k + 1 >= 3 && Roll(_streakHypePct))
            {
                var mate = PickRandomTeammateBot(killer);
                if (mate != null)
                    ScheduleBotChat(mate, killer.PlayerName,
                        (_, who) => ChatStyles.PickStreakHype(who, _rng),
                        teamOnly: true, isToxic: false);
            }
        }

        // Bot dies — different teammate may chat death rage
        if (victim is { IsValid: true, IsBot: true } && _toxicChat && Roll(_deathChatPct, victim))
            ScheduleBotChat(victim, killer?.PlayerName ?? "smbdy",
                ChatStyles.PickDeathLine, teamOnly: false, isToxic: false);

        // v0.15: spectator-ping — bot dying to enemy fires zone-aware info call.
        // Skipped for FF deaths (handled by FF rage path), and skipped if victim
        // already typed too much. Mood-skewed via PickDeathPing.
        if (!ffKill && victim is { IsValid: true, IsBot: true } && killer != null
            && _toxicChat
            && _botPersonas.TryGetValue(victim.Slot, out var vPer))
        {
            float chance = vPer.Mood switch
            {
                Friendliness.Hostile  => 0.35f,
                Friendliness.Friendly => 0.25f,
                _                     => 0.18f,
            };
            if (Roll(chance, victim))
            {
                var zone = ChatStyles.PickZoneFor(Server.MapName ?? "", _rng);
                LogBehavior("DEATH_PING", $"{victim.PlayerName} mood={vPer.Mood} zone={zone}");
                AddTimer(0.7f + (float)_rng.NextDouble() * 1.3f, () =>
                {
                    if (!victim.IsValid) return;
                    ScheduleBotChat(victim, "", (_, __) => ChatStyles.PickDeathPing(vPer, zone, _rng),
                        teamOnly: true, isToxic: vPer.Mood == Friendliness.Hostile);
                });
            }
        }

        // ── TEAM RAGE on FF-kill ──
        // If a player (especially a human) just killed a teammate, surviving teammate
        // bots get angry: high-chance rage chat, possible vote-kick, persistent grudge.
        if (killer is { IsValid: true } && victim is { IsValid: true } && killer != victim
            && killer.Team == victim.Team && killer.Team > CsTeam.Spectator)
        {
            var survivors = Utilities.GetPlayers()
                .Where(p => p.IsValid && p.IsBot && p != killer && p.Team == killer.Team)
                .ToList();
            // First reactor: high probability rage line, fast
            if (survivors.Count > 0)
            {
                var first = survivors[_rng.Next(survivors.Count)];
                AddTimer(0.6f + (float)_rng.NextDouble() * 1.2f, () =>
                {
                    if (!first.IsValid) return;
                    ScheduleBotChat(first, killer.PlayerName,
                        (_, who) => ChatStyles.PickFFRageLine(_rng) + " " + ChatStyles.RefName(killer.PlayerName, (int)killer.Team, _rng),
                        teamOnly: false, isToxic: true);
                });
                // 50% chance secondary teammate piles on
                if (Roll(0.5f) && survivors.Count > 1)
                {
                    var second = survivors[_rng.Next(survivors.Count)];
                    AddTimer(2.0f + (float)_rng.NextDouble() * 2.0f, () =>
                    {
                        if (!second.IsValid) return;
                        ScheduleBotChat(second, killer.PlayerName,
                            (_, __) => ChatStyles.PickRebukeLine(killer.PlayerName, _rng),
                            teamOnly: false, isToxic: true);
                    });
                }
                // v0.19: vote-kick chance — auto-bumped to 0.85 if killer is
                // chronic team-killer (2+ FF kills this match). Real MM: one TK
                // is forgiven; two becomes a vote-kick almost-certainly.
                float voteChance = _decisions.IsChronicTeamKiller(killer.Slot) ? 0.85f : 0.40f;
                if (Server.CurrentTime - _lastVoteCallTime > 60f && Roll(voteChance))
                {
                    var initiator = survivors[_rng.Next(survivors.Count)];
                    LogBehavior("VOTEKICK_TK", $"target={killer.PlayerName} chronic={(_decisions.IsChronicTeamKiller(killer.Slot)?1:0)}");
                    AddTimer(3.5f + (float)_rng.NextDouble() * 2f, () =>
                        CallVoteKick(initiator: initiator, target: killer, reason: "tk"));
                }
                // Grudge — pick one of the survivors to hold a grudge against killer
                if (Roll(0.25f))
                {
                    var grudger = survivors[_rng.Next(survivors.Count)];
                    _grudgeTarget[grudger.Slot] = killer.Slot;
                }
            }
        }

        // Mock dying teammate — survivor on same team mocks if victim has died ≥3 times
        if (victim is { IsValid: true, IsBot: true } && _toxicChat)
        {
            if (_deathsThisMatch.TryGetValue(victim.Slot, out var dx) && dx >= 3 && Roll(_mockDyingPct))
            {
                var mocker = PickRandomTeammateBot(victim);
                if (mocker != null && mocker != victim)
                    ScheduleBotChat(mocker, victim.PlayerName,
                        (_, who) => ChatStyles.PickMockDyingLine(who, dx, _rng),
                        teamOnly: true, isToxic: true);
            }

            // 0.7: spec-mock — already-dead teammate (a previously-died bot, now in spec)
            // mocks the just-killed bot in GLOBAL chat.
            var deadInSpec = Utilities.GetPlayers()
                .Where(p => p.IsValid && p.IsBot && p != victim
                            && (p.PlayerPawn?.Value?.LifeState ?? 0) != (byte)LifeState_t.LIFE_ALIVE)
                .ToList();
            if (deadInSpec.Count > 0 && Roll(0.18f))
            {
                var spec = deadInSpec[_rng.Next(deadInSpec.Count)];
                ScheduleBotChat(spec, victim.PlayerName,
                    (_, who) => ChatStyles.PickSpecMock(who, dx, _rng),
                    teamOnly: false, isToxic: true);
            }
        }

        // 0.7: Clear damage tally toward this victim — they're dead, no more "low" calls
        var clearKeys = _botDmgToTarget.Keys.Where(k => k.target == victim?.Slot).ToList();
        foreach (var k in clearKeys) _botDmgToTarget.Remove(k);

        // 0.7: High-Tab bot rage chat — every few rounds, a high-tab bot rants at own team
        if (_toxicChat && Roll(0.04f))
        {
            var ranters = Utilities.GetPlayers()
                .Where(p => p.IsValid && p.IsBot && _botPersonas.TryGetValue(p.Slot, out var per) && per.Tab == Talkativeness.High)
                .ToList();
            if (ranters.Count > 0)
            {
                var ranter = ranters[_rng.Next(ranters.Count)];
                AddTimer(1.0f + (float)_rng.NextDouble() * 2f, () =>
                {
                    if (!ranter.IsValid) return;
                    ScheduleBotChat(ranter, "",
                        (_, __) => ChatStyles.PickHighTabFlame(_rng),
                        teamOnly: true, isToxic: true);
                });
            }
        }

        // Push coordination: surviving teammates near corpse may react
        if (_antics && victim is { IsValid: true, IsBot: true })
            TriggerCoordReaction(victim);

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt e, GameEventInfo info)
    {
        var victim = e.Userid;
        var attacker = e.Attacker;
        if (victim is null || attacker is null || victim == attacker) return HookResult.Continue;
        if (!victim.IsValid || !attacker.IsValid) return HookResult.Continue;
        if (victim.Team <= CsTeam.Spectator) return HookResult.Continue;

        // ── 0.7: Track damage from BOTS toward ENEMIES for "low {zone}" callouts ──
        if (attacker.IsBot && attacker.Team != victim.Team)
        {
            var dkey = (bot: attacker.Slot, target: victim.Slot);
            _botDmgToTarget.TryGetValue(dkey, out var sofar);
            sofar += e.DmgHealth;
            _botDmgToTarget[dkey] = sofar;
            _lastEngagementTime[attacker.Slot] = Server.CurrentTime;

            // v0.9: "one shot {who}" / "low {who}" — fires fast (no delay) when victim drops to ≤30 HP.
            // Per-victim cooldown so we don't spam if multiple bots damage same low target.
            var vp0 = victim.PlayerPawn?.Value;
            if (_calloutsEnabled && _toxicChat
                && vp0?.IsValid == true && vp0.Health > 0 && vp0.Health <= 30)
            {
                var nowL = Server.CurrentTime;
                if (!_lowEnemyCallCooldown.TryGetValue(victim.Slot, out var luntil) || nowL >= luntil)
                {
                    _lowEnemyCallCooldown[victim.Slot] = nowL + 6.0f;
                    if (Roll(0.55f, attacker))
                    {
                        var who = victim.PlayerName ?? "him";
                        var refWho = ChatStyles.RefName(who, (int)victim.Team, _rng);
                        var line = ChatStyles.PickCalloutFixed(ChatStyles.Callout_OneShot, "", refWho, _rng);
                        ScheduleCalloutChat(attacker, line, zoneKey: null,
                            teamOnly: true, isToxic: false, responsePct: 0.25f);
                    }
                }
            }

            // Schedule a check 1.6s later — if victim is still alive and high damage,
            // shooter calls out "low <zone> [{n}x]"
            var capturedSofar = sofar;
            AddTimer(1.6f, () =>
            {
                if (!attacker.IsValid || !victim.IsValid) return;
                var vp = victim.PlayerPawn?.Value;
                if (vp?.IsValid != true) return;
                if (vp.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
                if (vp.Health <= 0) return;
                // only call if NO further damage to this target since
                if (_botDmgToTarget.TryGetValue(dkey, out var newSum) && newSum > capturedSofar) return;
                if (capturedSofar < 65) return;
                if (!_toxicChat) return;
                if (!_botPersonas.TryGetValue(attacker.Slot, out var per)) return;
                var zone = ChatStyles.PickZoneFor(Server.MapName ?? "", _rng);
                var key = ChatStyles.ZoneKeyFor(Server.MapName ?? "", $"low {zone}");
                if (!TryClaimZoneCall(key)) return;
                per.LowCallCount += 1;
                var prefix = per.LowCallCount > 1 ? $"{per.LowCallCount}x " : "";
                var line = $"{prefix}low {zone}";
                ScheduleCalloutChat(attacker, line, zoneKey: null /*already claimed*/, teamOnly: true, isToxic: false, responsePct: 0.30f);
            });
        }

        if (attacker.Team != victim.Team) return HookResult.Continue;

        var key = (v: victim.Slot, a: attacker.Slot);
        _ffDamageRound.TryGetValue(key, out var sum);
        sum += e.DmgHealth;
        _ffDamageRound[key] = sum;

        // 0.7: Track damage by BOT to ENEMY for "low {zone}" callouts.
        // (this branch only handles same-team FF; the enemy path is below in OnPlayerHurt-Other handler)

        // v0.11: feed FF taken into DecisionEngine for tilt accumulation
        if (victim.IsBot)
            _decisions.OnBotTookFF(victim.Slot, e.DmgHealth);

        // ── Physical reaction from victim bot (works for ANY teammate attacker, including humans)
        if (victim.IsBot)
            ReactToFFAttack(victim, attacker, e.DmgHealth, sum);

        if (!_toxicChat) return HookResult.Continue;

        var now = Server.CurrentTime;
        if (_lastFFChatTime.TryGetValue(attacker.Slot, out var t) && now - t < 2.5f) return HookResult.Continue;
        _lastFFChatTime[attacker.Slot] = now;

        // Recent sorry softens chat reactions
        bool sorrySoft = _recentSorry.TryGetValue(key, out var sat) && now - sat < 6f;

        if (sum <= 30 && attacker.IsBot && Roll(0.55f, attacker))
        {
            ScheduleBotChat(attacker, victim.PlayerName, (_, __) => ChatStyles.PickFFSorryLine(_rng), teamOnly: true, isToxic: false);
            _recentSorry[key] = now; // bot's own sorry counts too
            return HookResult.Continue;
        }
        if (sum > 30 && sum <= 80 && victim.IsBot && Roll(sorrySoft ? 0.18f : 0.55f, victim))
        {
            ScheduleBotChat(victim, attacker.PlayerName, (_, __) => ChatStyles.PickFFAnnoyedLine(_rng), teamOnly: true, isToxic: true);
            return HookResult.Continue;
        }
        if (sum > 80 && victim.IsBot && Roll(sorrySoft ? 0.30f : 0.7f, victim))
        {
            ScheduleBotChat(victim, attacker.PlayerName, (_, __) => ChatStyles.PickFFRageLine(_rng), teamOnly: false, isToxic: true);
            if (!sorrySoft && Server.CurrentTime - _lastVoteCallTime > 60f && Roll(_voteOnFFRagePct))
                CallVoteKick(initiator: victim, target: attacker, reason: "tk");
        }
        return HookResult.Continue;
    }

    /// Physical reaction when a bot takes friendly fire. Coin-flip across:
    ///  • Just turn (force-look at attacker briefly)
    ///  • Aim & shoot back (forced target on AimController, weapon AI fires)
    ///  • Aim only, don't shoot
    ///  • Ignore (rare)
    /// Recent "sorry" reduces likelihood of shoot-back; heavy cumulative damage increases it.
    private void ReactToFFAttack(CCSPlayerController victim, CCSPlayerController attacker, int dmg, int cumulative)
    {
        var pawn = victim.PlayerPawn?.Value;
        if (pawn?.IsValid != true) return;
        var ap = attacker.PlayerPawn?.Value;
        if (ap?.IsValid != true) return;
        var vpos = pawn.AbsOrigin; var apos = ap.AbsOrigin;
        if (vpos == null || apos == null) return;

        var key = (v: victim.Slot, a: attacker.Slot);
        bool sorrySoft = _recentSorry.TryGetValue(key, out var sat) && Server.CurrentTime - sat < 6f;

        // base weights: 30% turn, 35% aim+shoot, 20% aim-only, 15% ignore
        // pShoot ramps with cumulative damage (more FF → angrier reactions)
        float pTurn   = 0.30f;
        float pShoot  = 0.35f + Math.Min(0.40f, cumulative / 250f);
        float pAim    = 0.20f;
        float pIgnore = 0.15f;
        if (sorrySoft) { pShoot *= 0.30f; pIgnore += 0.25f; pTurn += 0.15f; }

        // Normalize
        float total = pTurn + pShoot + pAim + pIgnore;
        var roll = (float)(_rng.NextDouble() * total);
        float c = 0;

        c += pTurn;   if (roll < c)
        {
            ForceLookAt(victim, apos, durSec: 0.55f + (float)_rng.NextDouble() * 0.5f);
            return;
        }
        c += pShoot;  if (roll < c)
        {
            // Real action: bot turns toward attacker (head or body depending on weapon),
            // waits a human-ish reaction beat, then SHOOTS via the engine — so it goes
            // through proper hit detection, animations, ammo, sound. No Health hack.
            BotShootBackAtAttacker(victim, attacker);
            return;
        }
        c += pAim;    if (roll < c)
        {
            ForceLookAt(victim, apos, durSec: 0.7f + (float)_rng.NextDouble() * 0.5f);
            return;
        }
        // ignore — do nothing
    }

    private void ForceLookAt(CCSPlayerController bot, CounterStrikeSharp.API.Modules.Utils.Vector targetPos, float durSec)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn?.IsValid != true) return;
        var origin = pawn.AbsOrigin; if (origin == null) return;
        float dx = targetPos.X - origin.X;
        float dy = targetPos.Y - origin.Y;
        float dz = (targetPos.Z + 56f) - (origin.Z + 64f);
        float horiz = MathF.Sqrt(dx * dx + dy * dy);
        float yaw = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        float pitch = -MathF.Atan2(dz, horiz) * 180f / MathF.PI;
        _lookUntil[bot.Slot] = Server.CurrentTime + durSec;
        _forceLook[bot.Slot] = (yaw, pitch);
    }

    /// Aim at a specific body part (head or chest) of a target player.
    private void ForceLookAtPart(CCSPlayerController bot, CCSPlayerController target, bool head, float durSec)
    {
        var pawn = bot.PlayerPawn?.Value;
        var tp = target.PlayerPawn?.Value;
        if (pawn?.IsValid != true || tp?.IsValid != true) return;
        var origin = pawn.AbsOrigin;  if (origin == null) return;
        var torigin = tp.AbsOrigin;   if (torigin == null) return;
        float zOffset = head ? 70f : 50f;     // ~head height vs ~chest height (player model)
        float dx = torigin.X - origin.X;
        float dy = torigin.Y - origin.Y;
        float dz = (torigin.Z + zOffset) - (origin.Z + 64f);
        float horiz = MathF.Sqrt(dx * dx + dy * dy);
        float yaw = MathF.Atan2(dy, dx) * 180f / MathF.PI;
        float pitch = -MathF.Atan2(dz, horiz) * 180f / MathF.PI;
        _lookUntil[bot.Slot] = Server.CurrentTime + durSec;
        _forceLook[bot.Slot] = (yaw, pitch);
    }

    private static bool IsOneShotWeapon(string designerName)
    {
        var n = designerName?.ToLowerInvariant() ?? "";
        return n.Contains("awp") || n.Contains("ssg08") || n.Contains("scout");
    }

    /// Make the bot turn toward attacker (head or body depending on weapon) with a
    /// human-ish reaction time, then deliver visible damage. We try two parallel
    /// approaches so SOME shots land:
    ///   (a) ForceLook + +attack via console + IN_ATTACK schema bit  — if engine
    ///       accepts bot input, real shots fire with animation/sound
    ///   (b) Direct damage (Health -= ; CommitSuicide on lethal) — guaranteed visible
    ///       outcome even when (a) is silently no-op'd by bot AI
    private void BotShootBackAtAttacker(CCSPlayerController bot, CCSPlayerController attacker)
    {
        var pawn = bot.PlayerPawn?.Value;
        if (pawn?.IsValid != true) return;
        var weap = pawn.WeaponServices?.ActiveWeapon?.Value;
        bool oneshot = IsOneShotWeapon(weap?.DesignerName ?? "");
        bool aimHead = !oneshot;

        float reactionTime = 0.18f + (float)_rng.NextDouble() * 0.22f;
        AddTimer(reactionTime, () =>
        {
            if (!bot.IsValid || !attacker.IsValid) return;
            var atkPawn = attacker.PlayerPawn?.Value;
            if (atkPawn?.IsValid != true || atkPawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

            ForceLookAtPart(bot, attacker, head: aimHead, durSec: 0.85f + (float)_rng.NextDouble() * 0.5f);

            float trigDelay = 0.08f + (float)_rng.NextDouble() * 0.10f;
            AddTimer(trigDelay, () =>
            {
                if (!bot.IsValid) return;
                float holdSec = oneshot ? 0.05f : 0.13f + (float)_rng.NextDouble() * 0.18f;
                _attackUntil[bot.Slot] = Server.CurrentTime + holdSec;
                try { bot.ExecuteClientCommandFromServer("+attack"); } catch { }
                // (Buttons setter is read-only in CSSharp 1.0.367; rely on +attack console + ms.Buttons schema)

                AddTimer(holdSec, () =>
                {
                    if (!bot.IsValid) return;
                    try { bot.ExecuteClientCommandFromServer("-attack"); } catch { }
                    AddTimer(0.25f, () => FollowUpRevenge(bot, attacker, depth: 0));
                });
            });
        });
    }

    // (removed) DealRevengeDamage — Health -= hack discontinued per user request;
    // damage now must come from the bot's own weapon firing.

    /// After an initial revenge volley, check if attacker survived. If yes:
    ///   • 50% — fire another short burst at same body part
    ///   • 30% — switch to knife and run them down (uses bot AI movement + force look)
    ///   • 20% — let it go
    /// recursive up to depth 3 so we don't loop forever.
    private void FollowUpRevenge(CCSPlayerController bot, CCSPlayerController attacker, int depth)
    {
        if (depth > 3) return;
        if (!bot.IsValid || !attacker.IsValid) return;
        var atkPawn = attacker.PlayerPawn?.Value;
        if (atkPawn?.IsValid != true) return;
        if (atkPawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
        if (atkPawn.Health <= 0) return;     // already dead

        var roll = _rng.NextDouble();
        if (roll < 0.50)
        {
            // second burst — pull trigger again
            var pawn = bot.PlayerPawn?.Value;
            if (pawn?.IsValid != true) return;
            var weap = pawn.WeaponServices?.ActiveWeapon?.Value;
            bool oneshot = IsOneShotWeapon(weap?.DesignerName ?? "");
            ForceLookAtPart(bot, attacker, head: !oneshot, durSec: 0.7f);
            float holdSec = oneshot ? 0.05f : 0.12f + (float)_rng.NextDouble() * 0.16f;
            _attackUntil[bot.Slot] = Server.CurrentTime + holdSec;
            try { bot.ExecuteClientCommandFromServer("+attack"); } catch { }
            AddTimer(holdSec, () =>
            {
                try { if (bot.IsValid) bot.ExecuteClientCommandFromServer("-attack"); } catch { }
                AddTimer(0.30f + (float)_rng.NextDouble() * 0.4f, () =>
                    FollowUpRevenge(bot, attacker, depth + 1));
            });
            return;
        }
        if (roll < 0.80)
        {
            // knife chase — switch to knife, run at attacker, slash 1-3 times
            try { bot.ExecuteClientCommandFromServer("slot3"); } catch { }
            int slashes = 1 + _rng.Next(3);
            for (int i = 0; i < slashes; i++)
            {
                var t = 0.40f + i * 0.45f;
                AddTimer(t, () =>
                {
                    if (!bot.IsValid || !attacker.IsValid) return;
                    var ap = attacker.PlayerPawn?.Value;
                    if (ap?.IsValid != true || ap.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
                    ForceLookAtPart(bot, attacker, head: false, durSec: 0.5f);
                    AddTimer(0.10f, () =>
                    {
                        if (!bot.IsValid) return;
                        _attackUntil[bot.Slot] = Server.CurrentTime + 0.10f;
                        try { bot.ExecuteClientCommandFromServer("+attack"); } catch { }
                        AddTimer(0.10f, () =>
                        {
                            try { if (bot.IsValid) bot.ExecuteClientCommandFromServer("-attack"); } catch { }
                        });
                    });
                });
            }
            // After knife chase, see if they're still alive and consider another burst
            AddTimer(0.45f + slashes * 0.45f + 0.4f, () => FollowUpRevenge(bot, attacker, depth + 1));
            return;
        }
        // 20% — let it go
    }

    private HookResult OnRoundStart(EventRoundStart e, GameEventInfo info)
    {
        _ffDamageRound.Clear();
        _killsThisRound.Clear();
        _bumpsThisRound.Clear();
        _botDmgToTarget.Clear();
        _crouchJumpUsedThisRound.Clear();   // v0.10 — reset 1x/round crouch-jump quota
        _firstBloodDoneThisRound = false;
        _inFreezePeriod = true;
        _grudgeTarget.Clear();
        _matchRoundCount += 1;
        _timeCalloutDoneThisRound = false;
        _zoneCalloutCooldown.Clear();
        _lowEnemyCallCooldown.Clear();
        _preClutchAnnouncedThisRound.Clear();
        _specMockedThisRound.Clear();
        _botDeathTime.Clear();

        // v0.11: tilt decay + econ snapshot for both teams
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            _decisions.OnNewRound(bot.Slot);
            // v0.16: re-push aim profile so chad/tilt modifiers apply each round
            if (_botPersonas.TryGetValue(bot.Slot, out var per))
                PushAimProfile(bot.Slot, per);
        }
        _econ.SnapshotForRound(CsTeam.Terrorist);
        _econ.SnapshotForRound(CsTeam.CounterTerrorist);

        // v0.12: persona-driven buy commands at start of buy time.
        // Default OFF (engine bots have their own logic — toggle via css_buy_override).
        if (_buyOverrideEnabled)
            AddTimer(1.8f + (float)_rng.NextDouble() * 1.4f, IssuePersonaBuyCommands);

        // v0.20: drop-request chat — broke bots ask rich teammates to drop them.
        AddTimer(2.5f + (float)_rng.NextDouble() * 2.0f, IssueDropRequests);

        // 0.7 — first round of match: friendly bots post GL/glhf
        if (_matchRoundCount <= 1 && _toxicChat)
        {
            foreach (var bot in Utilities.GetPlayers())
            {
                if (!bot.IsValid || !bot.IsBot) continue;
                if (!_botPersonas.TryGetValue(bot.Slot, out var per)) continue;
                if (per.Mood != Friendliness.Friendly) continue;
                if (!Roll(0.65f, bot)) continue;
                AddTimer(1.5f + (float)_rng.NextDouble() * 4f, () =>
                {
                    if (!bot.IsValid) return;
                    ScheduleBotChat(bot, "", (_, __) => ChatStyles.PickGLHF(_rng),
                        teamOnly: false, isToxic: false);
                });
            }
        }

        // 0.7 — pick a per-team strategy for the round (T side mostly drives strat)
        if (_toxicChat)
            RollRoundStrategy();

        // Roll vendetta targets — bots randomly hating one of their teammates this round
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (!Roll(_grudgePerRoundPct)) continue;
            var teammates = Utilities.GetPlayers()
                .Where(p => p.IsValid && p != bot && p.Team == bot.Team).ToList();
            if (teammates.Count == 0) continue;
            var target = teammates[_rng.Next(teammates.Count)];
            _grudgeTarget[bot.Slot] = target.Slot;
            // Schedule a hate line a few seconds in, then potentially escalate to bump/TK
            AddTimer(2f + (float)_rng.NextDouble() * 6f, () =>
            {
                if (!bot.IsValid || !target.IsValid) return;
                if (_toxicChat)
                    ScheduleBotChat(bot, target.PlayerName,
                        (_, who) => ChatStyles.PickGrudgeHate(who, _rng),
                        teamOnly: false, isToxic: true);
            });
            AddTimer(8f + (float)_rng.NextDouble() * 25f, () =>
            {
                if (!bot.IsValid || !target.IsValid) return;
                var tp = target.PlayerPawn?.Value;
                if (tp?.IsValid != true || tp.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
                // half the time: deal moderate damage as if "fingers slipped"
                if (Roll(0.4f))
                {
                    int dmg = 25 + _rng.Next(40);
                    var newHp = MathF.Max(1, tp.Health - dmg);
                    tp.Health = (int)newHp;
                    var k = (v: target.Slot, a: bot.Slot);
                    _ffDamageRound.TryGetValue(k, out var s);
                    _ffDamageRound[k] = s + dmg;
                    if (_toxicChat && Roll(0.6f))
                        ScheduleBotChat(bot, target.PlayerName,
                            (_, __) => ChatStyles.PickGrudgeNadeExcuse(_rng),
                            teamOnly: true, isToxic: true);
                }
            });
        }

        // Long-pause AFK boost: if it took unusually long since last round end, bots are sleepy.
        if (_lastRoundEndWall > 0f)
        {
            var gap = Server.CurrentTime - _lastRoundEndWall;
            if (gap > NormalRoundGapSec)
            {
                var extra = (gap - NormalRoundGapSec) / 30f;     // each 30s past normal = +1x
                _afkBoostNextRound = MathF.Min(8.0f, 1.0f + extra * 2.5f);
            }
            else _afkBoostNextRound = 1.0f;
        }

        // AFK roulette: small chance per bot to be afk this round (boosted after long pauses)
        var afkProb = _afkOnSpawnPct * _afkBoostNextRound;
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (Roll(afkProb))
            {
                var dur = 5f + (float)_rng.NextDouble() * 25f;
                _afkUntil[bot.Slot] = Server.CurrentTime + dur;
                if (_toxicChat && Roll(0.35f))
                    ScheduleBotChat(bot, "", (_, __) => ChatStyles.PickAFKHeadsUp(_rng), teamOnly: true, isToxic: false);
                // Schedule team flame about it after 6s if still afk
                AddTimer(6f + (float)_rng.NextDouble() * 6f, () =>
                {
                    if (!bot.IsValid) return;
                    if (_afkUntil.TryGetValue(bot.Slot, out var until) && Server.CurrentTime < until)
                    {
                        var teammate = PickWeightedTalker(bot.Team);
                        if (teammate != null && teammate != bot && Roll(0.55f, teammate))
                            ScheduleBotChat(teammate, bot.PlayerName,
                                (_, who) => ChatStyles.PickAFKFlame(who, _rng),
                                teamOnly: true, isToxic: true);
                    }
                });
            }
        }

        if (_toxicChat)
        {
            // Pick at most one talker per team (whichever talkative bot wins the roll)
            foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
            {
                var caller = PickWeightedTalker(team);
                if (caller != null && Roll(_roundStartPct, caller))
                {
                    var picker = Roll(0.30f) ? ChatStyles.PickBanterLine : (Func<BotPersona, string, string>)ChatStyles.PickRoundStartLine;
                    bool toxic = picker == (Func<BotPersona, string, string>)ChatStyles.PickRoundStartLine;
                    ScheduleBotChat(caller, "", picker, teamOnly: true, isToxic: toxic);

                    // Argue chain: a different teammate may immediately disagree
                    if (toxic && Roll(_argueChancePct))
                    {
                        var dissident = PickRandomTeammateBot(caller);
                        if (dissident != null)
                            ScheduleBotChat(dissident, caller.PlayerName,
                                (_, __) => ChatStyles.PickArgueLine(_rng),
                                teamOnly: true, isToxic: true,
                                extraDelay: 0.7f + (float)_rng.NextDouble() * 1.5f);
                    }
                }
            }
        }

        // Antics
        if (_antics)
        {
            foreach (var bot in Utilities.GetPlayers())
            {
                if (!bot.IsValid || !bot.IsBot) continue;
                if (Roll(0.07f))
                {
                    var when = (float)(0.4 + _rng.NextDouble() * 1.6);
                    AddTimer(when, () => DoRandomAntic(bot));
                }
            }
        }

        // Mini-vote A/B — only on T side (where attackers actually pick site).
        // ~25% per round one talker calls "A or B?" then waits 4 sec; chat counts decide.
        if (_toxicChat && Roll(0.25f))
        {
            var caller = PickWeightedTalker(CsTeam.Terrorist);
            if (caller != null)
            {
                _siteVote[(int)CsTeam.Terrorist] = (0, 0, Server.CurrentTime + 4.5f);
                ScheduleBotChat(caller, "",
                    (_, __) => "a or b?",
                    teamOnly: true, isToxic: false);

                // Bots may "vote" via chat (their reply will hit OnPlayerChat parser)
                AddTimer(1.6f, () =>
                {
                    var teamBots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.Team == CsTeam.Terrorist).ToList();
                    foreach (var b in teamBots)
                    {
                        if (b == caller) continue;
                        if (!Roll(0.55f, b)) continue;
                        AddTimer((float)(_rng.NextDouble() * 2.0), () =>
                        {
                            if (!b.IsValid) return;
                            var pick = _rng.Next(2) == 0 ? "a" : "b";
                            try { b.ExecuteClientCommandFromServer($"say_team {pick}"); } catch { }
                        });
                    }
                });

                // Tally + announce result
                AddTimer(5.0f, () =>
                {
                    if (!_siteVote.TryGetValue((int)CsTeam.Terrorist, out var v)) return;
                    var winner = v.a > v.b ? "A" : v.b > v.a ? "B" : (_rng.Next(2) == 0 ? "A" : "B");
                    var anyTalker = PickWeightedTalker(CsTeam.Terrorist);
                    if (anyTalker != null)
                    {
                        ScheduleBotChat(anyTalker, "",
                            (_, __) => $"ok {winner.ToLowerInvariant()} it is, lets go",
                            teamOnly: true, isToxic: false);
                        // also radio
                        RadioFromBot(anyTalker, Radio_TakePoint, baseChance: 0.7f);
                    }
                    _siteVote.Remove((int)CsTeam.Terrorist);
                });
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd e, GameEventInfo info)
    {
        _inFreezePeriod = true;
        _lastRoundEndWall = Server.CurrentTime;
        var winner = (CsTeam)e.Winner;

        // v0.11: bump streaks + economy
        _econ.OnRoundEnd(winner);
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (bot.Team == winner)      _decisions.OnRoundWonForBot(bot.Slot);
            else if (bot.Team > CsTeam.Spectator) _decisions.OnRoundLostForBot(bot.Slot);
            // Detect clutches that ended in our team's victory
            if (_clutch.IsClutching(bot.Slot) && bot.Team == winner)
            {
                _clutch.Resolve(bot.Slot, won: true);
                _decisions.OnClutchWon(bot.Slot);
                LogBehavior("CLUTCH_WON", $"{bot.PlayerName} (slot {bot.Slot})");
                // Trigger a smug clutch chat line (separate from existing GG path)
                if (Roll(0.85f, bot))
                {
                    AddTimer(0.4f + (float)_rng.NextDouble() * 0.8f, () =>
                    {
                        if (!bot.IsValid) return;
                        ScheduleBotChat(bot, "", (_, __) => ChatStyles.PickClutch(_rng),
                            teamOnly: false, isToxic: true);
                    });
                }
            }
        }

        if (!_toxicChat) return HookResult.Continue;
        var winT  = PickWeightedTalker(winner);
        if (winT != null && Roll(_winChatPct, winT))
            ScheduleBotChat(winT, "", ChatStyles.PickWinLine, teamOnly: false, isToxic: true);
        var lossSide = winner == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        var losT  = PickWeightedTalker(lossSide);
        if (losT != null && Roll(_loseChatPct, losT))
            ScheduleBotChat(losT, "", ChatStyles.PickLoseLine, teamOnly: true, isToxic: false);

        // GG broadcast — friendly bots from BOTH teams may post
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (!_botPersonas.TryGetValue(bot.Slot, out var per)) continue;
            float personalGGPct = per.Mood switch
            {
                Friendliness.Friendly => _ggPct,
                Friendliness.Neutral  => _ggPct * 0.5f,
                _                     => _ggPct * 0.10f,
            };
            if (Roll(personalGGPct, bot))
            {
                AddTimer(0.5f + (float)_rng.NextDouble() * 2.0f, () =>
                {
                    if (!bot.IsValid) return;
                    ScheduleBotChat(bot, "", (_, __) => ChatStyles.PickGGLine(_rng), teamOnly: false, isToxic: false);
                });
            }
        }

        // Idle banter during the post-round delay
        ScheduleFreezePeriodIdle(durationSec: 4.5f);

        // v0.13: chad-streak gloating — bots on a hot run gloat after winning rounds.
        // 1 chad gloater per round at most (to avoid spam).
        var chadCandidates = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.IsBot && p.Team == winner && _decisions.IsOnChadStreak(p.Slot))
            .ToList();
        if (chadCandidates.Count > 0 && _toxicChat && Roll(0.55f))
        {
            var chad = chadCandidates[_rng.Next(chadCandidates.Count)];
            LogBehavior("CHAD_GLOAT", chad.PlayerName);
            AddTimer(1.0f + (float)_rng.NextDouble() * 1.6f, () =>
            {
                if (!chad.IsValid) return;
                ScheduleBotChat(chad, "", (_, __) => ChatStyles.PickStreakHype(chad.PlayerName, _rng),
                    teamOnly: false, isToxic: true);
            });
        }

        // v0.13: Rage-quit — prefer a HARD-TILTED bot if any. Bot's match arc
        // earned this; not just RNG. Falls back to random pick if nobody is tilted.
        if (Server.CurrentTime - _lastRageQuitTime > 180f && Roll(_ragequitPctPerRound))
        {
            var allBots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot).ToList();
            var tiltedBots = allBots.Where(b => _decisions.IsHardTilted(b.Slot)).ToList();
            // 75% prefer tilted; 25% random — keeps some unpredictability
            var pool = (tiltedBots.Count > 0 && _rng.NextDouble() < 0.75) ? tiltedBots : allBots;
            if (pool.Count > 0)
            {
                var quitter = pool[_rng.Next(pool.Count)];
                _lastRageQuitTime = Server.CurrentTime;
                LogBehavior("RAGEQUIT", $"{quitter.PlayerName} tilted={(_decisions.IsHardTilted(quitter.Slot)?1:0)}");
                ScheduleBotChat(quitter, "", (_, __) => ChatStyles.PickRageQuit(_rng), teamOnly: false, isToxic: true);
                AddTimer(4f + (float)_rng.NextDouble() * 3f, () =>
                {
                    if (!quitter.IsValid) return;
                    Server.ExecuteCommand($"kickid {quitter.UserId} \"rage quit\"");
                });
            }
        }

        // Random vote-kick from "I just don't like him" pool (very rare)
        if (Server.CurrentTime - _lastVoteCallTime > 90f && Roll(_voteRandomPerMatchPct))
            TryRandomVoteKick();

        // Random connection-loss / tech pause (very rare)
        if (!_techPauseActive
            && Server.CurrentTime - _lastDisconnectTime > 240f
            && Roll(0.012f))
        {
            TryDisconnectScenario();
        }

        return HookResult.Continue;
    }

    private void TryDisconnectScenario()
    {
        var bots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot).ToList();
        if (bots.Count == 0) return;
        var victim = bots[_rng.Next(bots.Count)];
        if (!victim.IsValid) return;

        _lastDisconnectTime = Server.CurrentTime;
        _techPauseActive = true;
        var name = victim.PlayerName;

        // Decide reconnect timing
        var roll = _rng.NextDouble();
        float reconnectDelay;
        bool willReconnect = true;
        if (roll < 0.65)      reconnectDelay = 5f + (float)_rng.NextDouble() * 5f;     // 5-10s
        else if (roll < 0.87) reconnectDelay = 30f + (float)_rng.NextDouble() * 30f;   // 30-60s
        else if (roll < 0.97) reconnectDelay = 90f + (float)_rng.NextDouble() * 90f;   // 90-180s (~1-2 rounds)
        else { reconnectDelay = 0f; willReconnect = false; }                           // never returns

        Server.PrintToChatAll($" {ChatColors.Olive}[INSANITY]{ChatColors.Default} {name} {ChatColors.Yellow}lost connection{ChatColors.Default} — tech pause");

        // Kick & freeze quota so fill doesn't auto-replace this slot
        try
        {
            var qcv = ConVar.Find("bot_quota");
            if (qcv != null)
            {
                if (int.TryParse(qcv.StringValue, out var q))
                {
                    _quotaBeforeDisc = q;
                    Server.ExecuteCommand($"bot_quota {Math.Max(0, q - 1)}");
                }
            }
        } catch { /* best-effort */ }

        // Try to pause the match (best-effort — server may ignore in non-comp configs)
        Server.ExecuteCommand("mp_pause_match");

        AddTimer(0.5f, () =>
        {
            if (!victim.IsValid) return;
            Server.ExecuteCommand($"kickid {victim.UserId} \"connection problem\"");
        });

        if (willReconnect)
        {
            AddTimer(reconnectDelay, () =>
            {
                _techPauseActive = false;
                Server.ExecuteCommand("mp_unpause_match");
                if (_quotaBeforeDisc >= 0)
                {
                    Server.ExecuteCommand($"bot_quota {_quotaBeforeDisc}");
                    _quotaBeforeDisc = -1;
                }
                Server.PrintToChatAll($" {ChatColors.Olive}[INSANITY]{ChatColors.Default} {name} {ChatColors.Yellow}reconnected{ChatColors.Default} — match resumed");
            });
        }
        else
        {
            // Permanent — just unpause without restoring quota
            AddTimer(8f, () =>
            {
                _techPauseActive = false;
                Server.ExecuteCommand("mp_unpause_match");
                Server.PrintToChatAll($" {ChatColors.Olive}[INSANITY]{ChatColors.Default} {name} {ChatColors.Yellow}didn't reconnect{ChatColors.Default} — playing without him");
            });
        }
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd e, GameEventInfo info)
    {
        _inFreezePeriod = false;
        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused e, GameEventInfo info)
    {
        if (!_toxicChat) return HookResult.Continue;
        var defuser = e.Userid;
        if (defuser is { IsValid: true, IsBot: true } && Roll(0.55f, defuser))
            ScheduleBotChat(defuser, "", (_, __) => ChatStyles.PickDefuse(_rng), teamOnly: true, isToxic: false);
        return HookResult.Continue;
    }

    private HookResult OnGrenadeThrown(EventGrenadeThrown e, GameEventInfo info)
    {
        var thrower = e.Userid;
        if (thrower is null || !thrower.IsValid || !thrower.IsBot) return HookResult.Continue;
        if (!_toxicChat) return HookResult.Continue;
        // 8% mock chance from a teammate, 4% praise chance
        if (Roll(0.08f))
        {
            var mate = PickRandomTeammateBot(thrower);
            if (mate != null && mate != thrower)
                ScheduleBotChat(mate, thrower.PlayerName,
                    (_, who) => ChatStyles.PickNadeMock(who, _rng),
                    teamOnly: false, isToxic: true);
        }
        else if (Roll(0.04f))
        {
            var mate = PickRandomTeammateBot(thrower);
            if (mate != null && mate != thrower)
                ScheduleBotChat(mate, thrower.PlayerName,
                    (_, __) => ChatStyles.PickNadePraise(_rng),
                    teamOnly: true, isToxic: false);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerChat(EventPlayerChat e, GameEventInfo info)
    {
        // Hook attached as RegisterEventHandler<EventPlayerChat>; this fires for every chat msg.
        var sayer = Utilities.GetPlayerFromUserid(e.Userid);
        if (sayer is null || !sayer.IsValid) return HookResult.Continue;
        var raw = (e.Text ?? string.Empty).ToLowerInvariant();

        // ---- Detect "sorry" keywords from a teammate; soften their FF outcome ----
        if (SorryKeywords.Any(k => raw.Contains(k)))
        {
            // Mark sorry against ANY teammate they recently damaged this round
            foreach (var k in _ffDamageRound.Keys.Where(k => k.a == sayer.Slot).ToList())
                _recentSorry[k] = Server.CurrentTime;
        }

        // ---- Mini-vote A/B response detection ----
        if (raw.Trim() is "a" or "b" or "go a" or "go b" or "lets go a" or "lets go b" || raw.Contains("rush a") || raw.Contains("rush b"))
        {
            int teamKey = (int)sayer.Team;
            if (_siteVote.TryGetValue(teamKey, out var v) && Server.CurrentTime < v.expires)
            {
                if (raw.Contains("a")) v.a++; else if (raw.Contains("b")) v.b++;
                _siteVote[teamKey] = v;
            }
        }

        // ---- Bots react to humans typing in chat (rare, only talker bots) ----
        if (_toxicChat && !sayer.IsBot && Roll(0.15f))
        {
            var teammates = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot).ToList();
            if (teammates.Count > 0)
            {
                var responder = PickWeightedTalker(sayer.Team) ?? teammates[_rng.Next(teammates.Count)];
                if (responder != null && Roll(0.50f, responder))
                    ScheduleBotChat(responder, sayer.PlayerName,
                        (_, __) => ChatStyles.PickHumanChatReact(_rng),
                        teamOnly: false, isToxic: true);
            }
        }

        // v0.17: bots ACK strategy calls from human teammate. "rush b", "split a",
        // "default", "stack b", "save", "eco" trigger 1-2 same-team bot replies.
        if (!sayer.IsBot && sayer.Team > CsTeam.Spectator)
        {
            string? stratKind = DetectStratCall(raw);
            if (stratKind != null)
            {
                LogBehavior("STRAT_HEARD", $"{sayer.PlayerName} called {stratKind}");
                var teammateBots = Utilities.GetPlayers()
                    .Where(p => p.IsValid && p.IsBot && p.Team == sayer.Team).ToList();
                int n = Math.Min(2, teammateBots.Count);
                for (int i = 0; i < n; i++)
                {
                    if (!Roll(0.55f)) continue;
                    var b = teammateBots[_rng.Next(teammateBots.Count)];
                    if (!_botPersonas.TryGetValue(b.Slot, out var per)) continue;
                    AddTimer(0.6f + i * 0.5f + (float)_rng.NextDouble() * 0.7f, () =>
                    {
                        if (!b.IsValid) return;
                        ScheduleBotChat(b, sayer.PlayerName,
                            (_, __) => ChatStyles.PickStratAck(per, stratKind!, _rng),
                            teamOnly: true, isToxic: per.Mood == Friendliness.Hostile);
                    });
                }
            }
        }
        return HookResult.Continue;
    }

    /// v0.17: classify a chat message into a "strat call" kind, or null if not one.
    private static string? DetectStratCall(string raw)
    {
        // Trim down stop words
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Rush calls
        if (raw.Contains("rush a") || raw.Contains("rush b") || raw.Contains("раш"))
            return "rush";
        // Split (typically a-site mid+long pinch)
        if (raw.Contains("split a") || raw.Contains("split b") || raw.Contains("split"))
            return "split";
        // Default / slow play
        if (raw.Contains("default") || raw.Contains("slow") || raw.Contains("default it") || raw.Contains("дефолт"))
            return "default";
        // Stack
        if (raw.Contains("stack a") || raw.Contains("stack b") || raw.Contains("stack"))
            return "stack";
        // Eco / save
        if (raw == "eco" || raw == "save" || raw.Contains("save round") || raw.Contains("экo") || raw.Contains("сейв"))
            return "eco";
        // Force buy
        if (raw.Contains("force") || raw.Contains("force buy") || raw.Contains("форс"))
            return "force";
        // Fast push (less specific than rush)
        if (raw.Contains("fast a") || raw.Contains("fast b") || raw == "go" || raw.Contains("lets go"))
            return "fast";
        return null;
    }

    private HookResult OnDecoyStarted(EventDecoyStarted e, GameEventInfo info)
    {
        var thrower = e.Userid;
        if (thrower is null || !thrower.IsValid || !thrower.IsBot) return HookResult.Continue;
        if (!_toxicChat) return HookResult.Continue;
        if (Roll(0.18f))
        {
            var mate = PickRandomTeammateBot(thrower);
            if (mate != null && mate != thrower)
                ScheduleBotChat(mate, thrower.PlayerName,
                    (_, who) => ChatStyles.PickNadeMock(who, _rng),
                    teamOnly: false, isToxic: true);
        }
        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted e, GameEventInfo info)
    {
        var p = e.Userid;
        // v0.9: read site from event (e.Site: 0=A, 1=B). CSSharp exposes it as int.
        int siteIdx = -1;
        try { siteIdx = e.Site; } catch { }
        var siteName = siteIdx == 0 ? "a" : siteIdx == 1 ? "b" : (_rng.Next(2) == 0 ? "a" : "b");

        if (p is { IsValid: true, IsBot: true })
        {
            if (_toxicChat && Roll(_plantChatPct, p))
                ScheduleBotChat(p, "", ChatStyles.PickPlantLine, teamOnly: true, isToxic: false);
            RadioFromBot(p, Radio_InPosition, baseChance: 0.6f);
        }

        // v0.9: CT-side "PLANTED A/B" callout from an alive CT bot, dedup-gated
        if (_calloutsEnabled && _toxicChat)
        {
            var ctCaller = PickIGLOrTalker(CsTeam.CounterTerrorist);
            if (ctCaller != null && Roll(0.7f, ctCaller))
            {
                var line = ChatStyles.PickCalloutFixed(ChatStyles.Callout_Planted, siteName, "", _rng);
                ScheduleCalloutChat(ctCaller, line, zoneKey: siteName, teamOnly: true, isToxic: false, responsePct: 0.40f);
            }
        }

        // CT side: a teammate may radio "get out" (defuse callout)
        var ctBots = Utilities.GetPlayers().Where(b => b.IsValid && b.IsBot && b.Team == CsTeam.CounterTerrorist).ToList();
        if (ctBots.Count > 0 && Roll(0.40f))
        {
            var anchor = ctBots[_rng.Next(ctBots.Count)];
            RadioFromBot(anchor, Radio_NeedBackup, baseChance: 1.0f);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerFootstep(EventPlayerFootstep e, GameEventInfo info)
    {
        ReactToSound(e.Userid, "step", 0f);
        // 8% chance the closest hearing teammate also fires "Enemy spotted!" radio
        if (e.Userid is { IsValid: true } src && !src.IsBot && Roll(0.08f))
        {
            var caller = ClosestHearingBot(src, 1100f);
            if (caller != null) RadioFromBot(caller, Radio_EnemySpot, baseChance: 1.0f);
        }
        // v0.9: rare zone-aware footstep chat callout (heavy dedup so it doesn't spam)
        if (_calloutsEnabled && _toxicChat && e.Userid is { IsValid: true } src2 && !src2.IsBot && Roll(0.04f))
        {
            var sp = src2.PlayerPawn?.Value;
            if (sp?.IsValid == true)
            {
                var pos = sp.AbsOrigin;
                var oppTeam = src2.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
                var caller2 = ClosestTeammateBot(oppTeam, pos, 1100f);
                if (caller2 != null && Roll(0.55f, caller2))
                {
                    var zone = PickZoneForPosOrRandom(pos, Server.MapName ?? "");
                    var line = ChatStyles.PickCalloutFixed(ChatStyles.Callout_Footsteps, zone, "", _rng);
                    var key = ChatStyles.ZoneKeyFor(Server.MapName ?? "", line);
                    ScheduleCalloutChat(caller2, line, key, teamOnly: true, isToxic: false, responsePct: 0.20f);
                }
            }
        }
        return HookResult.Continue;
    }
    private HookResult OnPlayerJump    (EventPlayerJump     e, GameEventInfo info) { ReactToSound(e.Userid, "jump", 100f); return HookResult.Continue; }
    private HookResult OnWeaponZoom    (EventWeaponZoom     e, GameEventInfo info) { ReactToSound(e.Userid, "scope", 200f); return HookResult.Continue; }
    private HookResult OnWeaponFire(EventWeaponFire e, GameEventInfo info)
    {
        ReactToSound(e.Userid, "shot", 800f);
        var p = e.Userid;
        if (p is { IsValid: true, IsBot: true })
            _combatUntil[p.Slot] = Server.CurrentTime + 1.2f;

        // 0.7: sound-localization — non-deaf nearby teammate bots turn toward shot origin
        // and "lock" on the predicted point with small noise.
        if (p is { IsValid: true })
            BroadcastShotToNearbyAllies(p);
        return HookResult.Continue;
    }

    private void BroadcastShotToNearbyAllies(CCSPlayerController shooter)
    {
        var sp = shooter.PlayerPawn?.Value;
        if (sp?.IsValid != true) return;
        var spos = sp.AbsOrigin;
        if (spos == null) return;
        const float HEARING_RADIUS = 1500f;
        var rsq = HEARING_RADIUS * HEARING_RADIUS;

        // v0.9: rare "shots {z}" callout from the closest ENEMY bot if shot was from human / enemy
        // We flip this so the team that HEARS the shooter gets the call (not the team firing).
        if (_calloutsEnabled && _toxicChat && Roll(0.10f))
        {
            var oppTeam = shooter.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
            var oppCaller = ClosestTeammateBot(oppTeam, spos, HEARING_RADIUS);
            if (oppCaller != null && Roll(0.45f, oppCaller))
            {
                var zone = PickZoneForPosOrRandom(spos, Server.MapName ?? "");
                var line = ChatStyles.PickCalloutFixed(ChatStyles.Callout_ShotsFired, zone, "", _rng);
                var key = ChatStyles.ZoneKeyFor(Server.MapName ?? "", line);
                ScheduleCalloutChat(oppCaller, line, key, teamOnly: true, isToxic: false, responsePct: 0.20f);
            }
        }

        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (bot == shooter) continue;
            if (bot.Team != shooter.Team) continue;       // only TEAMMATES auto-orient (enemies use existing hearing chat path)
            if (_botPersonas.TryGetValue(bot.Slot, out var per) && per.IsDeaf) continue;
            var bp = bot.PlayerPawn?.Value;
            if (bp?.IsValid != true || bp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var bpos = bp.AbsOrigin; if (bpos == null) continue;
            var dx = bpos.X - spos.X; var dy = bpos.Y - spos.Y; var dz = bpos.Z - spos.Z;
            var d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > rsq) continue;

            // Predicted point with imprecision (±60 units lateral)
            var noiseX = ((float)_rng.NextDouble() - 0.5f) * 120f;
            var noiseY = ((float)_rng.NextDouble() - 0.5f) * 120f;
            var fakePos = new CounterStrikeSharp.API.Modules.Utils.Vector(spos.X + noiseX, spos.Y + noiseY, spos.Z);
            ForceLookAt(bot, fakePos, durSec: 0.6f + (float)_rng.NextDouble() * 0.6f);
        }
    }

    // ================================================================================
    //  Hearing
    // ================================================================================

    private void ReactToSound(CCSPlayerController? src, string kind, float radiusBoost)
    {
        if (!_hearingEnabled || !_toxicChat) return;
        if (src is not { IsValid: true } || src.IsBot) return;

        var sp = src.PlayerPawn?.Value;
        if (sp?.IsValid != true) return;
        var srcPos = sp.AbsOrigin;
        if (srcPos == null) return;
        var radius = _hearingRadiusUnits + radiusBoost;
        var radiusSq = radius * radius;

        // We pick at most ONE bot per sound event (the closest hearing bot of opp team)
        CCSPlayerController? bestBot = null;
        float bestD2 = float.MaxValue;
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            if (bot.Team == src.Team) continue;
            // Deaf bots don't react to footsteps/jumps/scopes/shots
            if (_botPersonas.TryGetValue(bot.Slot, out var p) && p.IsDeaf) continue;
            var bp = bot.PlayerPawn?.Value;
            if (bp?.IsValid != true || bp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var bpos = bp.AbsOrigin; if (bpos == null) continue;
            var dx = bpos.X - srcPos.X; var dy = bpos.Y - srcPos.Y; var dz = bpos.Z - srcPos.Z;
            var d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > radiusSq) continue;
            if (d2 < bestD2) { bestD2 = d2; bestBot = bot; }
        }
        if (bestBot is null) return;

        var now = Server.CurrentTime;
        if (_lastHearTime.TryGetValue(bestBot.Slot, out var t) && now - t < 9f) return;
        _lastHearTime[bestBot.Slot] = now;

        if (Roll(_hearingChatPct, bestBot))
            ScheduleBotChat(bestBot, src.PlayerName,
                (s, who) => ChatStyles.PickHearLine(s, kind, who, _rng),
                teamOnly: true, isToxic: true);
    }

    // ================================================================================
    //  Antics & coordination
    // ================================================================================

    private void DoRandomAntic(CCSPlayerController bot)
    {
        if (!bot.IsValid || !bot.IsBot) return;
        var pawn = bot.PlayerPawn?.Value;
        if (pawn?.IsValid != true || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
        // Velocity-modifying antics removed in 0.6.6 — they were yeeting bots/players at
        // spawn and causing the "drift" feeling. Only visual antics remain.
        switch (_rng.Next(2))
        {
            case 0:
                _lookUntil[bot.Slot] = Server.CurrentTime + 0.9f;
                _forceLook[bot.Slot] = ((float)_rng.NextDouble() * 360f - 180f, -10f + (float)_rng.NextDouble() * 20f);
                break;
            case 1:
                if (_toxicChat && Roll(0.5f)) ScheduleBotChat(bot, "", ChatStyles.PickBanterLine, teamOnly: true, isToxic: false);
                break;
        }
    }

    private void TriggerCoordReaction(CCSPlayerController dead)
    {
        var dpawn = dead.PlayerPawn?.Value;
        if (dpawn?.IsValid != true) return;
        var deadPos = dpawn.AbsOrigin; if (deadPos == null) return;
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot || bot == dead) continue;
            if (bot.Team != dead.Team) continue;
            var bp = bot.PlayerPawn?.Value;
            if (bp?.IsValid != true || bp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var bpos = bp.AbsOrigin; if (bpos == null) continue;
            var dx = bpos.X - deadPos.X; var dy = bpos.Y - deadPos.Y; var dz = bpos.Z - deadPos.Z;
            if (dx * dx + dy * dy + dz * dz > 600f * 600f) continue;
            if (!Roll(0.12f)) continue;

            switch (_rng.Next(3))
            {
                case 0: {
                    var yaw0 = bp.EyeAngles.Y;
                    _lookUntil[bot.Slot] = Server.CurrentTime + 0.8f;
                    _forceLook[bot.Slot] = (yaw0 + 35f, bp.EyeAngles.X);
                    if (_toxicChat && Roll(0.5f))
                        ScheduleBotChat(bot, dead.PlayerName, (_, __) => ChatStyles.PickCoordHeadShake(_rng), teamOnly: true, isToxic: false);
                    break;
                }
                case 1: {
                    var yaw = (float)(MathF.Atan2(deadPos.Y - bpos.Y, deadPos.X - bpos.X) * 180f / MathF.PI);
                    _lookUntil[bot.Slot] = Server.CurrentTime + 1.0f;
                    _forceLook[bot.Slot] = (yaw, 0f);
                    if (_toxicChat)
                        ScheduleBotChat(bot, dead.PlayerName, (_, who) => ChatStyles.PickCoordNoYouFirst(who, _rng), teamOnly: true, isToxic: false);
                    break;
                }
                case 2: {
                    var yaw = (float)(MathF.Atan2(deadPos.Y - bpos.Y, deadPos.X - bpos.X) * 180f / MathF.PI);
                    _lookUntil[bot.Slot] = Server.CurrentTime + 0.7f;
                    _forceLook[bot.Slot] = (yaw, -10f);
                    break;
                }
            }
        }
    }

    // ================================================================================
    //  OnTick: aim + strafe + typing freeze + force look + AFK + body-bump detect
    // ================================================================================

    private void OnTick()
    {
        var now = Server.CurrentTime;

        try { _aim.Tick(); } catch { }
        // v0.18: throttle clutch refresh to ~5Hz — state changes infrequently
        if (now - _lastClutchRefreshAt > 0.18f)
        {
            _lastClutchRefreshAt = now;
            try { _clutch.Refresh(); } catch { }
        }
        // v0.18: flush behavior log at most every 2s
        if (now - _lastBehaviorFlushAt > 2.0f)
        {
            _lastBehaviorFlushAt = now;
            FlushBehaviorLog();
        }

        // v0.14: detect last-man transition → fire pre-clutch announcement
        // (bypasses ChatSuppression once via bypassDecisions flag).
        try
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (!p.IsValid || !p.IsBot) continue;
                if (_preClutchAnnouncedThisRound.Contains(p.Slot)) continue;
                if (!_clutch.IsClutching(p.Slot)) continue;
                var st = _clutch.Get(p.Slot);
                if (st == null) continue;
                // Just-became-last-man: ClutchStartedAt within last 0.5s
                if (now - st.ClutchStartedAt > 0.5f) continue;
                _preClutchAnnouncedThisRound.Add(p.Slot);
                if (!_botPersonas.TryGetValue(p.Slot, out var per)) { continue; }
                int opp = st.OpponentsAlive;
                // Mood-skewed probability: friendly+hostile fire more (info call / blame),
                // neutral less (focused).
                float baseChance = per.Mood switch
                {
                    Friendliness.Hostile  => 0.55f,
                    Friendliness.Friendly => 0.40f,
                    _                     => 0.25f,
                };
                if (!Roll(baseChance)) continue;
                LogBehavior("PRE_CLUTCH", $"{p.PlayerName} 1v{opp} mood={per.Mood}");
                // Pick teammate ref for "trade me {ref}" lines (the bot who just died)
                var refName = p.PlayerName;
                AddTimer(0.6f + (float)_rng.NextDouble() * 1.4f, () =>
                {
                    if (!p.IsValid) return;
                    var line = ChatStyles.PickPreClutch(per, opp, _rng);
                    line = line.Replace("{ref}", refName);
                    // Bypass ChatSuppression by writing directly via ScheduleBotChat
                    // — Roll() suppression isn't applied since we already chose to fire.
                    ScheduleBotChat(p, "", (_, __) => line, teamOnly: true, isToxic: per.Mood == Friendliness.Hostile);
                });
            }
        } catch { }

        // v0.21: spec-mock — dead bot has been spectating ≥10s, may snark
        // about a living teammate's play. 1× per dead bot per round.
        try
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (!p.IsValid || !p.IsBot) continue;
                if (_specMockedThisRound.Contains(p.Slot)) continue;
                var pp = p.PlayerPawn?.Value;
                if (pp == null) continue;
                if (pp.LifeState == (byte)LifeState_t.LIFE_ALIVE) continue;  // alive — skip
                if (!_botDeathTime.TryGetValue(p.Slot, out var dt)) continue;
                float dead = now - dt;
                if (dead < 10f || dead > 35f) continue;  // 10-35s window
                if (!_botPersonas.TryGetValue(p.Slot, out var per)) continue;
                // Mood-skewed chance — hostile most likely to mock
                float chance = per.Mood switch
                {
                    Friendliness.Hostile  => 0.10f,
                    Friendliness.Friendly => 0.04f,
                    _                     => 0.05f,
                };
                if (!Roll(chance)) continue;
                // Pick living teammate to target
                var alive = Utilities.GetPlayers()
                    .Where(t => t.IsValid && t != p && t.Team == p.Team
                        && t.PlayerPawn?.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE)
                    .ToList();
                if (alive.Count == 0) continue;
                var target = alive[_rng.Next(alive.Count)];
                _specMockedThisRound.Add(p.Slot);
                var refName = ChatStyles.RefName(target.PlayerName, (int)target.Team, _rng);
                var zone = ChatStyles.PickZoneFor(Server.MapName ?? "", _rng);
                LogBehavior("SPEC_MOCK", $"{p.PlayerName} → {target.PlayerName} mood={per.Mood}");
                AddTimer(0.4f + (float)_rng.NextDouble() * 1.3f, () =>
                {
                    if (!p.IsValid) return;
                    ScheduleBotChat(p, "", (_, __) => ChatStyles.PickSpecMock(per, refName, zone, _rng),
                        teamOnly: true, isToxic: per.Mood == Friendliness.Hostile);
                });
            }
        } catch { }

        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            var pawn = bot.PlayerPawn?.Value;
            if (pawn?.IsValid != true) continue;
            if (pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;

            // AFK — full freeze, skip all other logic
            if (_afkUntil.TryGetValue(bot.Slot, out var au) && now < au)
            {
                var v = pawn.AbsVelocity;
                v.X = 0f; v.Y = 0f; v.Z = MathF.Min(v.Z, 0f);
                continue;
            }

            // Mid-round AFK roll (per-tick converted: pct/sec × dt)
            if (Roll(_afkMidRoundPct * 0.030f) && !_afkUntil.ContainsKey(bot.Slot))
            {
                var dur = 4f + (float)_rng.NextDouble() * 11f;
                _afkUntil[bot.Slot] = now + dur;
                if (_toxicChat && Roll(0.30f))
                    ScheduleBotChat(bot, "", (_, __) => ChatStyles.PickAFKHeadsUp(_rng), teamOnly: true, isToxic: false);
                continue;
            }

            // Typing freeze
            if (_typingTimeEnabled && _typingUntil.TryGetValue(bot.Slot, out var tu) && now < tu)
            {
                var v = pawn.AbsVelocity;
                v.X = 0f; v.Y = 0f;
                continue;
            }

            // Force-look (antic / coordination)
            if (_lookUntil.TryGetValue(bot.Slot, out var lu) && now < lu && _forceLook.TryGetValue(bot.Slot, out var ang))
            {
                var ea = pawn.EyeAngles;
                ea.Y = ang.yaw;
                ea.X = ang.pitch;
                if (_rng.NextDouble() < 0.4) ang.yaw += ((float)_rng.NextDouble() - 0.5f) * 30f;
            }

            // Forced attack window (FF revenge) + v0.10 movement-realism button pulses
            // (walk/duck/jump). Centralised — see MovementRealism.cs.
            ApplyButtonPulses(bot, pawn, now);

            // v0.10: pre-aim + walk-pulse + shoulder-peek (buttons-only, no velocity writes).
            if (_movementRealismEnabled)
                MovementRealismTick(bot, pawn, now);

            // (Strafe-nudge velocity push removed in 0.6.6 — was the source of the
            //  random-drift bug that affected bots and humans alike.)

            // Body-block detection
            CheckBodyBump(bot, pawn, now);
        }
    }

    private void CheckBodyBump(CCSPlayerController bot, CCSPlayerPawn pawn, float now)
    {
        // Don't accuse anyone of blocking during freezetime — bots can't move yet
        if (_inFreezePeriod)
        {
            _lastMovingTime[bot.Slot] = now;
            return;
        }

        var av = pawn.AbsVelocity;
        var spXY = MathF.Sqrt(av.X * av.X + av.Y * av.Y);
        if (spXY > 30f)
        {
            _lastMovingTime[bot.Slot] = now;
            return;
        }

        // bot is essentially still — has it been still for a while?
        var sinceMoved = now - (_lastMovingTime.TryGetValue(bot.Slot, out var lm) ? lm : now);
        if (sinceMoved < _bodyBumpRequireSec) return;

        // cooldown: don't bump same teammate every tick
        if (_lastBumpTime.TryGetValue(bot.Slot, out var lb) && now - lb < 1.6f) return;

        // Find a teammate within 80u who is in front of this bot (likely blocking)
        var origin = pawn.AbsOrigin;
        if (origin == null) return;
        var yaw = pawn.EyeAngles.Y * MathF.PI / 180f;
        var fwdX = MathF.Cos(yaw);
        var fwdY = MathF.Sin(yaw);

        CCSPlayerController? blocker = null;
        float bestDot = 0.5f;            // ~60° forward cone
        float blockerDist2 = 80f * 80f;
        foreach (var mate in Utilities.GetPlayers())
        {
            if (!mate.IsValid || mate == bot) continue;
            if (mate.Team != bot.Team) continue;
            var mp = mate.PlayerPawn?.Value;
            if (mp?.IsValid != true || mp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var mo = mp.AbsOrigin; if (mo == null) continue;
            var dx = mo.X - origin.X; var dy = mo.Y - origin.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > blockerDist2) continue;
            var inv = 1f / MathF.Max(1e-3f, MathF.Sqrt(d2));
            var dot = (dx * inv) * fwdX + (dy * inv) * fwdY;
            if (dot > bestDot)
            {
                bestDot = dot;
                blocker = mate;
                blockerDist2 = d2;
            }
        }
        if (blocker is null) return;

        // Roll
        if (!Roll(_bodyBumpPct * 0.030f)) return;     // per-tick to per-bump prob
        _lastBumpTime[bot.Slot] = now;

        BumpTeammate(bot, blocker, isBot: blocker.IsBot);
    }

    private void BumpTeammate(CCSPlayerController bumper, CCSPlayerController blocker, bool isBot)
    {
        var bp = blocker.PlayerPawn?.Value;
        if (bp?.IsValid != true) return;

        // Damage the blocker directly
        var dmg = (int)MathF.Round(_bodyBumpDamage + ((float)_rng.NextDouble() * 8f - 4f));
        if (dmg < 4) dmg = 4;
        var newHp = bp.Health - dmg;
        if (newHp < 1) newHp = 1;
        bp.Health = newHp;

        // Track in FF ledger so escalation/rage can fire
        var key = (v: blocker.Slot, a: bumper.Slot);
        _ffDamageRound.TryGetValue(key, out var sum);
        _ffDamageRound[key] = sum + dmg;
        _bumpsThisRound[bumper.Slot] = _bumpsThisRound.TryGetValue(bumper.Slot, out var n) ? n + 1 : 1;

        // Bumper barks "MOVE", possibly post-tk smug line
        if (_toxicChat)
        {
            ScheduleBotChat(bumper, blocker.PlayerName, (_, __) => ChatStyles.PickBodyBlockRage(_rng), teamOnly: true, isToxic: true);
            if (_bumpsThisRound[bumper.Slot] >= 2)
                AddTimer(1.4f + (float)_rng.NextDouble(), () =>
                {
                    if (!bumper.IsValid) return;
                    ScheduleBotChat(bumper, blocker.PlayerName,
                        (_, __) => ChatStyles.PickBodyBlockAfterTK(_rng),
                        teamOnly: true, isToxic: true);
                });
        }

        // Blocker (if bot) reacts angrily
        if (isBot && _toxicChat && Roll(0.6f, blocker))
            ScheduleBotChat(blocker, bumper.PlayerName,
                (_, who) => ChatStyles.PickFFPostBumpVictim(who, _rng),
                teamOnly: false, isToxic: true);

        // Heavy bumping → escalate to vote kick
        if (_ffDamageRound[key] > 70 && Server.CurrentTime - _lastVoteCallTime > 60f && Roll(0.30f))
        {
            var initiator = isBot ? blocker : PickWeightedTalker(blocker.Team);
            if (initiator != null && initiator != bumper && initiator.IsBot)
                CallVoteKick(initiator: initiator, target: bumper, reason: "team damage");
        }
    }

    // ================================================================================
    //  Freeze-period idle banter
    // ================================================================================

    private void ScheduleFreezePeriodIdle(float durationSec)
    {
        // Once per second roll for an idle banter line from a random talkative bot
        int steps = (int)MathF.Ceiling(durationSec);
        for (int i = 1; i <= steps; i++)
        {
            var t = (float)i;
            AddTimer(t, () =>
            {
                if (!_toxicChat) return;
                if (!Roll(_pauseIdlePctPerSec * 1f)) return;
                var allBots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot).ToList();
                if (allBots.Count == 0) return;
                // Weighted by talkativeness
                var weights = allBots.Select(b => _botPersonas.TryGetValue(b.Slot, out var per) ? per.ChatProbabilityFactor : 0.3f).ToArray();
                var total = weights.Sum();
                var roll = (float)(_rng.NextDouble() * total);
                float cum = 0f;
                CCSPlayerController? picked = null;
                for (int j = 0; j < allBots.Count; j++)
                {
                    cum += weights[j];
                    if (roll <= cum) { picked = allBots[j]; break; }
                }
                if (picked == null) return;

                if (Roll(_pauseBaitPct))
                {
                    // bait either a teammate or the human
                    var others = Utilities.GetPlayers().Where(p => p.IsValid && p != picked).ToList();
                    if (others.Count > 0)
                    {
                        var target = others[_rng.Next(others.Count)];
                        ScheduleBotChat(picked, target.PlayerName,
                            (_, who) => ChatStyles.PickPauseBait(who, _rng),
                            teamOnly: false, isToxic: true);
                    }
                }
                else
                {
                    ScheduleBotChat(picked, "", (_, __) => ChatStyles.PickPauseIdle(_rng),
                        teamOnly: false, isToxic: false);
                }
            });
        }
    }

    // ================================================================================
    //  Vote-kick & rage-quit
    // ================================================================================

    private void CallVoteKick(CCSPlayerController initiator, CCSPlayerController target, string reason)
    {
        if (target == null || !target.IsValid) return;
        if (target.IsHLTV) return;
        _lastVoteCallTime = Server.CurrentTime;

        // Initiator types reasoning
        if (_toxicChat)
            ScheduleBotChat(initiator, target.PlayerName,
                (_, who) => ChatStyles.PickVoteKickReason(who, _rng),
                teamOnly: false, isToxic: true);

        AddTimer(2.5f + (float)_rng.NextDouble() * 2f, () =>
        {
            if (!initiator.IsValid || !target.IsValid) return;
            initiator.ExecuteClientCommandFromServer($"callvote kick \"{target.UserId}\"");
        });

        // Other bots may chime in F1/F2 banter
        AddTimer(4.0f + (float)_rng.NextDouble() * 3f, () =>
        {
            foreach (var bot in Utilities.GetPlayers())
            {
                if (!bot.IsValid || !bot.IsBot || bot == initiator) continue;
                if (bot.Team != initiator.Team) continue;
                if (!Roll(0.30f, bot)) continue;
                var voteYes = Roll(0.7f);
                ScheduleBotChat(bot, target.PlayerName,
                    (_, __) => voteYes ? ChatStyles.PickVoteKickYes(_rng) : ChatStyles.PickVoteKickNo(_rng),
                    teamOnly: true, isToxic: voteYes);
            }
        });
    }

    private void RollRoundStrategy()
    {
        // pick T side strat each round; chance also picks CT positioning
        // v0.9: prefer IGL archetype if alive, else weighted talker
        var tCaller = PickIGLOrTalker(CsTeam.Terrorist);
        if (tCaller == null) return;
        var roll = _rng.NextDouble();
        Func<BotPersona, string, string> picker;
        string mode;
        if (roll < 0.25)      { picker = (_, __) => ChatStyles.PickStratRush(_rng);  mode = "rush"; }
        else if (roll < 0.40) { picker = (_, __) => ChatStyles.PickStratShift(_rng); mode = "shift"; }
        else if (roll < 0.55) { picker = (_, __) => ChatStyles.PickStratForce(_rng); mode = "force"; }
        else if (roll < 0.70) { picker = (_, __) => ChatStyles.PickStratEco(_rng);   mode = "eco"; }
        else                   { picker = (_, __) => ChatStyles.PickStratFull(_rng);  mode = "full"; }

        if (Roll(0.55f, tCaller))
        {
            LogBehavior("IGL_STRAT", $"{tCaller.PlayerName} called {mode}");
            ScheduleBotChat(tCaller, "", picker, teamOnly: true, isToxic: false);

            // After 5-12 sec, mock anyone (a teammate) who didn't follow — heuristic just picks
            // a random teammate and accuses them. Realistic enough.
            AddTimer(5f + (float)_rng.NextDouble() * 7f, () =>
            {
                if (!_toxicChat) return;
                var tBots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.Team == CsTeam.Terrorist && p != tCaller).ToList();
                if (tBots.Count == 0) return;
                if (!Roll(0.30f)) return;
                var loner = tBots[_rng.Next(tBots.Count)];
                ScheduleBotChat(tCaller, loner.PlayerName,
                    (_, who) => ChatStyles.PickStratNotListening(who, _rng),
                    teamOnly: true, isToxic: true);
            });
        }
    }

    private void TryRandomVoteKick()
    {
        var bots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot).ToList();
        if (bots.Count < 2) return;
        var initiator = bots[_rng.Next(bots.Count)];
        var teammates = bots.Where(b => b.Team == initiator.Team && b != initiator).ToList();
        if (teammates.Count == 0) return;
        var target = teammates[_rng.Next(teammates.Count)];
        CallVoteKick(initiator, target, "vibes");
    }

    // ================================================================================
    //  Chat scheduling (with talkativeness, native-chat, rebuke chain)
    // ================================================================================

    private bool Roll(float pct) => _rng.NextDouble() < pct;
    private bool Roll(float pct, CCSPlayerController bot)
    {
        if (!_botPersonas.TryGetValue(bot.Slot, out var per)) return _rng.NextDouble() < pct;
        // v0.11: combine persona base + decision-engine match-arc boost + clutch-suppression
        float p = pct * per.ChatProbabilityFactor;
        p *= _decisions.ChatBoost(bot.Slot, per);
        p *= _clutch.ChatSuppression(bot.Slot);
        return _rng.NextDouble() < p;
    }

    private CCSPlayerController? ClosestHearingBot(CCSPlayerController src, float maxRadius)
    {
        var sp = src.PlayerPawn?.Value; if (sp?.IsValid != true) return null;
        var sPos = sp.AbsOrigin; if (sPos == null) return null;
        CCSPlayerController? best = null; float bestD = maxRadius * maxRadius;
        foreach (var b in Utilities.GetPlayers())
        {
            if (!b.IsValid || !b.IsBot || b.Team == src.Team) continue;
            var bp = b.PlayerPawn?.Value; if (bp?.IsValid != true) continue;
            if (bp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var bpos = bp.AbsOrigin; if (bpos == null) continue;
            var dx = bpos.X - sPos.X; var dy = bpos.Y - sPos.Y; var dz = bpos.Z - sPos.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    private CCSPlayerController? PickRandomTeammateBot(CCSPlayerController src)
    {
        var pool = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.Team == src.Team && p != src).ToList();
        if (pool.Count == 0) return null;
        return pool[_rng.Next(pool.Count)];
    }

    /// Pick one bot on a team weighted by Talkativeness. Returns null if no bots.
    private CCSPlayerController? PickWeightedTalker(CsTeam team)
    {
        var bots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.Team == team).ToList();
        if (bots.Count == 0) return null;
        // Weighted random using ChatProbabilityFactor; if all silent (factor 0.05), still random.
        float total = 0f;
        foreach (var b in bots)
        {
            var per = _botPersonas.TryGetValue(b.Slot, out var p) ? p : null;
            total += per?.ChatProbabilityFactor ?? 0.30f;
        }
        if (total <= 0f) return bots[_rng.Next(bots.Count)];
        var roll = (float)(_rng.NextDouble() * total);
        float cum = 0f;
        foreach (var b in bots)
        {
            var per = _botPersonas.TryGetValue(b.Slot, out var p) ? p : null;
            cum += per?.ChatProbabilityFactor ?? 0.30f;
            if (roll <= cum) return b;
        }
        return bots[^1];
    }

    private void ScheduleBotChat(CCSPlayerController bot, string subject,
        Func<BotPersona, string, string> picker, bool teamOnly, bool isToxic, float extraDelay = 0f)
    {
        if (!_botPersonas.TryGetValue(bot.Slot, out var persona))
        {
            persona = ChatStyles.RandomPersona(_rng, bot.PlayerName);
            _botPersonas[bot.Slot] = persona;
            PushAimProfile(bot.Slot, persona);
        }
        persona.TauntRussianTarget = !string.IsNullOrEmpty(subject) && ChatStyles.TargetIsRussian(subject);

        var now = Server.CurrentTime;
        if (_lastChatTime.TryGetValue(bot.Slot, out var t) && now - t < _chatCooldownSec) return;
        if (_typingUntil.TryGetValue(bot.Slot, out var tu) && now < tu) return;
        _lastChatTime[bot.Slot] = now;

        // If subject looks like a real player name, replace with how a lazy human would refer to them
        var refSubject = subject;
        if (!string.IsNullOrEmpty(subject))
        {
            var byName = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && p.PlayerName == subject);
            if (byName != null)
                refSubject = ChatStyles.RefName(byName.PlayerName, (int)byName.Team, _rng);
        }

        var line = picker(persona, refSubject);
        if (string.IsNullOrWhiteSpace(line)) return;
        line = ChatStyles.MaybeMangle(line, persona, _rng);

        bool teamFinal = teamOnly;
        if (_wrongChatEnabled && teamOnly && Roll(_wrongChatPct))
            teamFinal = false;

        var typeSec = (_typingTimeEnabled ? ChatStyles.ComputeTypingTime(line, persona.Wpm, _rng) : 0.05f) + extraDelay;
        _typingUntil[bot.Slot] = now + typeSec;

        // mark this as recent toxic chatter for rebuke chain
        if (isToxic)
        {
            _lastToxicChatTime[bot.Slot] = now + typeSec; // use post-send time
            _lastToxicChatLine[bot.Slot] = line;

            // Schedule possible rebuke chain right after the message lands
            if (Roll(_rebukeChainPct))
            {
                AddTimer(typeSec + 0.4f + (float)_rng.NextDouble() * 1.4f, () =>
                {
                    var rebuker = PickRandomTeammateBot(bot);
                    if (rebuker == null) return;
                    if (!Roll(0.55f, rebuker)) return;     // talkativeness gate again
                    ScheduleBotChat(rebuker, bot.PlayerName,
                        (_, who) => ChatStyles.PickRebukeLine(who, _rng),
                        teamOnly: true, isToxic: false);
                });
            }
        }

        var captured = line;
        AddTimer(typeSec, () =>
        {
            if (!bot.IsValid) return;
            if (_useNativeChat)
            {
                var sayCmd = teamFinal ? "say_team" : "say";
                // Escape any embedded quotes so the source say command stays valid
                var safe = captured.Replace("\"", "");
                bot.ExecuteClientCommandFromServer($"{sayCmd} {safe}");
            }
            else
            {
                var nameColor = bot.Team == CsTeam.Terrorist ? ChatColors.Red :
                                bot.Team == CsTeam.CounterTerrorist ? ChatColors.Blue : ChatColors.Default;
                var prefix = teamFinal
                    ? bot.Team == CsTeam.Terrorist ? $"{ChatColors.Red}(TEAM){ChatColors.Default} " :
                      bot.Team == CsTeam.CounterTerrorist ? $"{ChatColors.Blue}(TEAM){ChatColors.Default} " : ""
                    : "";
                Server.PrintToChatAll($"{prefix}{nameColor}{bot.PlayerName}{ChatColors.Default}: {captured}");
            }
        });
    }

    // ================================================================================
    //  v0.9.0 — Callouts (zone-aware, dedup, echo/rebuke chain, world-event triggers)
    // ================================================================================

    /// Try to claim the zone-call dedup slot for `zoneKey`. Returns true if free.
    /// Sets a fresh cooldown of 7-12s if claimed.
    private bool TryClaimZoneCall(string? zoneKey)
    {
        if (string.IsNullOrEmpty(zoneKey)) return true;
        var now = Server.CurrentTime;
        if (_zoneCalloutCooldown.TryGetValue(zoneKey, out var until) && now < until) return false;
        var cd = _zoneCalloutMinCooldown + (float)_rng.NextDouble() * (_zoneCalloutMaxCooldown - _zoneCalloutMinCooldown);
        _zoneCalloutCooldown[zoneKey] = now + cd;
        return true;
    }

    /// Higher-level wrapper: schedules a zone-aware callout from `bot`, then rolls
    /// an echo/question/rebuke/trade response from a teammate based on persona Mood/Tab.
    private void ScheduleCalloutChat(CCSPlayerController bot, string lineText,
        string? zoneKey, bool teamOnly = true, bool isToxic = false, float responsePct = 0.35f)
    {
        if (!_calloutsEnabled) return;
        if (bot is null || !bot.IsValid || !bot.IsBot) return;
        if (zoneKey != null && !TryClaimZoneCall(zoneKey)) return;

        ScheduleBotChat(bot, "",
            (_, __) => lineText,
            teamOnly: teamOnly, isToxic: isToxic);

        // Echo/question/rebuke chain — pick a teammate and roll persona response
        if (!Roll(responsePct)) return;
        var responder = PickWeightedTalker(bot.Team);
        if (responder == null || responder == bot) return;
        if (!_botPersonas.TryGetValue(responder.Slot, out var rper)) return;
        if (rper.Tab == Talkativeness.Silent && !Roll(0.10f)) return;

        // Decide response type by Mood + Tab
        // Hostile + Tab>=Medium → rebuke (toxic); Friendly + Tab>=Low → echo/trade;
        // Neutral or low-tab → question or echo
        Func<string> pickerF;
        bool toxic = false;
        var mapName = Server.MapName ?? "";
        var roll = _rng.NextDouble();
        if (rper.Mood == Friendliness.Hostile && rper.Tab >= Talkativeness.Medium && roll < 0.55)
        {
            pickerF = () => ChatStyles.Callout_Rebuke[_rng.Next(ChatStyles.Callout_Rebuke.Length)];
            toxic = true;
        }
        else if (rper.Mood == Friendliness.Friendly && rper.Tab >= Talkativeness.Low && roll < 0.55)
        {
            pickerF = () => roll < 0.30
                ? ChatStyles.PickCallout(ChatStyles.Callout_Trade, mapName, "", _rng)
                : ChatStyles.Callout_Echo[_rng.Next(ChatStyles.Callout_Echo.Length)];
        }
        else if (roll < 0.50)
        {
            pickerF = () => ChatStyles.PickCalloutFixed(ChatStyles.Callout_Question, zoneKey ?? "", "", _rng);
        }
        else
        {
            pickerF = () => ChatStyles.Callout_Echo[_rng.Next(ChatStyles.Callout_Echo.Length)];
        }

        var delay = 0.5f + (float)_rng.NextDouble() * 1.6f;
        var responderRef = responder;
        var line = pickerF();
        AddTimer(delay, () =>
        {
            if (!responderRef.IsValid) return;
            ScheduleBotChat(responderRef, "",
                (_, __) => line,
                teamOnly: teamOnly, isToxic: toxic);
        });
    }

    /// Find the closest world zone for a position. Stub: just picks from map pool —
    /// we don't have site centroids. For PLANTED, the engine sets m_iBombSite which
    /// would be cleaner, but is brittle to read; the random pick at A/B is fine for chat.
    private string PickZoneForPosOrRandom(CounterStrikeSharp.API.Modules.Utils.Vector? _, string mapName)
        => ChatStyles.PickZoneFor(mapName, _rng);

    /// Pick a teammate (alive) closest to a world position. Used to attribute
    /// callouts ("smoke X") to the bot who'd most plausibly see it.
    private CCSPlayerController? ClosestTeammateBot(CsTeam team, CounterStrikeSharp.API.Modules.Utils.Vector? pos, float maxRadius = 4000f)
    {
        if (pos == null) return null;
        CCSPlayerController? best = null;
        float bestD = maxRadius * maxRadius;
        foreach (var b in Utilities.GetPlayers())
        {
            if (!b.IsValid || !b.IsBot || b.Team != team) continue;
            var bp = b.PlayerPawn?.Value;
            if (bp?.IsValid != true || bp.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            var bpos = bp.AbsOrigin; if (bpos == null) continue;
            var dx = bpos.X - pos.X; var dy = bpos.Y - pos.Y; var dz = bpos.Z - pos.Z;
            var d = dx * dx + dy * dy + dz * dz;
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    /// IGL-preferred talker — first alive IGL on team if any (with prob boost), else weighted talker.
    private CCSPlayerController? PickIGLOrTalker(CsTeam team)
    {
        var bots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.Team == team).ToList();
        if (bots.Count == 0) return null;
        var igls = bots.Where(b =>
            _botPersonas.TryGetValue(b.Slot, out var per) && per.Archetype == BotArchetype.IGL).ToList();
        if (igls.Count > 0 && _rng.NextDouble() < 0.7) return igls[_rng.Next(igls.Count)];
        return PickWeightedTalker(team);
    }

    private HookResult OnSmokeDetonate(EventSmokegrenadeDetonate e, GameEventInfo info)
    {
        if (!_calloutsEnabled || !_toxicChat) return HookResult.Continue;
        var thrower = e.Userid;
        if (thrower is null || !thrower.IsValid) return HookResult.Continue;
        // Only call if smoke from ENEMY — teammate smokes shouldn't trigger surprise callout
        var pos = new CounterStrikeSharp.API.Modules.Utils.Vector(e.X, e.Y, e.Z);
        // Pick closest opposing-team bot to the smoke
        var opp = thrower.Team == CsTeam.Terrorist ? CsTeam.CounterTerrorist : CsTeam.Terrorist;
        var caller = ClosestTeammateBot(opp, pos) ?? PickWeightedTalker(opp);
        if (caller == null) return HookResult.Continue;
        if (!Roll(0.55f, caller)) return HookResult.Continue;
        var zone = PickZoneForPosOrRandom(pos, Server.MapName ?? "");
        var line = ChatStyles.PickCalloutFixed(ChatStyles.Callout_Smoke, zone, "", _rng);
        var key = ChatStyles.ZoneKeyFor(Server.MapName ?? "", line);
        ScheduleCalloutChat(caller, line, key, teamOnly: true, isToxic: false, responsePct: 0.20f);
        return HookResult.Continue;
    }

    private HookResult OnInfernoStart(EventInfernoStartburn e, GameEventInfo info)
    {
        if (!_calloutsEnabled || !_toxicChat) return HookResult.Continue;
        var pos = new CounterStrikeSharp.API.Modules.Utils.Vector(e.X, e.Y, e.Z);
        // We don't know thrower team from this event reliably; pick whichever team has a bot near it
        CCSPlayerController? caller = null;
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var c = ClosestTeammateBot(team, pos, 1200f);
            if (c != null) { caller = c; break; }
        }
        if (caller == null) return HookResult.Continue;
        if (!Roll(0.45f, caller)) return HookResult.Continue;
        var zone = PickZoneForPosOrRandom(pos, Server.MapName ?? "");
        var line = ChatStyles.PickCalloutFixed(ChatStyles.Callout_Molly, zone, "", _rng);
        var key = ChatStyles.ZoneKeyFor(Server.MapName ?? "", line);
        ScheduleCalloutChat(caller, line, key, teamOnly: true, isToxic: false, responsePct: 0.18f);
        return HookResult.Continue;
    }

    private HookResult OnFlashDetonate(EventFlashbangDetonate e, GameEventInfo info)
    {
        // Used as a backstop — actual "flashed" callout fires from EventPlayerBlind
        return HookResult.Continue;
    }

    private HookResult OnPlayerBlind(EventPlayerBlind e, GameEventInfo info)
    {
        if (!_calloutsEnabled || !_toxicChat) return HookResult.Continue;
        var p = e.Userid;
        if (p is null || !p.IsValid || !p.IsBot) return HookResult.Continue;
        // BlindDuration field name may vary; use heuristic — only call if we know they got fully flashed
        // (CS2 BlindDuration is on EventPlayerBlind as 'BlindDuration' float)
        float dur;
        try { dur = e.BlindDuration; } catch { dur = 1.2f; }
        if (dur < 1.2f) return HookResult.Continue;
        if (!Roll(0.55f, p)) return HookResult.Continue;
        var line = ChatStyles.Callout_Flashed[_rng.Next(ChatStyles.Callout_Flashed.Length)];
        // Don't dedup-by-zone for "flashed" (it's player-state, not zone)
        ScheduleCalloutChat(p, line, zoneKey: null, teamOnly: true, isToxic: false, responsePct: 0.10f);
        return HookResult.Continue;
    }

    private HookResult OnRoundTimeWarning(EventRoundTimeWarning e, GameEventInfo info)
    {
        if (!_calloutsEnabled || !_toxicChat) return HookResult.Continue;
        if (_timeCalloutDoneThisRound) return HookResult.Continue;
        _timeCalloutDoneThisRound = true;
        // Pick whichever team is on offense — easier to approximate as T side; alternate fallback CT
        foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
        {
            var caller = PickIGLOrTalker(team);
            if (caller == null) continue;
            if (!Roll(0.65f, caller)) continue;
            var line = ChatStyles.Callout_TimeLow[_rng.Next(ChatStyles.Callout_TimeLow.Length)];
            ScheduleCalloutChat(caller, line, zoneKey: null, teamOnly: true, isToxic: false, responsePct: 0.10f);
            break;     // one team only
        }
        return HookResult.Continue;
    }

    private HookResult OnBombBeginDefuse(EventBombBegindefuse e, GameEventInfo info)
    {
        if (!_calloutsEnabled || !_toxicChat) return HookResult.Continue;
        var defuser = e.Userid;
        if (defuser is { IsValid: true, IsBot: true } && Roll(0.55f, defuser))
        {
            var line = ChatStyles.Callout_DefuseCommit[_rng.Next(ChatStyles.Callout_DefuseCommit.Length)];
            ScheduleCalloutChat(defuser, line, zoneKey: null, teamOnly: true, isToxic: false, responsePct: 0.20f);
        }
        return HookResult.Continue;
    }
}
