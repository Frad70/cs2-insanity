// mmap'd shared pool, writable on the C++ side as of v3. CSSharp owns the
// schema (sets active flag on boot, clears slots on Despawn, pushes pending
// personas to the FIFO). C++ also writes managed/name when CFC PRE override
// fires — at OCC time the slot is known and we mark it ourselves so CSSharp
// doesn't have to predict it.
//
// v4 (mapchange survival): C++ writes mapchangeFlag at OnLevelShutdown;
// CSSharp reads it at OnClientDisconnect to skip Despawn during the engine's
// synthetic disconnect cascade, then clears at OnMapStart after snapshotting.

#pragma once

#include "pool_format.h"

namespace InsanityHider {

class Pool {
public:
    bool Open(const char* path);
    void Close();
    bool IsOpen() const { return m_pBase != nullptr; }
    bool IsManaged(int slot) const;
    bool IsActive() const;
    bool IsMapchangeInProgress() const;
    bool RevalidateHeader() const;
    const char* GetName(int slot) const;

    // v5 aim-override block. CSSharp WRITES; C++ READS in the AimHook
    // PRE-detour. Single global pair for now; per-slot is a future
    // extension. See pool_format.h for the byte layout.
    bool  IsAimOverrideEnabled() const;
    float GetAimPitch() const;
    float GetAimYaw() const;

    // Writes (writable mmap, v3+).
    void WriteManaged(int slot, uint8_t val);
    void WriteName(int slot, const char* name);
    void WriteMapchangeFlag(bool inProgress);
    // Pop one pending persona from the FIFO. Copies into outBuf (always
    // null-terminates). Returns true if a name was popped, false if FIFO empty.
    bool PopFifo(char* outBuf, size_t outBufBytes);

    const char* LastError() const { return m_szError; }

private:
    void*    m_pBase    = nullptr;
    size_t   m_nMapped  = 0;
    int      m_iFd      = -1;
    char     m_szError[128] = {0};
};

} // namespace InsanityHider
