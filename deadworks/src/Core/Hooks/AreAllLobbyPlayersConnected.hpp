#pragma once

#include <safetyhook.hpp>
#include <cstdint>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_AreAllLobbyPlayersConnected;

// Plugin-supplied override for the WaitingForPlayersToJoin readiness check. A total of 0
// disables the override, restoring the engine's native (always-empty-lobby) behavior.
// Set from managed code via GameRules.SetWaitingForPlayersRoster().
inline uint32_t g_LobbyPlayersConnectedOverride = 0;
inline uint32_t g_LobbyPlayersTotalOverride = 0;

// Free function, no `this`: bool(bool bRequireHero, int *pnConnected, int *pnTotal).
// Walks the GC lobby's members and counts how many have a connected player controller (and,
// if bRequireHero, an assigned hero). Called once per tick from CCitadelGameRules's
// WaitingForPlayersToJoin dispatch; the return value gates whether the dispatcher attempts
// CCitadelGameRules::ChangeGameState this tick, and *pnTotal is networked to clients.
bool __fastcall Hook_AreAllLobbyPlayersConnected(bool bRequireHero, uint32_t *pnConnected, uint32_t *pnTotal);

} // namespace hooks
} // namespace deadworks
