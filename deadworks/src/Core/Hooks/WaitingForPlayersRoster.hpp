#pragma once

#include <safetyhook.hpp>
#include <cstdint>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_WaitingForPlayersRoster;

// 0 disables the override, restoring the engine's native (always-empty-roster) behavior.
// Set from managed code via GameRules.SetWaitingForPlayersRequiredCount().
inline uint32_t g_WaitingForPlayersRequiredCount = 0;

// Free function, no `this`: bool(bool, uint32_t *outReadyCount, uint32_t *outTotalCount).
// Called once per tick from CCitadelGameRules's WaitingForPlayersToJoin dispatch. The return
// value gates whether the dispatcher attempts CCitadelGameRules::ChangeGameState this tick.
bool __fastcall Hook_WaitingForPlayersRoster(bool flag, uint32_t *outReadyCount, uint32_t *outTotalCount);

} // namespace hooks
} // namespace deadworks
