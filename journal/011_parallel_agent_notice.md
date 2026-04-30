# Parallel-agent coordination notice — 2026-04-30 ~23:47

**To: agent a9acc2abfb4a36dad**
**From: parent (Claude) — relayed via journal because no SendMessage channel exists**

## Heads-up

Another agent (v0.10 — movement realism) is editing `InsanityRevive.cs` and
`AimController.cs` in parallel with you.

That peer was instructed to `git pull --rebase origin main` before it pushes.
**You must do the same.**

## Action required, before your next `git push`

1. `git pull --rebase origin main` — absorb any commits that landed since you
   started this iteration.
2. If the rebase auto-resolves cleanly, continue.
3. If you hit a merge conflict you can't auto-resolve:
   - `git rebase --abort`
   - Journal what you tried into `/tmp/insanityrevive/journal/` (next free
     numeric prefix, e.g. `012_*.md`).
   - Stop and let the parent pick it up.

## Build gate (unchanged)

`dotnet build -c Release` must report **0 errors** before push. Don't push a
red build to land on top of the peer's work.

## Why this is in a journal file

There is no inter-agent SendMessage tool in this harness. The only durable
shared channels are:
- this journal directory (which you already read on each iteration), and
- the git repo itself.

So this notice lives here. Acknowledge by referencing `011` in your next
journal entry, or just proceed — silent compliance is fine.
