#include "GetPlayerStartLane.hpp"
#include "../../SDK/Schema/Schema.hpp"

int32_t __fastcall deadworks::hooks::Hook_GetPlayerStartLane(void *gameRules, void *controller) {
    static const int kController_nAssignedLane = schema::GetOffset(
                                                     "CCitadelPlayerController", hash_32_fnv1a_const("CCitadelPlayerController"),
                                                     "m_nAssignedLane", hash_32_fnv1a_const("m_nAssignedLane"))
                                                     .Offset;

    if (controller && kController_nAssignedLane) {
        const int8_t lane = *reinterpret_cast<int8_t *>(reinterpret_cast<uintptr_t>(controller) + kController_nAssignedLane);
        if (lane > 0)
            return lane;
    }

    return g_GetPlayerStartLane.call<int32_t>(gameRules, controller);
}
