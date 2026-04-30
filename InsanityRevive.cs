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
public class InsanityRevive : BasePlugin
{
    public override string ModuleName => "INSANITY REVIVE";
    public override string ModuleVersion => "0.7.0";
    public override string ModuleAuthor => "frad70 + Claude";
    public override string ModuleDescription => "Predictive aim + social bots: AFK, vote-kick, ragequit, GG/friendly chatter, freeze-period banter, body-block bumps with FF consequences.";

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

    // -------- global tunables --------
    public string CurrentPreset { get; private set; } = "Insane";
    private bool _toxicChat = true;
    private bool _hearingEnabled = true;
    private bool _strafingEnabled = false;     // disabled in 0.6.6 — caused drift bug
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
    private float _friendlyChatBoost    = 1.6f;    // talkativeness multiplier for friendly bots on nice events

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

    public override void Load(bool hotReload)
    {
        Logger.LogInformation("INSANITY REVIVE v{v} loading…", ModuleVersion);

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

        // 33 Hz tick — drives aim override + strafe + typing-freeze + look-force
        AddTimer(0.030f, OnTick, TimerFlags.REPEAT);

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
            _botPersonas[p.Slot] = ChatStyles.RandomPersona(_rng, p.PlayerName);
        return HookResult.Continue;
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
        _aim.Forget(p.Slot);
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
                // Vote-kick chance (high cooldown still applies)
                if (Server.CurrentTime - _lastVoteCallTime > 60f && Roll(0.40f))
                {
                    var initiator = survivors[_rng.Next(survivors.Count)];
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
                per.LowCallCount += 1;
                var prefix = per.LowCallCount > 1 ? $"{per.LowCallCount}x " : "";
                var zone = ChatStyles.PickZoneFor(Server.MapName ?? "", _rng);
                ScheduleBotChat(attacker, "",
                    (_, __) => $"{prefix}low {zone}",
                    teamOnly: true, isToxic: false);
            });
        }

        if (attacker.Team != victim.Team) return HookResult.Continue;

        var key = (v: victim.Slot, a: attacker.Slot);
        _ffDamageRound.TryGetValue(key, out var sum);
        sum += e.DmgHealth;
        _ffDamageRound[key] = sum;

        // 0.7: Track damage by BOT to ENEMY for "low {zone}" callouts.
        // (this branch only handles same-team FF; the enemy path is below in OnPlayerHurt-Other handler)

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
        _firstBloodDoneThisRound = false;
        _inFreezePeriod = true;
        _grudgeTarget.Clear();
        _matchRoundCount += 1;

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
        if (!_toxicChat) return HookResult.Continue;
        var winner = (CsTeam)e.Winner;
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

        // Random rage-quit (very rare, capped by global cooldown)
        if (Server.CurrentTime - _lastRageQuitTime > 180f && Roll(_ragequitPctPerRound))
        {
            var allBots = Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot).ToList();
            if (allBots.Count > 0)
            {
                var quitter = allBots[_rng.Next(allBots.Count)];
                _lastRageQuitTime = Server.CurrentTime;
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
        return HookResult.Continue;
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
        if (p is { IsValid: true, IsBot: true })
        {
            if (_toxicChat && Roll(_plantChatPct, p))
                ScheduleBotChat(p, "", ChatStyles.PickPlantLine, teamOnly: true, isToxic: false);
            RadioFromBot(p, Radio_InPosition, baseChance: 0.6f);
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

            // Forced attack window (FF revenge): set Attack bit on controller buttons.
            // Engine reads CCSPlayerController.Buttons each tick and feeds it to weapon
            // logic — this is what makes a bot actually pull the trigger.
            if (_attackUntil.TryGetValue(bot.Slot, out var au2) && now < au2)
            {
                // (Buttons setter is read-only in CSSharp 1.0.367; rely on +attack console + ms.Buttons schema)
                try
                {
                    var ms2 = pawn.MovementServices;
                    if (ms2 != null) ms2.Buttons.ButtonStates[0] |= 1UL;
                } catch { }
            }

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
        var tCaller = PickWeightedTalker(CsTeam.Terrorist);
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
        return _rng.NextDouble() < pct * per.ChatProbabilityFactor;
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
}
