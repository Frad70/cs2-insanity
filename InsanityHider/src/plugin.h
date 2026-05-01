#ifndef INSANITY_HIDER_PLUGIN_H
#define INSANITY_HIDER_PLUGIN_H

#include <ISmmPlugin.h>
#include <playerslot.h>

#include "pool.h"

class CServerSideClient;

class InsanityHiderPlugin : public ISmmPlugin, public IMetamodListener {
public:
    bool Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late) override;
    bool Unload(char* error, size_t maxlen) override;

    // Post-hook: writes m_bFakePlayer=0 into the live CServerSideClient
    // for managed slots, so userinfo broadcast tells clients fakeplayer=0
    // and the panorama scoreboard renders ping instead of the BOT icon.
    void Hook_OnClientConnected_Post(CPlayerSlot slot, const char* pszName, uint64 xuid,
                                     const char* pszNetworkID, const char* pszAddress,
                                     bool bFakePlayer);
    void OnLevelInit(char const* pMapName, char const*, char const*, char const*, bool, bool) override;

    const char* GetAuthor()      override { return "frad70"; }
    const char* GetName()        override { return "InsanityHider"; }
    const char* GetDescription() override { return "Selective fake-client hider via direct m_bFakePlayer field write"; }
    const char* GetURL()         override { return ""; }
    const char* GetLicense()     override { return "MIT"; }
    const char* GetVersion()     override { return "0.4.0"; }
    const char* GetDate()        override { return __DATE__; }
    const char* GetLogTag()      override { return "INSANITYHIDER"; }

    InsanityHider::Pool* GetPool() { return &m_Pool; }

private:
    InsanityHider::Pool m_Pool;
    bool m_bSelfDisabled = false;  // latched on pool header corruption
};

extern InsanityHiderPlugin g_Plugin;

PLUGIN_GLOBALVARS();

#endif
