#pragma once

#include "NativeCallbacks.hpp"

namespace deadworks {

void PopulateNavNatives(NativeCallbacks &cb);

// Resolve the CNavMesh global and query functions (called from ResolveNativeStatics)
void ResolveNavStatics();

} // namespace deadworks
