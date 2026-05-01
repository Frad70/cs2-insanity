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
    const char* LastError() const { return m_szError; }

private:
    void*    m_pBase    = nullptr;
    size_t   m_nMapped  = 0;
    int      m_iFd      = -1;
    char     m_szError[128] = {0};
};

} // namespace InsanityHider
