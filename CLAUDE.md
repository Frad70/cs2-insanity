# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

INSANITY REVIVE is a CounterStrikeSharp (CSSharp) plugin for CS2 whose goal is to make bots **indistinguishable from real MM players** — chat, movement, aim, decisions, target selection, cooperation. Single C# project, .NET 8, namespace `InsanityRevive`, package `CounterStrikeSharp.API` 1.0.367, `MinimumApiVersion(304)`.

## Build & deploy

```bash
dotnet build -c Release
```

There is no test project. Correctness is verified by:
1. **Build must be 0 errors** before any push (warnings are tolerated; some are pre-existing).
2. The compiled DLL/PDB get copied into the live server's plugin directory:
   `~/cs2-server/game/csgo/addons/counterstrikesharp/plugins/InsanityRevive/InsanityRevive.{dll,pdb}`.
3. CSSharp picks up the new DLL on the next in-game `css_plugins reload InsanityRevive` (rcon hot-reload is unreliable due to a known Steam pressure-vessel issue — don't rely on it from scripts).
4. Behavior is observed post-hoc via the structured behavior log written to `/home/frad70/cs2-server/insanity-revive.log` (see `LogBehavior` / `FlushBehaviorLog` in `InsanityRevive.cs`).

In-game commands (registered via `[ConsoleCommand]`):
- `css_bots` — open the radio/settings ChatMenu (admin only, `@css/root`).
- `css_bots_preset <casual|normal|hard|insane|aimbot>` — swap difficulty preset; **triggers a `changelevel` reload of the current map** to apply the bot profile.
- `css_buy_override <0|1>` — toggle persona-driven `buy weapon_*` per bot (default off — engine AI conflicts).

## Versioning & git workflow

- `ModuleVersion` (string in `InsanityRevive.cs`) is the source of truth. Bump it for every feature.
- Commit messages follow `vX.Y.Z — <one-line summary>` (see `git log`). Each numbered version is its own commit.
- Per the project charter (`journal/000_charter.md`), push to `origin` at every numbered bump.
- `journal/` is committed and is the durable lossy-compaction-resistant log; **read `journal/` before doing anything substantial** (especially after `/compact`). Journal entries use a numeric prefix (`NNN_*.md`) — pick the next free number when adding one.
- `journal/SESSION_START.txt` and `journal/RETURN_PHRASE.txt` describe an autonomous-session protocol; honor them if present.

## Architecture

The plugin is one partial class spread across two files plus several free-standing service classes:

```
InsanityRevive.cs       — partial class InsanityRevive : BasePlugin (the host)
MovementRealism.cs      — partial class InsanityRevive (movement-only methods, split to reduce merge conflicts)

AimController.cs        — class AimController              (smooth predictive aim, per-bot AimProfile)
ChatStyles.cs           — class ChatStyles + BotPersona    (chat pools, persona, callout zone keys)
DecisionEngine.cs       — class DecisionEngine             (per-bot tilt/confidence/streaks/chad-mode)
ClutchBehavior.cs       — class ClutchBehavior             (1vN slowdown + chat suppression)
EconomyModel.cs         — class EconomyModel               (per-team buy plans)
BuyPreferences.cs       — class BuyPreferences             (per-bot weapon prefs)
MapKnowledge.cs         — class MapKnowledge               (per-map zone names, awp flag, rotation times)
```

The host plugin owns instances of every service:
```csharp
private readonly AimController    _aim       = new();
private readonly DecisionEngine   _decisions = new();
private readonly ClutchBehavior   _clutch    = new();
private readonly EconomyModel     _econ      = new();
private readonly BuyPreferences   _buyPrefs  = new();
```

Service classes are read-mostly; they don't register events themselves — the host plugin is the only thing wired into the CSSharp event bus.

### The 33 Hz tick

`AddTimer(0.030f, OnTick, TimerFlags.REPEAT)` in `Load()` is the heartbeat. It drives:
- `AimController.Tick()` — predictive aim goal lerp.
- Per-bot button pulses via `ApplyButtonPulses` (`IN_ATTACK | IN_SPEED | IN_DUCK | IN_JUMP`).
- Movement realism rolls (`MovementRealismTick`).
- `_forceLook` eye-angle override (typing freeze, pre-aim).
- Behavior log flush every ~2s.
- Throttled subsystems use their own gates (`_lastClutchRefreshAt` etc) — clutch refresh runs at 5 Hz, not 33 Hz.

**Hot path discipline:** anything in `OnTick` runs ~33×/s × N bots. Avoid allocations and LINQ-heavy work where possible; the existing code uses cached `Dictionary<int, …>` per slot for cheap lookup.

### State convention: per-bot dictionaries keyed on `slot`

Almost all per-bot state lives in `Dictionary<int, T>` keyed on `CCSPlayerController.Slot`. Patterns:
- `_*Until` dictionaries hold a "valid until `Server.CurrentTime`" timestamp; reads compare `now < until`. Used for chat typing freeze, button pulses, aim reaction-time gates, etc.
- `_*Cooldown` dictionaries are the same idea but for "don't fire again before this time".
- All per-bot state must be cleared in `Unload`. Round-scoped state must be cleared in `OnRoundStart`. Add new state to *both* clear paths.

### Movement: buttons-only, never velocity

The v0.6.6 drift bug came from per-tick velocity writes. **Do not** write to `pawn.AbsVelocity` or any velocity field to influence movement. Only:
1. OR button bits onto `pawn.MovementServices.Buttons.ButtonStates[0]` (use the `IN_*` constants in `InsanityRevive.cs`), via the `_*Until` window pattern.
2. Set eye angles via `_forceLook[slot] = (yaw, pitch)` and `_lookUntil[slot]`, which `OnTick` enforces.

`MovementRealism.cs` is the canonical example. The header comment in that file restates the rule.

### Bot speech path: `ScheduleBotChat`

All bot chat goes through `ScheduleBotChat(bot, subject, picker, teamOnly, isToxic, extraDelay)` in `InsanityRevive.cs`. It:
- lazy-creates a `BotPersona` (and `AimProfile`) if missing,
- enforces a per-bot chat cooldown (`_chatCooldownSec`) and the typing-freeze window,
- calls `picker(persona, refSubject)` to produce the line (pickers come from `ChatStyles`),
- runs it through `ChatStyles.MaybeMangle` for typo/style mangle,
- with `_wrongChatEnabled`, occasionally flips `team_only → all`,
- delays sending by realistic typing time (`ChatStyles.ComputeTypingTime` based on `persona.Wpm`),
- if `isToxic`, registers a `_rebukeChainPct` chance for a teammate rebuke.

Don't bypass it — `Server.PrintToChatAll` for bots will look fake (no typing freeze, no persona mangle, no cooldown). Higher-level callout flows go through `ScheduleCalloutChat` which adds `TryClaimZoneCall` dedup + echo/question/rebuke chain.

### Persona model

`BotPersona` (in `ChatStyles.cs`) is the per-bot personality blob: `Style`, `Tab` (talkativeness), `Mood` (friendliness), `Skill`, `Wpm`, `Archetype`, plus 10 aim fields (snap/bias/refresh/reaction/overshoot×2/noise/micro-adjust/spray/flick). When you create or mutate a persona, call `PushAimProfile(slot, persona)` to push the aim fields into `AimController` — `_aim.SetProfile(slot, …)` is what makes per-bot aim differentiation actually take effect.

### Difficulty presets

`_presets` table in `InsanityRevive.cs` maps a preset name → `(botprofile.vpk to copy, bot_difficulty, bot_aim_*)`. `ApplyPreset`:
1. Copies `~/cs2-server/game/csgo/overrides/<profile>/botprofile.vpk` over `botprofile.vpk` (engine reads it on map load).
2. Tightens `_aim.{SnapPerTick, MaxBiasDeg, GoalRefreshSec}` etc.
3. `ReapplyConvarsOnly` for live-tunable convars.
4. Schedules a `changelevel` to the current map so botprofile.vpk is re-read.

Adding a new preset requires both a row in `_presets` and a corresponding `botprofile.vpk` on disk under `overrides/<Name>/`.

### Event handlers

Registered in `Load()`. Common pattern: each handler returns `HookResult.Continue`, mutates plugin state, and dispatches to the relevant service (e.g. `_decisions.OnBotKill(...)`, `_clutch.Refresh()`). When adding a new event:
1. `RegisterEventHandler<EventX>(OnX);` in `Load()`.
2. Implement `private HookResult OnX(EventX e, GameEventInfo info) { … return HookResult.Continue; }`.
3. If you cache per-bot state, clear it in `Unload` and `OnRoundStart` as appropriate.

## Conventions worth following

- **Don't bypass dedup gates.** Zone callouts (`TryClaimZoneCall`), low-HP (`_lowEnemyCallCooldown`), per-bot chat (`_lastChatTime`/`_chatCooldownSec`) — every chat trigger goes through these so the team doesn't sound like a spam bot.
- **Roll, don't always do.** Use the `Roll(pct)` / `Roll(pct, bot)` helpers — bot-aware overload multiplies by the bot's `Talkativeness` factor. Tune via the existing `_*Pct` fields rather than hardcoding numbers at call sites.
- **Wrap CSSharp calls in `try/catch`** around `MovementServices`, `ExecuteClientCommandFromServer`, and similar engine interop. The plugin must survive a single bot disappearing mid-tick without throwing into the event loop.
- **Partial class split for parallel work.** When multiple agents/branches will edit `InsanityRevive.cs` at once, peel a feature into its own `partial class InsanityRevive` file (see `MovementRealism.cs`). Smaller surface in the main file → fewer multi-edit collisions.
- **Use `Server.CurrentTime`** (float seconds, monotonic) for all in-game timing — not `DateTime.Now` (only used for human-readable log lines).
