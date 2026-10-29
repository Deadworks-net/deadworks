#pragma once

#include <safetyhook.hpp>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_CAI_BaseNPC_GetNewSchedule;
void *__fastcall Hook_CAI_BaseNPC_GetNewSchedule(void *npc, void **outSchedule, const char **outName, const char **outId);

} // namespace hooks
} // namespace deadworks
