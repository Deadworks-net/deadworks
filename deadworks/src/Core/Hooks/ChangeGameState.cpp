#include "ChangeGameState.hpp"
#include "../Deadworks.hpp"
#include "../../SDK/Schema/Schema.hpp"

namespace deadworks {
namespace hooks {

static int GetGameState(void *gameRules) {
    static const int kGameRules_eGameState = schema::GetOffset(
                                                 "CCitadelGameRules", hash_32_fnv1a_const("CCitadelGameRules"),
                                                 "m_eGameState", hash_32_fnv1a_const("m_eGameState"))
                                                 .Offset;
    return *reinterpret_cast<int *>(reinterpret_cast<uintptr_t>(gameRules) + kGameRules_eGameState);
}

void ChangeGameState(void *gameRules, int newState) {
    g_ChangeGameState.call<void>(gameRules, newState);
    // Report what the engine actually landed on rather than what was requested.
    g_Deadworks.OnGameStateChanged(GetGameState(gameRules));
}

void __fastcall Hook_ChangeGameState(void *thisptr, int newState) {
    if (!g_Deadworks.ShouldAllowGameStateChange(GetGameState(thisptr), newState))
        return;

    ChangeGameState(thisptr, newState);
}

} // namespace hooks
} // namespace deadworks
