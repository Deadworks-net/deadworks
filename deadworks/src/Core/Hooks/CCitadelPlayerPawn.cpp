#include "CCitadelPlayerPawn.hpp"

#include "../Deadworks.hpp"
#include "InitializeHeroOnPawn.hpp"

namespace deadworks {
namespace hooks {

void __fastcall Hook_CCitadelPlayerPawn_ModifyCurrency(CCitadelPlayerPawn *thisptr, ECurrencyType nCurrencyType, int32_t nAmount,
                                                        ECurrencySource nSource, bool bSilent, bool bForceGain, bool bSpendOnly,
                                                        void *pSourceAbility, void *pSourceEntity) {
    if (g_Deadworks.OnPre_CCitadelPlayerPawn_ModifyCurrency(thisptr, nCurrencyType, nAmount, nSource, bSilent, bForceGain, bSpendOnly, pSourceAbility, pSourceEntity))
        return;

    g_CCitadelPlayerPawn_ModifyCurrency.thiscall<void>(thisptr, nCurrencyType, nAmount, nSource, bSilent, bForceGain, bSpendOnly, pSourceAbility, pSourceEntity);
}

// Like InitializeHeroOnPawn, SelectHeroInternal passes the controller it resolves from m_hController
// on without a null check; on build 10725 a pawn with a stale one dies reading [null+0xD09] at
// server+0x6df598. CreateHeroPawn links the controller before its callers get here, so only a pawn
// that has already lost it is refused.
void __fastcall Hook_CCitadelPlayerPawn_SelectHeroInternal(CCitadelPlayerPawn *thisptr, void *pHeroDef) {
    if (thisptr && !PawnHasLiveController(thisptr)) {
        g_Log->Warning("SelectHeroInternal skipped: pawn {:p} has no live controller (m_hController is stale)",
                       static_cast<void *>(thisptr));
        return;
    }

    g_CCitadelPlayerPawn_SelectHeroInternal.thiscall<void>(thisptr, pHeroDef);
}

} // namespace hooks
} // namespace deadworks
