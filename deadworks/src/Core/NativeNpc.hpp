#pragma once

#include "NativeCallbacks.hpp"

namespace deadworks {

void PopulateNpcNatives(NativeCallbacks &cb);

// Resolve the CAI_BaseNPC order/state functions (called from ResolveNativeStatics)
void ResolveNpcStatics();

namespace npc {

// tier0 MakeGlobalSymbol: the interned name, which is what a schedule id (CGlobalSymbol) holds.
const char *MakeScheduleId(const char *name);

// CAI_BaseNPC::GetScheduleOfType: the schedule for id on this NPC (translated for its class), with its name.
void *GetScheduleOfType(void *npc, const char **id, const char **outName);

} // namespace npc

} // namespace deadworks
