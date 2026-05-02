// mmap'd shared pool, writable on the C++ side as of v3. CSSharp owns the
// schema (sets active flag on boot, clears slots on Despawn, pushes pending
// personas to the FIFO). C++ also writes managed/name when CFC PRE override
// fires — at OCC time the slot is known and we mark it ourselves so CSSharp
// doesn't have to predict it.

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
    bool RevalidateHeader() const;
    const char* GetName(int slot) const;

    // Writes (writable mmap, v3+).
    void WriteManaged(int slot, uint8_t val);
    void WriteName(int slot, const char* name);
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
