# Vendor patches

Diffs applied to vendored `hl2sdk` / `mmsource` clones at CI time. Keep
each patch tiny and self-contained — the goal is to unstick the build,
not to fork upstream.

## `hl2sdk-cplayerslot-default-ctor.patch`

Restores a default constructor on `CPlayerSlot` so SourceHook's
`SH_CALL_HOOKS` macro can declare `my_rettype orig_ret;` for hooks whose
return type is `CPlayerSlot` (we use one for `IVEngineServer::CreateFakeClient`).

The added constructor sets `m_Data = -1`, which mirrors `Invalidate()` —
so an accidentally-default-constructed `CPlayerSlot` is invalid by
default and won't quietly impersonate slot 0.

Tracked in https://github.com/Frad70/cs2-insanity/issues/33. Drop the
patch + the `git apply` step in `.github/workflows/build.yml` once
upstream `hl2sdk@cs2` reintroduces a default ctor (or sourcehook stops
needing one).
