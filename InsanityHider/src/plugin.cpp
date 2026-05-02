// InsanityHider — Metamod:Source plugin that hides the BOT icon for
// fake-clients managed by the InsanityRevive (CSSharp) plugin.
//
// Mechanism: when a managed bot completes its OnClientConnected
// post-hook, we write 0 into m_bFakePlayer (offset 160) on its
// CServerSideClient instance. The userinfo string-table broadcast that
// follows reports fakeplayer=0, and the client's panorama scoreboard
// renders ping instead of the BOT icon.
//
// Single-source-of-truth design: server.cfg sets `bot_quota 0`, so the
// engine never auto-fills bots — every CServerSideClient that exists
// was issued by FakeClientManager (CSSharp) via `bot_add`, and every
// such bot fires OnClientConnected. No race, no sweep needed.
//
// Pool layout shared with CSSharp: see src/pool_format.h.

#include "plugin.h"

#include <stdio.h>
#include <stdint.h>
#include <unistd.h>
#include <string.h>
#include <errno.h>
#include <dlfcn.h>

#include <iserver.h>
#include <eiface.h>
#include <tier1/utlvector.h>
#include <tier1/convar.h>

#if !defined(__linux__)
#error "Linux x64 only — CS2 dedicated has no Windows server build."
#endif

// CNetworkGameServerBase->m_Clients lives at byte offset 592 on this
// CS2 build. CS2Fixes' utils.cpp uses
// (CUtlVector<CServerSideClient*>*)(&GetIGameServer()[74]) — pointer-
// array indexing on INetworkGameServer*, stride sizeof(class)=8, so
// 74 × 8 = 592. Validated empirically.
constexpr int kClientListOffset = 74 * 8;

// CServerSideClient::m_bFakePlayer offset on this CS2 build. Reference
// header (CS2Fixes serversideclient.h) put it at 176; live binary has
// it at 160 — layout shifted -16 bytes. Diffed engine bots (0x01) vs
// human (0x00) to confirm.
constexpr int kFakePlayerOffset = 160;

// CServerSideClient::m_Name offset (CUtlString = 8-byte char* m_pString).
// Reference header had it at 80; -16 shift yields 64. Validated probe
// (offset=64 → 'Mangos' for live bot 'Mangos'). Treat as immutable for
// this CS2 build; if engine layout changes, re-probe.
constexpr int kNameOffset = 64;

SH_DECL_HOOK6_void(IServerGameClients, OnClientConnected, SH_NOATTRIB, 0,
                   CPlayerSlot, const char*, uint64, const char*, const char*, bool);
SH_DECL_HOOK4_void(IServerGameClients, ClientPutInServer, SH_NOATTRIB, 0,
                   CPlayerSlot, char const*, int, uint64);

InsanityHiderPlugin g_Plugin;
PLUGIN_EXPOSE(InsanityHiderPlugin, g_Plugin);

IVEngineServer*     engine      = nullptr;
ICvar*              icvar       = nullptr;
IServerGameClients* gameclients = nullptr;
extern INetworkServerService* g_pNetworkServerService;

static CServerSideClient* ResolveClientBySlot(int slot) {
    if (!g_pNetworkServerService) return nullptr;
    auto* server = g_pNetworkServerService->GetIGameServer();
    if (!server) return nullptr;
    auto* clientList = reinterpret_cast<CUtlVector<CServerSideClient*>*>(
        reinterpret_cast<unsigned char*>(server) + kClientListOffset);
    int count = clientList->Count();
    if (count < 0 || count > 256 || slot < 0 || slot >= count) return nullptr;
    return clientList->Element(slot);
}

// Overwrite engine-side m_Name (CUtlString) at offset 64. Calls the
// engine's CUtlString::Set so the heap allocation is managed correctly.
// No-op if dlsym failed at Load() (m_pUtlStringSet == nullptr). Returns
// the m_pString readback after the call (or nullptr if disabled / no-op),
// so the caller can verify the write took.
static const char* OverwriteEngineName(InsanityHiderPlugin* plugin, void* pClient, const char* newName) {
    if (!plugin->m_pUtlStringSet || !pClient || !newName || !newName[0]) return nullptr;
    void* pUtlString = reinterpret_cast<unsigned char*>(pClient) + kNameOffset;
    plugin->m_pUtlStringSet(pUtlString, newName);
    return *reinterpret_cast<const char**>(pUtlString);  // CUtlString::m_pString at offset 0
}

void InsanityHiderPlugin::Hook_OnClientConnected_Post(CPlayerSlot slot, const char* pszName, uint64,
                                                     const char*, const char*, bool bFakePlayer) {
    int idx = slot.Get();

    // Self-disable guard (c): if the pool's magic/version has shifted under
    // us (CSSharp recreated it with a different layout), latch off forever.
    // m_bSelfDisabled is a runtime latch, separate from the kill-switch.
    if (m_bSelfDisabled) RETURN_META(MRES_IGNORED);
    if (m_Pool.IsOpen() && !m_Pool.RevalidateHeader()) {
        META_CONPRINTF("[InsanityHider] error: pool header revalidation failed — self-disabling\n");
        m_bSelfDisabled = true;
        RETURN_META(MRES_IGNORED);
    }

    if (!m_Pool.IsActive())                                   RETURN_META(MRES_IGNORED);
    if (!bFakePlayer)                                         RETURN_META(MRES_IGNORED);
    if (idx < 0 || idx >= (int)InsanityHider::POOL_SLOTS)     RETURN_META(MRES_IGNORED);
    if (!m_Pool.IsManaged(idx))                               RETURN_META(MRES_IGNORED);

    auto* pClient = ResolveClientBySlot(idx);
    if (!pClient) {
        META_CONPRINTF("[InsanityHider] error: ResolveClientBySlot null slot=%d\n", idx);
        RETURN_META(MRES_IGNORED);
    }

    auto* raw = reinterpret_cast<unsigned char*>(pClient);
    if (raw[kFakePlayerOffset] != 0x01) RETURN_META(MRES_IGNORED);  // idempotent
    raw[kFakePlayerOffset] = 0;

    // Overwrite m_Name from pool persona, if CSSharp wrote one.
    const char* persona = m_Pool.GetName(idx);
    const char* readback = persona ? OverwriteEngineName(this, pClient, persona) : nullptr;

    META_CONPRINTF("[InsanityHider] wrote 0x00 slot=%d engineName=%s persona=%s readback=%s\n",
                   idx, pszName ? pszName : "?", persona ? persona : "<none>",
                   readback ? readback : "<n/a>");
    RETURN_META(MRES_IGNORED);
}

void InsanityHiderPlugin::Hook_ClientPutInServer_Post(CPlayerSlot slot, char const* pszName,
                                                      int type, uint64) {
    if (m_bSelfDisabled) RETURN_META(MRES_IGNORED);
    if (!m_Pool.IsActive()) RETURN_META(MRES_IGNORED);
    if (type != 1) RETURN_META(MRES_IGNORED);  // not a fake-client
    int idx = slot.Get();
    if (idx < 0 || idx >= (int)InsanityHider::POOL_SLOTS) RETURN_META(MRES_IGNORED);
    if (!m_Pool.IsManaged(idx)) RETURN_META(MRES_IGNORED);  // CSSharp didn't mark it

    auto* pClient = ResolveClientBySlot(idx);
    if (!pClient) RETURN_META(MRES_IGNORED);
    auto* raw = reinterpret_cast<unsigned char*>(pClient);

    bool wroteByte = (raw[kFakePlayerOffset] == 0x01);
    if (wroteByte) raw[kFakePlayerOffset] = 0;

    // Overwrite m_Name from pool persona for managed slot. Re-applied here
    // (in addition to OCC path) because the engine re-stamps bot names
    // from bot_names.txt during post-spawn — CPiS is later in the chain
    // and outlasts that stamp.
    const char* persona = m_Pool.GetName(idx);
    const char* readback = persona ? OverwriteEngineName(this, pClient, persona) : nullptr;

    if (wroteByte || persona) {
        META_CONPRINTF("[InsanityHider] late-adopt slot=%d engineName=%s persona=%s readback=%s (CPiS)\n",
                       idx, pszName ? pszName : "?", persona ? persona : "<none>",
                       readback ? readback : "<n/a>");
    }
    RETURN_META(MRES_IGNORED);
}

void InsanityHiderPlugin::OnLevelInit(char const* pMapName, char const*, char const*,
                                      char const*, bool, bool) {
    // After mapchange, CServerSideClient instances are recreated with
    // m_bFakePlayer=0x01. With bot_quota=0 (server.cfg), nothing
    // auto-spawns — FakeClientManager re-issues bot_add for each pool
    // slot, and OnClientConnected fires per-bot as expected. Nothing
    // to do here; just log for cross-reference with telemetry.
    META_CONPRINTF("[InsanityHider] OnLevelInit map=%s\n", pMapName ? pMapName : "?");
}

bool InsanityHiderPlugin::Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late) {
    PLUGIN_SAVEVARS();

    GET_V_IFACE_CURRENT(GetEngineFactory, engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
    GET_V_IFACE_CURRENT(GetEngineFactory, icvar,  ICvar,          CVAR_INTERFACE_VERSION);
    GET_V_IFACE_ANY    (GetServerFactory, gameclients, IServerGameClients, INTERFACEVERSION_SERVERGAMECLIENTS);
    GET_V_IFACE_ANY    (GetEngineFactory, g_pNetworkServerService, INetworkServerService,
                        NETWORKSERVERSERVICE_INTERFACE_VERSION);

    g_pCVar = icvar;
    g_SMAPI->AddListener(this, this);

    if (!m_Pool.Open("/tmp/insanityrevive_fake_slots.bin")) {
        META_CONPRINTF("[InsanityHider] error: pool open (%s) — all clients treated as engine\n",
                       m_Pool.LastError());
    } else {
        META_CONPRINTF("[InsanityHider] pool mapped (%zu slots)\n", InsanityHider::POOL_SLOTS);
    }

    // Resolve CUtlString::Set from libtier0 for engine-side name overwrite.
    // RTLD_NOLOAD first — engine has already loaded libtier0; we just want
    // a handle for dlsym. Fallback: regular dlopen if NOLOAD returns null.
    m_pTier0 = dlopen("libtier0.so", RTLD_NOW | RTLD_NOLOAD);
    if (!m_pTier0) m_pTier0 = dlopen("libtier0.so", RTLD_NOW);
    if (m_pTier0) {
        m_pUtlStringSet = reinterpret_cast<CUtlStringSetFn>(
            dlsym(m_pTier0, "_ZN10CUtlString3SetEPKc"));
    }
    if (!m_pUtlStringSet) {
        META_CONPRINTF("[InsanityHider] warning: CUtlString::Set unresolved (%s) — name overwrite disabled, byte 160 only\n",
                       dlerror() ? dlerror() : "no tier0 handle");
    } else {
        META_CONPRINTF("[InsanityHider] CUtlString::Set resolved at %p — name overwrite enabled\n",
                       (void*)m_pUtlStringSet);
    }

    SH_ADD_HOOK(IServerGameClients, OnClientConnected, gameclients,
                SH_MEMBER(this, &InsanityHiderPlugin::Hook_OnClientConnected_Post), true);
    SH_ADD_HOOK(IServerGameClients, ClientPutInServer, gameclients,
                SH_MEMBER(this, &InsanityHiderPlugin::Hook_ClientPutInServer_Post), true);

    META_CONPRINTF("[InsanityHider] loaded — m_bFakePlayer offset=%d, kill-switch via pool[%zu]\n",
                   kFakePlayerOffset, InsanityHider::POOL_ACTIVE_OFFSET);
    return true;
}

bool InsanityHiderPlugin::Unload(char* error, size_t maxlen) {
    SH_REMOVE_HOOK(IServerGameClients, OnClientConnected, gameclients,
                   SH_MEMBER(this, &InsanityHiderPlugin::Hook_OnClientConnected_Post), true);
    SH_REMOVE_HOOK(IServerGameClients, ClientPutInServer, gameclients,
                   SH_MEMBER(this, &InsanityHiderPlugin::Hook_ClientPutInServer_Post), true);
    m_Pool.Close();
    if (m_pTier0) { dlclose(m_pTier0); m_pTier0 = nullptr; m_pUtlStringSet = nullptr; }
    return true;
}
