#pragma once

#include <safetyhook.hpp>

class CBaseEntity;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_FireModifierEvent;
__int64 __fastcall Hook_FireModifierEvent(uint32_t event, CBaseEntity *caster, CBaseEntity *target,
                                        CBaseEntity *castEntity, void *eventData);

} // namespace hooks
} // namespace deadworks
