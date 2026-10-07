#include "CCitadelPlayerPawn.hpp"

#include "../Deadworks.hpp"
#include "InitializeHeroOnPawn.hpp"

namespace deadworks {
namespace hooks {

// The three flags are 32-bit in the game: ModifyCurrency reads each stack slot as a dword, and the item-sale path
// passes bSpendOnly = 1 to take the refund off the souls spent (net worth). Forwarding them as bool only wrote the
// low byte of each slot, so the rest was stale stack, the flag no longer read as 1, and every sale counted as
// earned souls: net worth, level and boons went up.
void __fastcall Hook_CCitadelPlayerPawn_ModifyCurrency(CCitadelPlayerPawn *thisptr, ECurrencyType nCurrencyType, int32_t nAmount,
                                                        ECurrencySource nSource, int32_t bSilent, int32_t bForceGain, int32_t bSpendOnly,
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
