#include "ChangeGameState.hpp"
#include "../Deadworks.hpp"

// CCitadelGameRules::m_eGameState offset.
static constexpr uintptr_t kGameStateOffset = 0xfc;

void __fastcall deadworks::hooks::Hook_ChangeGameState(void *thisptr, int newState) {
    const int currentState = *reinterpret_cast<int *>(reinterpret_cast<uintptr_t>(thisptr) + kGameStateOffset);

    if (!g_Deadworks.ShouldAllowGameStateChange(thisptr, currentState, newState)) {
        g_Log->Info("[ChangeGameState] vetoed {} -> {}", currentState, newState);
        return;
    }

    g_Log->Info("[ChangeGameState] this={:#x} newState={}", reinterpret_cast<uintptr_t>(thisptr), newState);
    hooks::g_ChangeGameState.call<void>(thisptr, newState);
    g_Deadworks.OnGameStateChanged(thisptr, newState);
}
