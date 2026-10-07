#pragma once

#include <memory>

#include "Logger.hpp"

namespace deadworks {

// Process-wide logger. Null until Deadworks::InitFromAppSystem creates the engine-backed one,
// so anything that can run before that (startup, MemoryDataLoader) must check it.
inline std::unique_ptr<Logger> g_Log;

} // namespace deadworks
