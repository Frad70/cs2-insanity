#!/usr/bin/env bash
# Apply small patches to the vendored alliedmodders SDKs to keep this
# project buildable against current upstream HEAD. Each patch is gated by
# a grep so re-runs are idempotent (safe on cache-hit CI runs and on
# repeated local builds).
#
# Usage: scripts/ci-patch-sdks.sh <hl2sdk-root>
# Example: scripts/ci-patch-sdks.sh InsanityHider/hl2sdk

set -eu -o pipefail

SDK_ROOT="${1:?usage: $0 <path/to/hl2sdk>}"

[ -d "$SDK_ROOT" ] || { echo "ERROR: $SDK_ROOT is not a directory" >&2; exit 1; }

# ---- 1. CPlayerSlot needs a default constructor.
#
# SourceHook's SH_DECL_HOOK1 macro expands to `my_rettype orig_ret{};`
# which value-initializes the rettype with a brace-init-list. CPlayerSlot
# in current hl2sdk/cs2 only declares `CPlayerSlot( int slot )`, so the
# compiler suppresses the implicit default ctor and the macro fails to
# compile when hooking a function that returns CPlayerSlot (e.g.
# IVEngineServer::CreateFakeClient). We add a default ctor that
# initializes to the invalid sentinel (-1), matching the semantics of
# the existing Invalidate() method.
playerslot="$SDK_ROOT/public/playerslot.h"
if [ -f "$playerslot" ] && ! grep -q 'CPlayerSlot() : m_Data' "$playerslot"; then
    sed -i 's|CPlayerSlot( int slot ) : m_Data( slot ) {}|CPlayerSlot() : m_Data( -1 ) {}\n\tCPlayerSlot( int slot ) : m_Data( slot ) {}|' \
        "$playerslot"
    echo "patched: $playerslot (added CPlayerSlot default ctor)"
fi
