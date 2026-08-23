#include "AreAllLobbyPlayersConnected.hpp"

bool __fastcall deadworks::hooks::Hook_AreAllLobbyPlayersConnected(bool bRequireHero, uint32_t *pnConnected, uint32_t *pnTotal) {
    const bool ready = hooks::g_AreAllLobbyPlayersConnected.call<bool>(bRequireHero, pnConnected, pnTotal);

    // The engine counts against the GC lobby, which is always empty on a direct-connect
    // dedicated server, so both outputs come back 0 and readiness is trivially true.
    // Substitute the plugin-supplied counts so the dispatcher's own transition attempt
    // reflects an actual target.
    if (g_LobbyPlayersTotalOverride == 0)
        return ready;

    if (pnConnected)
        *pnConnected = g_LobbyPlayersConnectedOverride;
    if (pnTotal)
        *pnTotal = g_LobbyPlayersTotalOverride;

    return g_LobbyPlayersConnectedOverride >= g_LobbyPlayersTotalOverride;
}
