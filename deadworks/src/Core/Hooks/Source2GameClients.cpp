#include "Source2GameClients.hpp"

#include "../Deadworks.hpp"

namespace deadworks {
namespace hooks {

void Source2GameClientsHook::Hook_ClientPutInServer(CPlayerSlot slot, const char *pszName, int type, uint64 xuid) {
    g_Source2GameClients_ClientPutInServer.thiscall<void>(this, slot, pszName, type, xuid);
    g_Deadworks.On_ISource2GameClients_ClientPutInServer(slot, pszName, type, xuid);
}

bool Source2GameClientsHook::Hook_ClientConnect(CPlayerSlot slot, const char *pszName, uint64 xuid, const char *pszNetworkID, bool unk1, CBufferString *pRejectReason) {
    bool result = g_Source2GameClients_ClientConnect.thiscall<bool>(this, slot, pszName, xuid, pszNetworkID, unk1, pRejectReason);
    if (!result)
        return false; // engine already rejected

    if (!g_Deadworks.On_ISource2GameClients_ClientConnect(slot, pszName, xuid, pszNetworkID, unk1, pRejectReason))
        return false; // managed rejected (ban, etc.)

    return true;
}

void Source2GameClientsHook::Hook_ClientDisconnect(CPlayerSlot slot, ENetworkDisconnectionReason reason, const char *pszName, uint64 xuid, const char *pszNetworkID) {
    // Before the game's own cleanup, which for a bot deletes its hero pawn.
    g_Deadworks.OnPre_ISource2GameClients_ClientDisconnect(slot, reason);

    g_Source2GameClients_ClientDisconnect.thiscall<void>(this, slot, reason, pszName, xuid, pszNetworkID);

    g_Deadworks.On_ISource2GameClients_ClientDisconnect(slot, reason, pszName, xuid, pszNetworkID);
}

} // namespace hooks
} // namespace deadworks
