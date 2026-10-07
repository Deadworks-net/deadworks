#include "ReplayComplete.hpp"

bool __fastcall deadworks::hooks::Hook_IsReplayComplete(void *gameRules) {
    if (!gameRules)
        return false;
    return g_IsReplayComplete.call<bool>(gameRules);
}
