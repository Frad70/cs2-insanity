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
    bool inRange = (idx >= 0 && idx < (int)InsanityHider::POOL_SLOTS);
    bool managed = inRange ? m_Pool.IsManaged(idx) : false;

    auto* pClient = inRange ? ResolveClientBySlot(idx) : nullptr;
    int fpByte = -1;
    if (pClient) fpByte = reinterpret_cast<unsigned char*>(pClient)[kFakePlayerOffset];

    // Pool snapshot for first 8 slots — diagnose pre-mark/slot-pick mismatch.
    unsigned p[8] = {0};
    for (int i = 0; i < 8; ++i) p[i] = m_Pool.IsManaged(i) ? 1 : 0;

    META_CONPRINTF("[InsanityHider] OCC fire slot=%d name=%s fake=%d managed=%d byte160=0x%02x "
                   "pool[0..7]=%u%u%u%u%u%u%u%u active=%d\n",
                   idx, pszName ? pszName : "?", (int)bFakePlayer, (int)managed,
                   fpByte & 0xff, p[0],p[1],p[2],p[3],p[4],p[5],p[6],p[7], (int)m_bActive);

    if (!m_bActive)         { META_CONPRINTF("[InsanityHider] OCC skip reason=inactive slot=%d\n", idx); RETURN_META(MRES_IGNORED); }
    if (!inRange)           { META_CONPRINTF("[InsanityHider] OCC skip reason=out_of_range slot=%d\n", idx); RETURN_META(MRES_IGNORED); }
    if (!bFakePlayer)       { META_CONPRINTF("[InsanityHider] OCC skip reason=not_fake slot=%d\n", idx); RETURN_META(MRES_IGNORED); }
    if (!managed)           { META_CONPRINTF("[InsanityHider] OCC skip reason=unmanaged slot=%d\n", idx); RETURN_META(MRES_IGNORED); }
    if (!pClient)           { META_CONPRINTF("[InsanityHider] OCC skip reason=resolve_null slot=%d\n", idx); RETURN_META(MRES_IGNORED); }
    if (fpByte != 0x01)     { META_CONPRINTF("[InsanityHider] OCC skip reason=byte_not_01 slot=%d byte=0x%02x\n", idx, fpByte & 0xff); RETURN_META(MRES_IGNORED); }

    reinterpret_cast<unsigned char*>(pClient)[kFakePlayerOffset] = 0;
    META_CONPRINTF("[InsanityHider] wrote 0x00 slot=%d name=%s\n", idx, pszName ? pszName : "?");
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

    META_CONPRINTF("[InsanityHider] loaded — m_bFakePlayer offset=%d active=%d\n",
                   kFakePlayerOffset, (int)m_bActive);
    return true;
}

bool InsanityHiderPlugin::Unload(char* error, size_t maxlen) {
    SH_REMOVE_HOOK(IServerGameClients, OnClientConnected, gameclients,
                   SH_MEMBER(this, &InsanityHiderPlugin::Hook_OnClientConnected_Post), true);
    m_Pool.Close();
    return true;
}
