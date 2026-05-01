// Shared layout for /tmp/insanityrevive_fake_slots.bin.
// CSSharp-side pool writer (FakeClientManager.cs) and C++-side pool
// reader (pool.cpp) MUST agree on these constants — if magic diverges,
// pool fails its sanity check and the hider silently disables itself.

#pragma once

#include <stdint.h>
#include <stddef.h>

namespace InsanityHider {

// "INSF" little-endian = 0x46534E49.
constexpr uint32_t POOL_MAGIC   = 0x46534E49u;
constexpr uint32_t POOL_VERSION = 1u;
constexpr size_t   POOL_SLOTS   = 120;

// Header: { uint32 magic, uint32 version, uint32 activeFlag } = 12 bytes.
// activeFlag at offset 8 is the kill-switch: 0 = hider disabled, 1 = enabled.
// CSSharp writes it via the `insanity_hider_active` ConCommand; C++ reads it
// on every OCC. Followed by uint8_t slots[POOL_SLOTS]. Total = 132 bytes.
constexpr size_t   POOL_HEADER_BYTES  = 12;
constexpr size_t   POOL_ACTIVE_OFFSET = 8;
constexpr size_t   POOL_TOTAL         = POOL_HEADER_BYTES + POOL_SLOTS;

} // namespace InsanityHider
