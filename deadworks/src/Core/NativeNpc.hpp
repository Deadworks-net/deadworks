#pragma once

#include "NativeCallbacks.hpp"

namespace deadworks {

void PopulateNpcNatives(NativeCallbacks &cb);

// Resolve the CAI_BaseNPC order/state functions (called from ResolveNativeStatics)
void ResolveNpcStatics();

} // namespace deadworks
