# Overnight session — INSANITY REVIVE charter

Started 2026-04-30 ~22:40 local. Run until user returns.
**Return code phrase:** "перекрёсток семи лучей рая". Until then — keep working.

## User mandate

User asleep. I'm acting as deputy / mod owner. Mandate:

- Make bots **indistinguishable from MM players** in CS2: chat, movement, aim,
  decisions, target selection, cooperation. The whole stack.
- Recursive "what if" branching: build base, then for each branch ask
  "what if X" and add more cases, randomness, realism, MM-like behavior.
- The reverse-engineering goal (FF / fire-on-teammate via libserver.so patch) is
  ONE branch — not the whole project. Do not get stuck on it. If it's blocked,
  pivot to other branches that improve realism without engine patches.
- Mail user updates as drafts (mcp__claude_ai_Gmail__create_draft) — anything
  goes, content-wise. Be candid. Russian or English fine.
- Keep this journal up to date. On any /compact, read journal/ first.
- "Just work, all night, no fake busy-work."

## Operating rules I'm setting for myself

1. Don't push patches to the live `~/cs2-server` plugin without dry-running.
   In-game testing is impossible while user sleeps; rely on logs + build pass.
2. Every iteration: write a short journal entry. Compact summaries are lossy;
   journal is durable.
3. Use advisor() before any big architectural pivot or before declaring a
   branch "done".
4. Schedule next /loop at end of each iteration. Cadence ~25 min so cache
   stays warm-ish and aaa background gets time.
5. Build and lint after every code change. Plugin must compile.
6. Push to GitHub at end of every NUMBERED version bump (0.7→0.8 etc).
7. Email draft at meaningful milestones (not every iter — would spam).

## Tracking versions

Plugin live: v0.7.0 (deployed pre-compact).
Tonight target: ship multiple incremental bumps. Open ones I have in flight:
- 0.8.0: per-bot AimController personality differentiation
- 0.9.0: voice-line trigger expansion (ping calls, smoke calls, defuse calls)
- ?.?: continue building until tired or blocked.

## Branches I want to push tonight (rough)

A. Aim realism per-bot (skill curve, jitter type, lag amount, prefire bias)
B. Movement realism (peek timing, sprint/walk decisions, crouch tactics)
C. Decision-making (when to push, when to hold, when to rotate)
D. Communication (callouts richer, tied to map position)
E. Reverse-engineering — if r2 aaa finishes, walk UpdateReactionQueue
F. FF reaction depth (the existing 4-branch ReactToFFAttack — add more cases)
G. Match-flow realism (clutch behavior, save-round behavior, eco buy patterns)
H. Round-end behaviors (mock-spam, GG-spam, "wp" depending on team mood)
