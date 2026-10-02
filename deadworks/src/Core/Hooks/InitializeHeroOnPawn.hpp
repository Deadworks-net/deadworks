#pragma once

#include <safetyhook.hpp>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_InitializeHeroOnPawn;
__int64 __fastcall Hook_InitializeHeroOnPawn(void *pPawn, char bWipeItems);

// Whether the pawn's m_hController resolves to a live entity. GetHeroPawn() walking
// controller -> pawn does not imply the pawn points back.
bool PawnHasLiveController(void *pPawn);

} // namespace hooks
} // namespace deadworks
