// Shared layout for /tmp/insanityrevive_fake_slots.bin.
// CSSharp-side pool writer (FakeClientManager.cs / PoolMmap.cs) and C++-
// side pool reader (pool.cpp) MUST agree on these constants — if magic or
// version diverges, pool fails its sanity check and the hider silently
// reinitializes (CSSharp side) or self-disables (C++ side).
//
// Layout v2 (this file):
//   [ 0.. 3] uint32 magic        = 'INSF' = 0x46534E49
//   [ 4.. 7] uint32 version      = 2
//   [ 8..11] uint32 activeFlag   = kill-switch (0/1)
//   [12..131] uint8 managed[120] = per-slot management bit
//   [132..3971] char names[120][32] = per-slot persona name, null-terminated
// Total = 3972 bytes.

#pragma once

#include <stdint.h>
#include <stddef.h>

namespace InsanityHider {

constexpr uint32_t POOL_MAGIC   = 0x46534E49u;
constexpr uint32_t POOL_VERSION = 2u;
constexpr size_t   POOL_SLOTS   = 120;

constexpr size_t   POOL_HEADER_BYTES  = 12;
constexpr size_t   POOL_ACTIVE_OFFSET = 8;
constexpr size_t   POOL_MANAGED_OFFSET = POOL_HEADER_BYTES;
constexpr size_t   POOL_NAMES_OFFSET   = POOL_MANAGED_OFFSET + POOL_SLOTS;  // 132
constexpr size_t   POOL_NAME_BYTES     = 32;
constexpr size_t   POOL_TOTAL          = POOL_NAMES_OFFSET + (POOL_SLOTS * POOL_NAME_BYTES);

} // namespace InsanityHider
