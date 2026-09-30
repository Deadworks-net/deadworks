#include "NpcSchedule.hpp"

#include "../Deadworks.hpp"
#include "../NativeNpc.hpp"

namespace deadworks {
namespace hooks {

// Runs every time an NPC picks its next schedule (the previous one finished, failed or was interrupted).
// The game's choice is offered to plugins, which can name another schedule; that one is looked up the
// same way, so an id the NPC's class translates (or doesn't know) behaves as if the game had chosen it.
// Schedules set directly (ForceNpcGo, the SetSchedule task, NpcSetSchedule) don't come through here.
void *__fastcall Hook_CAI_BaseNPC_GetNewSchedule(void *npc, void **outSchedule, const char **outName, const char **outId) {
    void *schedule = g_CAI_BaseNPC_GetNewSchedule.call<void *>(npc, outSchedule, outName, outId);

    const char *requested = g_Deadworks.OnPost_NpcSelectSchedule(npc, *outId);
    if (!requested)
        return schedule;

    const char *id = npc::MakeScheduleId(requested);
    const char *name = nullptr;
    void *replacement = id ? npc::GetScheduleOfType(npc, &id, &name) : nullptr;
    if (!replacement)
        return schedule;

    *outId = id;
    *outName = name;
    *outSchedule = replacement;
    return replacement;
}

} // namespace hooks
} // namespace deadworks
