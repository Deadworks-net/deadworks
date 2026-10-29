#pragma once

#include "NativeCallbacks.hpp"

namespace deadworks {

void PopulateBotNatives(NativeCallbacks &cb);

// Resolve the bot brain functions and the bot-match flag (called from ResolveNativeStatics)
void ResolveBotStatics();

} // namespace deadworks
