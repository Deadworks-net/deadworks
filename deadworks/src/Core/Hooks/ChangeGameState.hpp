#pragma once

#include <safetyhook.hpp>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_ChangeGameState;
void __fastcall Hook_ChangeGameState(void *thisptr, int newState);

// Runs the real CCitadelGameRules::ChangeGameState via the trampoline and notifies managed of
// the resulting state. Shared by the inline hook (after the veto check) and the plugin-facing
// native callback so plugins always observe OnGameStateChanged, whoever started the transition.
void ChangeGameState(void *gameRules, int newState);

} // namespace hooks
} // namespace deadworks
