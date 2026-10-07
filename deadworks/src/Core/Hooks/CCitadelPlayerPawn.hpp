#pragma once

#include <safetyhook.hpp>

#include "../../SDK/Enums.hpp"

class CCitadelPlayerPawn;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_CCitadelPlayerPawn_ModifyCurrency;
void __fastcall Hook_CCitadelPlayerPawn_ModifyCurrency(CCitadelPlayerPawn *thisptr, ECurrencyType nCurrencyType, int32_t nAmount,
                                                        ECurrencySource nSource, int32_t bSilent, int32_t bForceGain, int32_t bSpendOnly,
                                                        void *pSourceAbility, void *pSourceEntity);

inline safetyhook::InlineHook g_CCitadelPlayerPawn_SelectHeroInternal;
void __fastcall Hook_CCitadelPlayerPawn_SelectHeroInternal(CCitadelPlayerPawn *thisptr, void *pHeroDef);

} // namespace hooks
} // namespace deadworks
