# Optimization proposal — hot-path GC + iteration cleanup

Author: read-only audit agent, 2026-04-30. Not yet pushed.
Pickup: any push-er — see "Suggested split" below for one-task-per-PR slices.

## TL;DR

`OnTick` runs at 33Hz. Every tick today it:

- calls `Utilities.GetPlayers()` **at least 5×** (Aim.Tick + 3 OnTick foreach +
  per-bot scan inside spec-mock loop), each call allocates a fresh `List<>`.
- iterates the player list **3 separate times** (pre-clutch / spec-mock /
  main bot loop) when one fused pass would do.
- inside the spec-mock branch it calls `Utilities.GetPlayers().Where(...).ToList()`
  **per dead bot per tick** — i.e. an O(N²) allocation pattern any time a
  bot is dead but inside the 10–35s mock window.
- does an O(N²) FOV target scan (`AimController.Tick`) plus an O(N²)
  body-bump scan (`CheckBodyBump`) — both with their own `GetPlayers()`
  re-enumeration.
- formats a `$"…"` interpolated string for every `LogBehavior` call even
  when the queue is empty/discarded (always-on cost).

At 10v10 that's ~7 GetPlayers calls × 33 ticks/s ≈ **230 list allocations/s**
plus ~6.6k FOV-scan iterations/s plus ~6.6k body-bump iterations/s, all
inside a CSSharp interop layer where each `pawn.PlayerPawn?.Value` /
`pawn.AbsOrigin` is a marshalled call. That's the biggest per-frame cost
in the plugin and the easiest win.

None of this is wrong-behavior — it's all GC pressure / interop overhead /
duplicated work. The plugin already shipped to v0.21 so we should
**not** change observable behavior. Optimization only.

## Concrete findings, ordered by ROI

### 1. Single fused pass over alive bots (HIGH)

**Where:** `InsanityRevive.cs:1969 OnTick()`

Currently three separate `foreach (var p in Utilities.GetPlayers())` loops
(lines 1991, 2031, 2070), each filtering for "alive bot". Plus
`AimController.Tick()` does its own `GetPlayers().Where(...).ToList()`
**at line 85**, called from `OnTick` line 1973.

Fix: build the alive-player list once at the top of `OnTick` and pass it to:
- `_aim.Tick(IReadOnlyList<CCSPlayerController> alive)` (new overload),
- the pre-clutch loop,
- the spec-mock loop,
- the main per-bot tick loop,
- `CheckBodyBump(bot, pawn, now, alive)`,
- `MovementRealismTick` (if it ever needs neighbor scan).

Single allocation per tick instead of 5–7.

### 2. Spec-mock inner allocation (HIGH)

**Where:** `InsanityRevive.cs:2051`

```csharp
var alive = Utilities.GetPlayers()
    .Where(t => t.IsValid && t != p && t.Team == p.Team
        && t.PlayerPawn?.Value?.LifeState == (byte)LifeState_t.LIFE_ALIVE)
    .ToList();
```

This runs **once per dead bot per tick** while inside the 10–35s mock window.
Allocates a list and a captured-closure delegate every time.

Fix: After (1) is done, the alive list is already in scope. Iterate it
inline; no LINQ, no allocation. We only need an alive teammate — pick by
random index or reservoir sample.

### 3. AimController.Tick: reuse alive list, drop double dict lookup, slot-keyed lookup (HIGH)

**Where:** `AimController.cs:82`

Three small wins here:

a. **Don't enumerate players twice.** Take the alive list as a parameter
   (see #1), drop the `.Where(...).ToList()`.

b. **Forced-target lookup is O(N) per bot.** Lines 110–112:
   ```csharp
   foreach (var p in alive)
       if (p.Slot == ft.targetSlot) { target = p; break; }
   ```
   Replace with `Utilities.GetPlayerFromSlot(ft.targetSlot)` (O(1)) or
   maintain a `Dictionary<int, CCSPlayerController> bySlot` that's built
   once in #1.

c. **Double dict access.** Lines 108/113:
   ```csharp
   if (ForcedTarget.TryGetValue(slot, out var ft) && now <= ft.untilTime) { ... }
   else if (ForcedTarget.ContainsKey(slot)) ForcedTarget.Remove(slot);
   ```
   Collapse to a single `TryGetValue` + remove-on-expiry inside the same
   branch.

### 4. Replace per-slot `Dictionary<int,T>` with `T[64]` arrays (MEDIUM)

**Where:** `InsanityRevive.cs:23-44, 53-90`

There are ~30 `Dictionary<int, …>` keyed by `bot.Slot`. CS2 caps slot at
64 (MaxPlayers), and slots are dense small ints. Each tick we hash-lookup
~20 of these dicts × 20 bots = ~400 dict ops/tick = ~13k/s. Hash is cheap
but it's still wasteful vs a flat array.

Fix template:
```csharp
private readonly float[] _typingUntil = new float[64];   // 0 = unset / past
private readonly float[] _combatUntil = new float[64];
…
```

Sentinel `0f` works because `Server.CurrentTime` is monotonic and large.
Same for the `bool[]` round-flag dicts (`_crouchJumpUsedThisRound`,
`_preClutchAnnouncedThisRound`, `_specMockedThisRound`).

This is invasive but the refactor is mechanical and the touch surface is
isolated to the field declarations + their getter sites. Recommend doing
it in **one focused PR**, not bundled with #1–#3.

### 5. CheckBodyBump O(N²) (MEDIUM)

**Where:** `InsanityRevive.cs:2128`

Called for every alive bot. Inside it does `foreach (mate in
Utilities.GetPlayers())` for the 80u-radius blocker scan. So per tick:
N alive bots × N players = N² teammate dot-product checks. At 10v10 that's
200 marshalled `PlayerPawn` reads / `AbsOrigin` reads per tick = 6,600/s.

Fix: Most of those iterations are wasted — bots that haven't been "stuck
≥ `_bodyBumpRequireSec`" don't need the scan at all. Move the
"stillness check" gate to BEFORE the loop (it already is, mostly), and
when we DO scan, reuse the alive list from #1 instead of calling
`GetPlayers()` again.

Stretch: bucket bots into a coarse 256u-cell grid built once per tick;
each bot only checks neighbors in its + 8 surrounding cells. Probably
overkill for 20 players.

### 6. ClutchBehavior.Refresh allocates dicts/lists (LOW)

**Where:** `ClutchBehavior.cs:30`

Allocates a `Dictionary<CsTeam, List<…>>` and two `List<>`s every
refresh. Already throttled to 5Hz so it's only ~5 allocs/sec, but it's
trivially fixable: keep two pre-allocated `List<CCSPlayerController>`
fields (`_tAlive`, `_ctAlive`), `Clear()` them at start of `Refresh()`,
fill, dispatch. Zero alloc.

### 7. `LogBehavior` always pays interpolation cost (LOW)

**Where:** `InsanityRevive.cs:535`

```csharp
_behaviorLog.Enqueue($"{DateTime.Now:HH:mm:ss} [{kind}] {detail}");
```

The `$"…"` allocates a string + the caller already allocated `detail` via
its own `$"…"`. Minor but called from hot-ish event paths.

Two options:
- Cheap: only build the string if behavior-log is enabled (add a
  `_behaviorLogEnabled` bool flag, check first).
- Better: make `LogBehavior` take `(string kind, FormattableString detail)`
  and build the string lazily inside.

### 8. `FlushBehaviorLog` opens StreamWriter every 2s (LOW)

**Where:** `InsanityRevive.cs:543`

Opens / closes the file every flush. On Linux that's an `open(2)` +
`close(2)` syscall pair every 2s. Fine, but if we keep an open
`StreamWriter` field with `AutoFlush = false` and call `.Flush()` from
the tick path, we save the open/close. (Match `Unload()` path to dispose
cleanly.)

**Risk:** if the plugin crashes the tail of the buffer is lost. Probably
worth it; keep a `Flush()` after each enqueue burst.

### 9. `ConcurrentQueue` is overkill for behavior log (LOW)

**Where:** `InsanityRevive.cs:141`

`_behaviorLog` is `ConcurrentQueue<string>`. It's only ever
enqueued/dequeued from the CSSharp main tick. A plain
`Queue<string>` is faster. Leave only if there's a future scenario where
a callback runs on a different thread (unlikely in CSSharp).

### 10. `pawn.PlayerPawn?.Value` and `AbsOrigin` re-reads per bot (LOW-MED)

Each `PlayerPawn?.Value`, `AbsOrigin`, `AbsVelocity`, `EyeAngles` access
goes through a CSSharp marshalling layer. Today the OnTick body reads:

- `pawn.AbsVelocity` at line 2080 (AFK check)
- `pawn.AbsVelocity` again inside `CheckBodyBump` at line 2137
- `pawn.AbsOrigin` and `pawn.AbsVelocity` again inside `MovementRealismTick`
- `pawn.AbsOrigin` again inside `ApplyButtonPulses` (well, no — it just
  reads `MovementServices`).

Read each once per tick at the top, pass into helpers. Same pattern Aim
already uses with `var origin = pawn.AbsOrigin` + reuse.

## Suggested split (one PR per item, in priority order)

1. **PR-A: Single alive-list fan-out (#1, #2, #3a).** Add an
   `IReadOnlyList<CCSPlayerController>` parameter to `_aim.Tick` and the
   three OnTick loops. Build it once. Eliminates ~5 list allocs/tick.
2. **PR-B: AimController internals (#3b, #3c).** Slot-keyed lookup, drop
   double dict access. Standalone, no API change.
3. **PR-C: ClutchBehavior dealloc (#6).** Trivial, isolated.
4. **PR-D: BodyBump uses fanned-out list (#5).** Depends on PR-A.
5. **PR-E: BehaviorLog tightening (#7, #8, #9).** Standalone.
6. **PR-F: T[64] array migration (#4).** Big mechanical refactor — keep
   isolated. Land last.
7. **PR-G (stretch): Per-tick pawn snapshot (#10).** Useful only after
   the others land — measurable then.

## What I deliberately did NOT propose

- **Adding caching to `ChatStyles.Pick*`** — those allocate strings via
  `array[rng.Next()]` returning a const string from the static array.
  No allocation, no work needed.
- **Touching ChatStyles.cs structure** — 1.6k lines of static string
  arrays. Build cost is one-time at type init. Leave it.
- **Object pooling for `Goal`** in AimController — the pool is keyed per
  bot and reused across ticks (only allocated on fresh-target). Already
  amortised.
- **Replacing `Roll()`** — `_rng.NextDouble()` is fine; LCG would change
  the persona-distribution snapshot and break behavioral parity with
  shipped builds.

## Validation

Each PR should:

1. `dotnet build -c Release` — 0 errors, no new warnings.
2. Manual sanity: `css_plugins reload InsanityRevive` doesn't crash; bots
   still join, chat, shoot, walk. Behavior log keeps writing.
3. If we get GC counters from the server, `gcServer = false` (default in
   CSSharp) means Gen0 collections drop visibly with PR-A alone. No
   automated bench available — eyeball via top during a bot match.

## Coordination notes

- v0.10 movement realism owns `MovementRealism.cs` + `partial class`
  surface in `InsanityRevive.cs`. PR-A touches OnTick body — coordinate
  via `git pull --rebase origin main` per `journal/011`.
- The behavior-log path (`/home/frad70/cs2-server/insanity-revive.log`)
  is the user's machine; don't relocate it.
- All field renames in PR-F MUST keep public API stable — these
  dictionaries are `private readonly`, so no external consumer, but
  double-check nothing in `BuyPreferences` / `DecisionEngine` /
  `EconomyModel` reaches into the host's state directly.

## Quick wins (sub-30-min PRs, if anyone wants to warm up)

- #6 ClutchBehavior pre-allocated lists — ~10 LOC.
- #9 ConcurrentQueue → Queue — ~2 LOC + a `lock` if paranoid.
- #3c double-dict-access in AimController — ~3 LOC.

Pick one of those if you're doing a small drive-by; pick PR-A if you
want the big tick-cost win.
