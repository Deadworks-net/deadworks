#pragma once

#include <safetyhook.hpp>
#include <cstdint>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_GetPlayerStartLane;

// Unnamed CCitadelGameRules helper: the lane whose zipline a player rides when GameInProgress
// starts. The engine tries citadel_active_lane, citadel_force_assigned_lane, the GC lobby's
// per-slot lane, then a lane map for bots, and finally lane 1 when the match intro is eligible.
// A direct-connect server has no lobby, so every player lands on the same lane. When a plugin has
// set the controller's m_nAssignedLane, that lane wins instead.
int32_t __fastcall Hook_GetPlayerStartLane(void *gameRules, void *controller);

} // namespace hooks
} // namespace deadworks
