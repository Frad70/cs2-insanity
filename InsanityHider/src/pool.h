// mmap reader for the CSSharp ↔ C++ shared slot pool. CSSharp side
// writes a byte per slot to mark "this slot is one of our managed
// fake-clients". C++ side mmap's the same file READ-only and consults
// pool[idx] inside the OnClientConnected handler to decide whether to
// flip m_bFakePlayer.

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
    // Returns the persona name CSSharp wrote for this slot, or nullptr if
    // empty/unset. The pointer aliases mmap'd memory — valid until Close().
    const char* GetName(int slot) const;
    // Re-validates magic+version against the live mapping. Returns false if
    // the pool was recreated under our feet with a different layout — caller
    // should treat the pool as compromised and stop writing.
    bool RevalidateHeader() const;
    const char* LastError() const { return m_szError; }

private:
    void*    m_pBase    = nullptr;
    size_t   m_nMapped  = 0;
    int      m_iFd      = -1;
    char     m_szError[128] = {0};
};

} // namespace InsanityHider
