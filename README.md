# INSANITY

A two-plugin engineering experiment for a **Counter-Strike 2 dedicated server you
own and administer**. Gives the server's engine bots a more lifelike identity
on the scoreboard — synthetic SteamID64s, curated persona names, jittered ping,
and the `BOT` glyph hidden — so practice/training/low-population servers feel
populated instead of empty.

The interesting part isn't the feature — it's the plumbing under it: a
C# (CSSharp) plugin and a C++ (Metamod:Source) plugin co-operating over a
shared mmap, an inline detour into `libserver.so`, ProcessUsercmds interception,
schema patching, and a deterministic CI pipeline.

> ⚠️ **Intended use.** This is for **your own dedicated server** —
> private practice ranges, gun-game servers, training boxes, internal LAN
> games. **Do not** run it on official Valve matchmaking servers, public
> ranked servers, or any server where players would have a reasonable
> expectation of distinguishing humans from bots. See [Disclaimer](#disclaimer)
> below.

## Layout

| Plugin            | Side                                | Role                                                                                                  |
| ----------------- | ----------------------------------- | ----------------------------------------------------------------------------------------------------- |
| `InsanityRevive/` | CSSharp (CounterStrikeSharp / .NET 8) | Owns lifecycle: spawns bots via `bot_add`, overwrites identity (name, SteamID, profile, ping), JSONL telemetry, ProcessUsercmds detour, mapchange survival. Source of truth for the shared pool. |
| `InsanityHider/`  | Metamod:Source (C++)                | Sits early in the engine callback chain. On `OnClientConnected` post-hook, flips `m_bFakePlayer = 0` for slots marked in the shared pool — *before* the engine's userinfo broadcast leaves the server. That's what hides the `BOT` icon and renders ping in the scoreboard column instead. |

The two halves communicate via a 132-byte mmap'd file at
`/tmp/insanityrevive_fake_slots.bin`:

```
[ 0.. 3] magic   = 0x46534E49 ('INSF')
[ 4.. 7] version = 1
[ 8..11] reserved
[12..131] slots[120]   uint8: 0 = unmanaged, 1 = ours
```

CSSharp owns the writes (pre-mark in `Spawn()`, un-mark in `Despawn()`); C++ only reads.

## SteamID generation

SteamID64s for managed bots are minted from a **reserved synthetic range**
(`76561198_900_000_000` .. `76561198_999_999_999`) and seeded from the
session ID. The range is intentionally above where real Steam accounts have
ever populated, so synthetic IDs cannot collide with a live account. There is
no facility for using real Steam IDs.

## Single source of truth

`bot_quota 0` in `server.cfg` — the engine never auto-fills bots. Every
fake-client that exists came through `FakeClientManager.Spawn()`, was
pre-marked in the pool, and gets its `BOT` icon flipped off in the post-OCC
chain. Manual `bot_add` from rcon is *not* marked and stays visible as a
bot — selectivity is the whole point.

## Build

```bash
# CSSharp side. SRCDS_ROOT must point at the dedicated-server root, or pass
# -p:CSSharpApiPath=… to point at a CounterStrikeSharp.API.dll directly.
cd InsanityRevive && dotnet build -c Release

# C++ side. Needs hl2sdk-cs2 + metamod-source vendored beside the source tree
# (the CI workflow under .github/workflows/build.yml does this from scratch
# and is the canonical reference for how to set them up).
cd InsanityHider && make
```

The `scripts/deploy.sh` helper builds, prints a deploy-baseline stanza
(commit-sha + dll sha256), and optionally copies the binary to
`$SRCDS_ROOT/game/csgo/addons/counterstrikesharp/plugins/InsanityRevive/`.

## Documentation

- [`notes/`](notes/) — design notes for live-engine probes (Stage 3/4 series).
  These document what was checked on disk vs. what required a connected
  human client to verify, and which schema fields turned out to be
  server-side-only on current CS2 builds.

## Disclaimer

This project is an independent technical experiment. It is **not affiliated
with, endorsed by, or sponsored by Valve Corporation**. *Counter-Strike 2*,
*Counter-Strike*, *Steam*, *SteamID*, and all related trademarks are the
property of Valve Corporation.

The plugins manipulate server-side bot state on a Source 2 dedicated server
you run yourself. They do not modify the *Counter-Strike 2* client, do not
ship game assets, and do not interact with Valve's matchmaking or
authentication services in any way. Use of these plugins on official
Valve-operated servers or matchmaking is **not** an intended use and may
violate Valve's terms of service for those services. **Run only on
dedicated servers you own and administer.**

This software is provided "as is", without warranty of any kind — see
[`LICENSE`](LICENSE) for the full MIT license terms.
