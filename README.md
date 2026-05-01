# INSANITY

> _«Когда-нибудь ты не отличишь их от настоящих игроков. И вот тогда станет по-настоящему страшно.»_

Two-plugin stack that turns CS2 dedicated bots into clients indistinguishable from real
players on the scoreboard — synthetic SteamIDs, persona names from a curated pool, jittered
ping, no `BOT` icon.

## Layout

| Plugin            | Side                                | Role                                                                                                  |
| ----------------- | ----------------------------------- | ----------------------------------------------------------------------------------------------------- |
| `InsanityRevive/` | CSSharp (CounterStrikeSharp / .NET) | Owns lifecycle: spawns bots via `bot_add`, overwrites identity (name, SteamID, profile, ping), JSONL telemetry, ProcessUsercmds detour, mapchange survival. Source of truth for the shared pool. |
| `InsanityHider/`  | Metamod:Source (C++)                | Sits early in the engine callback chain. On `OnClientConnected` post-hook, flips `m_bFakePlayer = 0` for slots marked in the shared pool — *before* the engine's userinfo broadcast leaves the server. That's what kills the `BOT` icon and renders ping in the scoreboard column instead. |

The two halves communicate via a 132-byte mmap'd file at `/tmp/insanityrevive_fake_slots.bin`:

```
[ 0.. 3] magic   = 0x46534E49 ('INSF')
[ 4.. 7] version = 1
[ 8..11] reserved
[12..131] slots[120]   uint8: 0 = unmanaged, 1 = ours
```

CSSharp owns the writes (pre-mark in `Spawn()`, un-mark in `Despawn()`); C++ only reads.

## Single source of truth

`bot_quota 0` in `server.cfg` — the engine never auto-fills bots. Every fake-client that
exists came through `FakeClientManager.Spawn()`, was pre-marked in the pool, and gets its
`BOT` icon flipped off in the post-OCC chain. Manual `bot_add` from rcon is *not* marked
and stays visible as a bot — selectivity is the whole point.

## Design milestone — 2026-05-01

Today the scoreboard finally rendered five fake players with ping and no `BOT` glyph,
plus a sixth real bot from manual `bot_add` correctly visible as a bot. Selectivity
verified end-to-end. That's what the `v0.4.0-hidden` tag marks.

## Build

```bash
# CSSharp side
cd InsanityRevive && dotnet build -c Release

# C++ side (needs hl2sdk-cs2 + metamod-source vendored beside the source tree)
cd InsanityHider && make
```
