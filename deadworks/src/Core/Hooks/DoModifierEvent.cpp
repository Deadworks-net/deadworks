#include "DoModifierEvent.hpp"

#include "../Deadworks.hpp"

namespace deadworks {
namespace hooks {

void __fastcall Hook_DoModifierEvent(unsigned int event, CBaseEntity *caster, CBaseEntity *opt_cast_target, CBaseEntity *opt_cast_ent, void *event_data) {
    g_Deadworks.OnDoModifierEvent(static_cast<EModifierEvent>(event), caster, opt_cast_target, opt_cast_ent, event_data);
    g_DoModifierEvent.call(event, caster, opt_cast_target, opt_cast_ent, event_data);
}

} // namespace hooks
} // namespace deadworks
