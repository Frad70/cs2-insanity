# Iteration 7 — independent modules added (uncommitted)

While the v0.10 movement-realism agent was finishing, I built five
independent modules so the next iteration has plenty of integration
material. Build is green with all of them in place.

## New files (in working tree, not committed yet)

| File                  | Lines | Purpose                                                           |
|-----------------------|-------|-------------------------------------------------------------------|
| `DecisionEngine.cs`   | ~190  | Per-bot tilt / confidence / streaks / chad-mode / hard-tilt flag  |
| `EconomyModel.cs`     | ~135  | Per-team buy-plan classifier, anti-eco boldness                   |
| `MapKnowledge.cs`     | ~130  | Per-map zone names, awp-favoring flag, rotation-time estimates    |
| `ClutchBehavior.cs`   | ~145  | 1vN detection, decision-slowdown + chat-suppression multipliers   |
| `BuyPreferences.cs`   | ~175  | Per-bot primary/secondary preference, utility prefs, force tendency|

All sit in same `InsanityRevive` namespace as separate types (NOT
`partial class InsanityRevive` — that pattern is reserved for v0.10's
MovementRealism extension). Each is read-mostly; no event handlers
registered. Wiring into the host plugin is a future iteration.

## Integration plan (next time)

When v0.10 lands cleanly, do v0.11.0 wiring:

1. Add private fields to `InsanityRevive`:
   ```csharp
   private readonly DecisionEngine    _decisions = new();
   private readonly ClutchBehavior    _clutch    = new();
   private readonly EconomyModel      _econ      = new();
   private readonly BuyPreferences    _buyPrefs  = new();
   ```

2. **DecisionEngine wiring** (highest priority — emergent realism):
   - `OnPlayerDeath`: call `OnBotKill` / `OnBotDeath` with FF/headshot/clutch flags
   - `OnPlayerHurt`: call `OnBotTookFF` for cumulative FF
   - `OnRoundEnd`: call `OnRoundWonForBot` / `OnRoundLostForBot`
   - In `ScheduleBotChat`: multiply chat probability by `ChatBoost(slot, persona)`
   - When deciding to ragequit/vote-kick: gate on `IsHardTilted(slot)` to avoid
     calm bots randomly blowing up

3. **ClutchBehavior wiring** (next priority):
   - Tick: call `_clutch.Refresh()` once per tick (not per bot)
   - In aim path: read `DecisionSlowdown(slot)` as a snap-multiplier
   - In chat path: multiply chat probability by `ChatSuppression(slot)`
   - When clutch ends + bot wins: call `Resolve(slot, won=true)` and trigger
     "clutch line" via existing chat path

4. **EconomyModel wiring** (after the above):
   - `OnRoundFreezeEnd`: `SnapshotForRound(team)` for both teams
   - `OnRoundEnd`: `OnRoundEnd(winner)` to bump streaks
   - In bot-tick: query `BotShouldEco(bot)` to suppress aggressive movement

5. **BuyPreferences wiring** (after Economy):
   - `OnRoundFreezeStart`: roll prefs if not set, then issue `bot_buy <weapon>`
     console commands per bot. (CounterStrikeSharp `bot_buy` may not exist;
     fallback: just give weapon via schema setter as last-resort hack.)

6. **MapKnowledge wiring** (subtlest):
   - In MovementRealism's walk-vs-run logic: pass `MapKnowledge.GetCurrent(map)`
     and engage shift-walk only when bot is in `LongRangeZones` (use a fuzzy
     name-match against last-known nav area — NavArea string isn't easily
     accessible, so this part needs more thought; might end up as no-op)
   - In Awper archetypes: pass `FavorsAwp(map)` to bias initial buy and
     positioning

## Status

- v0.10 still in progress (movement agent owns InsanityRevive.cs +
  AimController.cs + MovementRealism.cs).
- v0.9 committed and live: zone-aware callouts.
- These 5 modules NOT committed; will commit alongside v0.11.0 wiring.
