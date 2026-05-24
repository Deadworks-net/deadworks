#pragma once

#include <safetyhook.hpp>

class CBaseEntity;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_DoModifierEvent;
void __fastcall Hook_DoModifierEvent(unsigned int event,
                                     CBaseEntity *caster,
                                     CBaseEntity *opt_cast_target,
                                     CBaseEntity *opt_cast_ent,
                                     void *modifier_event_data);

} // namespace hooks
} // namespace deadworks
