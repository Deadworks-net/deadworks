#pragma once

#include "NativeCallbacks.hpp"

namespace deadworks {

void PopulateCollisionNatives(NativeCallbacks &cb);

// Resolve the CCollisionProperty helpers and the entity-pair functions (called from ResolveNativeStatics)
void ResolveCollisionStatics();

} // namespace deadworks
