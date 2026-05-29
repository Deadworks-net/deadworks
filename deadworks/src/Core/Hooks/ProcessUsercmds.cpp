#include "ProcessUsercmds.hpp"

#include "../UsercmdVisitorRuntime.hpp"
#include "../../SDK/CBaseEntity.hpp"

namespace deadworks {
namespace hooks {

void *__fastcall Hook_ProcessUsercmds(void *pController, void *cmds, int numcmds, unsigned char paused, float margin) {
    auto *entity = static_cast<CBaseEntity *>(reinterpret_cast<CEntityInstance *>(pController));
    int slot = entity->GetRefEHandle().GetEntryIndex() - 1;

    ProcessUsercmdVisitors(slot, cmds, numcmds, paused, margin);

    return g_ProcessUsercmds.thiscall<void *>(pController, cmds, numcmds, paused, margin);
}

} // namespace hooks
} // namespace deadworks
