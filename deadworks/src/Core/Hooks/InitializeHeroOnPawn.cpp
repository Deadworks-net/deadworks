#include "InitializeHeroOnPawn.hpp"

#include "../Deadworks.hpp"
#include "../../SDK/CBaseEntity.hpp"
#include "../../SDK/Schema/Schema.hpp"

namespace deadworks {
namespace hooks {

bool PawnHasLiveController(void *pPawn) {
    static const int kPawn_hController = schema::GetOffset(
                                             "CBasePlayerPawn", hash_32_fnv1a_const("CBasePlayerPawn"),
                                             "m_hController", hash_32_fnv1a_const("m_hController"))
                                             .Offset;
    // Schema lookup failed - leave the call alone rather than silently turning it into a no-op.
    if (kPawn_hController <= 0)
        return true;

    CEntityHandle handle(*reinterpret_cast<const uint32_t *>(
        reinterpret_cast<uintptr_t>(pPawn) + kPawn_hController));
    return handle.IsValid() && handle.Get() != nullptr;
}

// InitializeHeroOnPawn resolves the pawn's controller from m_hController and hands it to
// controller-side helpers without null-checking it; on build 10725 the first of those reads
// [controller+0x984]. A hero pawn whose back-reference is stale (after a pawn swap, a reconnect,
// or a plugin moving heroes between controllers) takes the whole server down when it is next set
// up, from the game's own callers as well as ResetHero. The join flow always has a live
// controller here, so this only ever skips a call that would have crashed.
__int64 __fastcall Hook_InitializeHeroOnPawn(void *pPawn, char bWipeItems) {
    if (pPawn && !PawnHasLiveController(pPawn)) {
        g_Log->Warning("InitializeHeroOnPawn skipped: pawn {:p} has no live controller (m_hController is stale)",
                       pPawn);
        return 0;
    }

    auto result = g_InitializeHeroOnPawn.thiscall<__int64>(pPawn, bWipeItems);
    if (pPawn)
        g_Deadworks.OnPost_InitializeHeroOnPawn(pPawn);
    return result;
}

} // namespace hooks
} // namespace deadworks
