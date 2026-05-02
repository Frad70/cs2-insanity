// Shared layout for /tmp/insanityrevive_fake_slots.bin.
//
// Layout v3:
//   [   0..   3]  uint32 magic        = 'INSF' = 0x46534E49
//   [   4..   7]  uint32 version      = 3
//   [   8..  11]  uint32 activeFlag   = kill-switch (0/1)
//   [  12.. 131]  uint8 managed[120]  = per-slot management bit
//   [ 132..3971]  char names[120][32] = per-slot persona name (null-terminated)
//   [3972..4483]  char fifoBuf[16][32] = SPSC ring buffer of pending personas
//                                       CSSharp pushes (advances head),
//                                       C++ pops (advances tail).
//   [4484..4487]  uint32 fifoHead     = CSSharp writes
//   [4488..4491]  uint32 fifoTail     = C++ writes
// Total = 4492 bytes.
//
// FIFO is single-producer / single-consumer. Counters increment monotonically;
// slot index = counter % capacity. Empty when head == tail. Overflow when
// head - tail >= capacity (don't push more than capacity-1 outstanding).

#pragma once

#include <stdint.h>
#include <stddef.h>

namespace InsanityHider {

constexpr uint32_t POOL_MAGIC   = 0x46534E49u;
constexpr uint32_t POOL_VERSION = 3u;
constexpr size_t   POOL_SLOTS   = 120;

constexpr size_t POOL_HEADER_BYTES   = 12;
constexpr size_t POOL_ACTIVE_OFFSET  = 8;
constexpr size_t POOL_MANAGED_OFFSET = POOL_HEADER_BYTES;            // 12
constexpr size_t POOL_NAMES_OFFSET   = POOL_MANAGED_OFFSET + POOL_SLOTS;  // 132
constexpr size_t POOL_NAME_BYTES     = 32;

constexpr size_t POOL_FIFO_CAPACITY    = 16;
constexpr size_t POOL_FIFO_OFFSET      = POOL_NAMES_OFFSET + (POOL_SLOTS * POOL_NAME_BYTES); // 3972
constexpr size_t POOL_FIFO_BYTES       = POOL_FIFO_CAPACITY * POOL_NAME_BYTES;               // 512
constexpr size_t POOL_FIFO_HEAD_OFFSET = POOL_FIFO_OFFSET + POOL_FIFO_BYTES;                 // 4484
constexpr size_t POOL_FIFO_TAIL_OFFSET = POOL_FIFO_HEAD_OFFSET + 4;                          // 4488

constexpr size_t POOL_TOTAL = POOL_FIFO_TAIL_OFFSET + 4;  // 4492

} // namespace InsanityHider
