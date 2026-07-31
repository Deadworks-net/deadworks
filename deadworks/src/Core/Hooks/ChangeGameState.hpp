#pragma once

#include <safetyhook.hpp>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_ChangeGameState;
void __fastcall Hook_ChangeGameState(void *thisptr, int newState);

} // namespace hooks
} // namespace deadworks
