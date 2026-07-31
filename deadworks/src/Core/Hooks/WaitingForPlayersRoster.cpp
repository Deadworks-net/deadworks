#include "WaitingForPlayersRoster.hpp"
#include "../Deadworks.hpp"

#include <interfaces/interfaces.h>
#include <iserver.h>
#include <serversideclient.h>

static uint32_t GetConnectedPlayerCount() {
    if (!g_pNetworkServerService)
        return 0;

    auto *server = g_pNetworkServerService->GetIGameServer();
    if (!server)
        return 0;

    uint32_t count = 0;
    for (int i = 0; i < 64; ++i) {
        auto *client = server->GetClientBySlot(CPlayerSlot(i));
        if (client && client->IsInGame())
            count++;
    }
    return count;
}

bool __fastcall deadworks::hooks::Hook_WaitingForPlayersRoster(bool flag, uint32_t *outReadyCount, uint32_t *outTotalCount) {
    const bool ready = hooks::g_WaitingForPlayersRoster.call<bool>(flag, outReadyCount, outTotalCount);

    // The engine's roster is populated by matchmaking/party data, which is always empty on a
    // direct-connect dedicated server, so both outputs come back 0 and readiness is trivially
    // true. Substitute real connected-player counts so the dispatcher's own transition attempt
    // reflects an actual target.
    if (g_WaitingForPlayersRequiredCount == 0)
        return ready;

    const uint32_t connected = GetConnectedPlayerCount();
    if (outReadyCount)
        *outReadyCount = connected;
    if (outTotalCount)
        *outTotalCount = g_WaitingForPlayersRequiredCount;

    return connected >= g_WaitingForPlayersRequiredCount;
}
