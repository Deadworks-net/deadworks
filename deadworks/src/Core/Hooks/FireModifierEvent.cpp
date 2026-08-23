#include "FireModifierEvent.hpp"

#include "../Deadworks.hpp"

namespace deadworks {
namespace hooks {

__int64 __fastcall Hook_FireModifierEvent(uint32_t event, CBaseEntity *caster, CBaseEntity *target,
                                        CBaseEntity *castEntity, void *eventData) {
    g_Deadworks.OnPre_FireModifierEvent(static_cast<EModifierEvent>(event), caster, target, castEntity, eventData);
    // The native returns the aggregated modifier response (e.g. CHECK_FOR_PARRY), so it must be preserved.
    return g_FireModifierEvent.call<__int64>(event, caster, target, castEntity, eventData);
}

} // namespace hooks
} // namespace deadworks
