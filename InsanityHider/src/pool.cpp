#include "pool.h"

#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <errno.h>
#include <string.h>
#include <stdio.h>

namespace InsanityHider {

bool Pool::Open(const char* path) {
    Close();

    m_iFd = ::open(path, O_RDONLY);
    if (m_iFd < 0) {
        snprintf(m_szError, sizeof(m_szError),
                 "open(%s) failed: errno=%d %s", path, errno, strerror(errno));
        return false;
    }

    struct stat st{};
    if (fstat(m_iFd, &st) < 0) {
        snprintf(m_szError, sizeof(m_szError),
                 "fstat failed: errno=%d %s", errno, strerror(errno));
        Close();
        return false;
    }
    if (st.st_size < (off_t)POOL_TOTAL) {
        snprintf(m_szError, sizeof(m_szError),
                 "pool too small: %lld < %zu", (long long)st.st_size, POOL_TOTAL);
        Close();
        return false;
    }

    void* base = mmap(nullptr, POOL_TOTAL, PROT_READ, MAP_SHARED, m_iFd, 0);
    if (base == MAP_FAILED) {
        snprintf(m_szError, sizeof(m_szError),
                 "mmap failed: errno=%d %s", errno, strerror(errno));
        Close();
        return false;
    }

    auto* u32 = reinterpret_cast<const uint32_t*>(base);
    uint32_t magic = u32[0], version = u32[1];
    if (magic != POOL_MAGIC) {
        snprintf(m_szError, sizeof(m_szError),
                 "magic mismatch: got 0x%08x want 0x%08x — refusing to use stale pool",
                 magic, POOL_MAGIC);
        munmap(base, POOL_TOTAL);
        Close();
        return false;
    }
    if (version != POOL_VERSION) {
        snprintf(m_szError, sizeof(m_szError),
                 "version mismatch: got %u want %u", version, POOL_VERSION);
        munmap(base, POOL_TOTAL);
        Close();
        return false;
    }

    m_pBase = base;
    m_nMapped = POOL_TOTAL;
    m_szError[0] = '\0';
    return true;
}

void Pool::Close() {
    if (m_pBase) {
        munmap(m_pBase, m_nMapped);
        m_pBase = nullptr;
        m_nMapped = 0;
    }
    if (m_iFd >= 0) {
        ::close(m_iFd);
        m_iFd = -1;
    }
}

bool Pool::IsManaged(int slot) const {
    if (!m_pBase) return false;
    if (slot < 0 || slot >= (int)POOL_SLOTS) return false;
    auto* slots = reinterpret_cast<const uint8_t*>(m_pBase) + POOL_HEADER_BYTES;
    return slots[slot] != 0;
}

bool Pool::IsActive() const {
    if (!m_pBase) return false;
    auto* p = reinterpret_cast<const uint32_t*>(
        reinterpret_cast<const uint8_t*>(m_pBase) + POOL_ACTIVE_OFFSET);
    return *p != 0;
}

bool Pool::RevalidateHeader() const {
    if (!m_pBase) return false;
    auto* u32 = reinterpret_cast<const uint32_t*>(m_pBase);
    return u32[0] == POOL_MAGIC && u32[1] == POOL_VERSION;
}

} // namespace InsanityHider
