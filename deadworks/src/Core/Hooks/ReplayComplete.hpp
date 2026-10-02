#pragma once

#include <safetyhook.hpp>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_IsReplayComplete;

// Guessed name. bool(CCitadelGameRules*): one byte of the game rules (0x14e8 in 6722). Once the game reaches End and
// starts signing out to the GC, the GC system asks it every frame about the global game rules, without checking that
// there are any. A plugin that changes level after the match ends leaves that sign-out pending while the level
// reloads, when the global is null, and the server crashed reading it. Without game rules this answers false, as a
// null check there would.
bool __fastcall Hook_IsReplayComplete(void *gameRules);

} // namespace hooks
} // namespace deadworks
