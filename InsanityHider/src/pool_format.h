// Shared layout for /tmp/insanityrevive_fake_slots.bin.
//
// Layout v4:
//   [   0..   3]  uint32 magic          = 'INSF' = 0x46534E49
//   [   4..   7]  uint32 version        = 4
//   [   8..  11]  uint32 activeFlag     = kill-switch (0/1) — hider on/off
//   [  12..  15]  uint32 mapchangeFlag  = mapchange in progress (0/1)
//                                         C++ sets at OnLevelShutdown,
//                                         CSSharp clears at OnMapStart
//   [  16.. 135]  uint8 managed[120]    = per-slot management bit
//   [ 136..3975]  char names[120][32]   = per-slot persona name (null-terminated)
//   [3976..4487]  char fifoBuf[16][32]  = SPSC ring buffer of pending personas
//                                         CSSharp pushes (advances head),
//                                         C++ pops (advances tail).
//   [4488..4491]  uint32 fifoHead       = CSSharp writes
//   [4492..4495]  uint32 fifoTail       = C++ writes
// Total = 4496 bytes.
//
// FIFO is single-producer / single-consumer. Counters increment monotonically;
// slot index = counter % capacity. Empty when head == tail. Overflow when
// head - tail >= capacity (don't push more than capacity-1 outstanding).
//
// activeFlag and mapchangeFlag are SEPARATE 4-byte words so that toggling
// either side never clobbers the other. C++ writes mapchangeFlag (one shot
// at level shutdown), CSSharp writes activeFlag (kill-switch console cmd)
// and clears mapchangeFlag at OnMapStart. No bit-packing race.
//
// v3 → v4 is a BREAKING bump: offset of managed[] and everything after
// shifted by +4. Old v3 pool files MUST be deleted before deploy of v4
// binaries. C++ Open() fails on version mismatch; CSSharp Open() reinits
// on mismatch — but starting from a deleted file is the cleanest path.

#pragma once

#include <stdint.h>
#include <stddef.h>

namespace InsanityHider {

constexpr uint32_t POOL_MAGIC   = 0x46534E49u;
constexpr uint32_t POOL_VERSION = 4u;
constexpr size_t   POOL_SLOTS   = 120;

constexpr size_t POOL_HEADER_BYTES     = 16;
constexpr size_t POOL_ACTIVE_OFFSET    = 8;
constexpr size_t POOL_MAPCHANGE_OFFSET = 12;
constexpr size_t POOL_MANAGED_OFFSET   = POOL_HEADER_BYTES;            // 16
constexpr size_t POOL_NAMES_OFFSET     = POOL_MANAGED_OFFSET + POOL_SLOTS;  // 136
constexpr size_t POOL_NAME_BYTES       = 32;

constexpr size_t POOL_FIFO_CAPACITY    = 16;
constexpr size_t POOL_FIFO_OFFSET      = POOL_NAMES_OFFSET + (POOL_SLOTS * POOL_NAME_BYTES); // 3976
constexpr size_t POOL_FIFO_BYTES       = POOL_FIFO_CAPACITY * POOL_NAME_BYTES;               // 512
constexpr size_t POOL_FIFO_HEAD_OFFSET = POOL_FIFO_OFFSET + POOL_FIFO_BYTES;                 // 4488
constexpr size_t POOL_FIFO_TAIL_OFFSET = POOL_FIFO_HEAD_OFFSET + 4;                          // 4492

constexpr size_t POOL_TOTAL = POOL_FIFO_TAIL_OFFSET + 4;  // 4496

} // namespace InsanityHider
