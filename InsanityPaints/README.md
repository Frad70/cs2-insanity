# InsanityPaints

> **Early alpha — Phase 1.** Skins for weapons, knives, and gloves.
> Stickers, keychains, real-StatTrak counters, and bot-psychology-driven
> skin selection are deliberately *out of scope* for this revision —
> see the [Roadmap](#roadmap) section.

Third plugin in the `cs2-insanity` stack. Slaps custom paintkits, knife
swaps, and glove models onto:

- **Real players** with admin permissions, picking via in-chat menus —
  selections persist in a JSON file keyed by SteamID64.
- **InsanityRevive-managed bots** — loadout is derived deterministically
  from the bot's persona name, so `Andrey_K` carries the same AK across
  rounds, mapchanges, and full server restarts.
- **Not** `bot_add` engine bots. They stay vanilla so they're easy to tell
  apart in experiments. Toggle via `apply_to_revive_bots` if you want
  bots untouched too.

## How it talks to InsanityRevive

`InsanityPaints` is a **read-only** consumer of the shared mmap pool at
`/tmp/insanityrevive_fake_slots.bin`. It only reads `managed[120]` to
distinguish Revive bots from `bot_add` engine bots. No writes — the pool
is owned by `InsanityRevive`, and we never touch any byte of it.

If the pool file isn't there (Revive hasn't loaded yet), `FakeSlotsReader`
returns `IsManaged == false` for every slot and the plugin treats only
real humans. Each `EventPlayerSpawn` we opportunistically retry the
`TryOpen`, so once Revive comes up the bots start getting their skins.

## CS2 server-side guidelines flag

`core.json` must have:

```jsonc
{
  "FollowCS2ServerGuidelines": false
}
```

Without that, CSSharp blocks the `m_iItemDefinitionIndex` /
`m_nFallbackPaintKit` writes and skins silently do nothing. See the
top-level `cs2-insanity` README for the full warning.

## Chat commands

All admin-gated by the flag in `settings.json` (default `@css/root`).
Non-admins get a chat refusal so they know the command exists but isn't
open to them.

| Command   | Action                                                          |
| --------- | --------------------------------------------------------------- |
| `!ws`     | Open weapon picker → pick a weapon → pick a paintkit            |
| `!knife`  | Pick a knife defindex for the current team, then a paint        |
| `!gloves` | Pick a glove model + paint for the current team                 |

Changes are applied on the next spawn or weapon pickup (no `!rs` /
respawn dance). Picks save to
`csgo/addons/counterstrikesharp/configs/plugins/InsanityPaints/players.json`
immediately.

## Console commands

| Command                          | Purpose                                                                                  |
| -------------------------------- | ---------------------------------------------------------------------------------------- |
| `css_insanity_paints_reload`     | Hot-reload `settings.json`, the three catalogs, and `players.json`. No restart required. |

Server console can always call it; in-game callers need the admin flag.

## Configuration files

All live under
`csgo/addons/counterstrikesharp/configs/plugins/InsanityPaints/`. The
plugin creates `settings.json` and an empty `players.json` on first run;
the three catalogs are shipped with the build.

| File                  | Purpose                                                                                |
| --------------------- | -------------------------------------------------------------------------------------- |
| `settings.json`       | Plugin-wide flags: enable_weapons / _knives / _gloves, admin flag, log level, etc.     |
| `weapons_paints.json` | The pool of `(weapon_defindex, paint, name)` rows the menus and bot resolver pick from |
| `knives.json`         | List of knife defindexes that can be assigned to a team                                |
| `gloves.json`         | List of `(glove_defindex, paint, name)` glove models                                   |
| `players.json`        | Per-SteamID64 stored loadout (written by chat commands)                                |

### Catalog schemas

`weapons_paints.json` is a flat list:

```json
[
  { "weapon_defindex": 7, "paint": 1100, "name": "AK-47 | Vulcan" },
  { "weapon_defindex": 7, "paint": 309,  "name": "AK-47 | Redline" }
]
```

`knives.json` is one row per knife defindex:

```json
[
  { "defindex": 515, "weapon_name": "weapon_knife_butterfly", "name": "Butterfly Knife" }
]
```

`gloves.json` is one row per glove model + paint pairing:

```json
[
  { "defindex": 5032, "paint": 10037, "name": "Specialist Gloves | Field Agent" }
]
```

Extending any of them is just appending entries and either running
`css_insanity_paints_reload` or restarting the server.

## How a bot's loadout is decided

`BotLoadoutResolver` takes the bot's `persona.Name` (the same name that
Revive injects into the engine), produces a SHA-256 digest over its UTF-8
bytes, takes the first 8 bytes as a `ulong`, and uses modulo against the
relevant catalog length.

Why SHA-256 and not `string.GetHashCode()` — the latter is randomized
per-process in modern .NET for security, so the same bot would catch a
different skin on every server restart. The SHA-256 path is stable
across processes, .NET versions, and machines.

To break ties between the three axes (weapon, knife, gloves), a small
ASCII prefix is mixed in before hashing — `w7:Andrey_K`, `kT:Andrey_K`,
`gCT:Andrey_K`, etc. — so a bot can carry a different paint on every
weapon and still be deterministic.

## Apply path

The skin write itself is lifted (in spirit, not in copy) from
[Nereziel/cs2-WeaponPaints](https://github.com/Nereziel/cs2-WeaponPaints).
The minimal Phase-1 path:

1. `EventPlayerSpawn` → `Server.NextFrame` so `pawn.WeaponServices.MyWeapons`
   is populated.
2. `Listeners.OnEntitySpawned` for `weapon_*` entities → `Server.NextWorldUpdate`,
   so mid-round buys catch the right paint without a respawn.
3. Per weapon:
   - Knife: `AcceptInput("ChangeSubclass", newDefindex)`, set
     `EntityQuality = 3`, clear attribute lists.
   - Set `ItemID / ItemIDLow / ItemIDHigh` to a fresh per-apply value
     starting at `16384`.
   - Set `FallbackPaintKit / FallbackSeed / FallbackWear`.
   - Inject `set item texture prefab/seed/wear` into both attribute
     lists via the `CAttributeList_SetOrAddAttributeValueByName`
     gamedata signature. The `Fallback*` fields cover most older
     paintkits; the attribute injection covers everything else,
     including newer paintkits that silently no-op without it.
   - Optional StatTrak: set `EntityQuality = 9` and `FallbackStatTrak = N`
     (count doesn't tick — see Roadmap).
4. Gloves: `pawn.EconGloves.ItemDefinitionIndex = …`, clear attributes,
   inject paint/seed/wear via the same signature (`CEconItemView` has
   no `Fallback*` fields, so the attribute write is the *only* way to
   get a paint onto gloves), `lastinv` toggle on either side of a
   `NextFrame` to nudge the model refresh.

The single `CAttributeList_SetOrAddAttributeValueByName` signature
ships in `gamedata/InsanityPaints.json`. CSSharp resolves it lazily on
first apply — if it doesn't match the running CS2 build, the plugin
logs a warning and falls back to `Fallback*` only (basic skins still
work; gloves and some new paintkits won't). Stickers and keychains
(Phase 3) would need the same signature plus more attribute names.

## Build

```bash
cd InsanityPaints
dotnet build -c Release
```

The output `InsanityPaints.dll` goes to
`csgo/addons/counterstrikesharp/plugins/InsanityPaints/` on the live
server, alongside the `gamedata/InsanityPaints.json` signature file (the
CI bundle puts it in `plugins/InsanityPaints/gamedata/`). The three
catalogs and the `settings.json` template land in
`csgo/addons/counterstrikesharp/configs/plugins/InsanityPaints/`.

## Roadmap

Phase 2 and 3 are not implemented yet — listed here so the design space
stays explicit.

- **Phase 2**
  - Live StatTrak counter. Subscribe to `EventPlayerDeath`, increment a
    per-SteamID-per-weapon counter (per-persona for Revive bots),
    persist alongside the loadout. Set `EntityQuality = 9` already
    happens; what's missing is the `kill eater` attribute write, which
    requires the `CAttributeList_SetOrAddAttributeValueByName` gamedata
    signature.
  - Bot-psychology-driven skin selection. Read `BotProfile` from Revive
    (likely via a new `psy_type[120]` field in the pool mmap — would be
    a layout v8 bump on the Revive side) and use it as an extra seed in
    `BotLoadoutResolver` so e.g. an aggressive bot tends toward
    aggressive-looking AK skins.
- **Phase 3**
  - Stickers — needs the gamedata signature mentioned above and a
    `stickers.json` catalog.
  - Keychains / charms — same gamedata signature, separate catalog.

## Not on the menu

Music kits, agents, pins, web admin UI, MySQL persistence, `!wp` /
`!agent` / `!music` commands. The Nereziel plugin covers all of these
if you need them — `InsanityPaints` is intentionally smaller in surface
area.
