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
    META_CONPRINTF("[InsanityHider] wrote 0x00 slot=%d name=%s\n", idx, pszName ? pszName : "?");
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
    if (raw[kFakePlayerOffset] != 0x01) RETURN_META(MRES_IGNORED);  // already hidden
    raw[kFakePlayerOffset] = 0;
    META_CONPRINTF("[InsanityHider] late-adopt slot=%d name=%s (CPiS)\n", idx, pszName ? pszName : "?");
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
    return true;
}
