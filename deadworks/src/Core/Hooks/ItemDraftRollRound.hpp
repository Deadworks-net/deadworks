#pragma once

#include <safetyhook.hpp>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_ItemDraftRollRound;
__int64 __fastcall Hook_ItemDraftRollRound(void *pPawn, void *pParams);

} // namespace hooks
} // namespace deadworks
