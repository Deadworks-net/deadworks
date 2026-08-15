#include "AbilityThink.hpp"

#include "../Deadworks.hpp"
#include "../../SDK/Schema/Schema.hpp"

namespace deadworks {
namespace hooks {

void __fastcall Hook_AbilityThink(void *pPawn) {
    static const int kPawn_hController = schema::GetOffset(
                                             "CBasePlayerPawn", hash_32_fnv1a_const("CBasePlayerPawn"),
                                             "m_hController", hash_32_fnv1a_const("m_hController"))
                                             .Offset;
    static const int kPawn_pMovementServices = schema::GetOffset(
                                                   "CBasePlayerPawn", hash_32_fnv1a_const("CBasePlayerPawn"),
                                                   "m_pMovementServices", hash_32_fnv1a_const("m_pMovementServices"))
                                                   .Offset;
    static const int kMoveSvc_nButtons = schema::GetOffset(
                                             "CPlayer_MovementServices", hash_32_fnv1a_const("CPlayer_MovementServices"),
                                             "m_nButtons", hash_32_fnv1a_const("m_nButtons"))
                                             .Offset;
    static const int kButtonState_States = schema::GetOffset(
                                               "CInButtonState", hash_32_fnv1a_const("CInButtonState"),
                                               "m_pButtonStates", hash_32_fnv1a_const("m_pButtonStates"))
                                               .Offset;

    auto *pawn = reinterpret_cast<char *>(pPawn);

    // Only process player pawns (they have a valid controller handle)
    int hController = *reinterpret_cast<int *>(pawn + kPawn_hController);
    if (hController != -1 && hController != -2) {
        int slot = (hController & 0x7FFF) - 1;
        if (slot >= 0 && slot < 64) {
            auto *pMoveSvc = *reinterpret_cast<char **>(pawn + kPawn_pMovementServices);
            if (pMoveSvc) {
                auto *buttonStates = reinterpret_cast<uint64_t *>(pMoveSvc + kMoveSvc_nButtons + kButtonState_States);
                // buttonStates[0] = held/current, [1] = changed, [2] = scroll

                uint64_t forcedBits = 0;
                uint64_t blockedBits = g_Deadworks.OnPre_AbilityThink(
                    slot, pPawn,
                    buttonStates[0], buttonStates[1], buttonStates[2],
                    &forcedBits);

                if (blockedBits) {
                    buttonStates[0] &= ~blockedBits;
                    buttonStates[1] &= ~blockedBits;
                    buttonStates[2] &= ~blockedBits;
                }
                if (forcedBits) {
                    buttonStates[0] |= forcedBits;
                    buttonStates[1] |= forcedBits;
                }
            }
        }
    }

    g_AbilityThink.call(pPawn);
}

} // namespace hooks
} // namespace deadworks
