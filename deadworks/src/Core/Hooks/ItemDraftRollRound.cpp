#include "ItemDraftRollRound.hpp"

#include "../Deadworks.hpp"
#include "../../SDK/Schema/Schema.hpp"

namespace deadworks {
namespace hooks {

// Address of m_ItemDraftRoundState.m_nID on the pawn. The original bumps it only when it
// actually rolled, so comparing it before and after skips the "drafting disabled" early-out.
static uint32_t *GetDraftRoundId(void *pPawn) {
    static const int32_t offset =
        schema::GetOffset("CCitadelPlayerPawn", hash_32_fnv1a_const("CCitadelPlayerPawn"),
                          "m_ItemDraftRoundState", hash_32_fnv1a_const("m_ItemDraftRoundState")).Offset +
        schema::GetOffset("ItemDraftRoundState_t", hash_32_fnv1a_const("ItemDraftRoundState_t"),
                          "m_nID", hash_32_fnv1a_const("m_nID")).Offset;
    return reinterpret_cast<uint32_t *>(reinterpret_cast<uintptr_t>(pPawn) + offset);
}

__int64 __fastcall Hook_ItemDraftRollRound(void *pPawn, void *pParams) {
    uint32_t idBefore = pPawn ? *GetDraftRoundId(pPawn) : 0;
    auto result = g_ItemDraftRollRound.thiscall<__int64>(pPawn, pParams);
    if (pPawn && *GetDraftRoundId(pPawn) != idBefore)
        g_Deadworks.OnPost_ItemDraftRollRound(pPawn);
    return result;
}

} // namespace hooks
} // namespace deadworks
