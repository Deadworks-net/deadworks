#include "NetworkServerService.hpp"

#include "../Deadworks.hpp"

namespace deadworks {
namespace hooks {

void NetworkServerServiceHook::Hook_StartupServer(const GameSessionConfiguration_t &config, ISource2WorldSession *pWorldSession, const char *pszMapName) {
    // Pass the config through as a pointer: a reference argument is deduced by value here, which copies the
    // whole CSVCMsg_GameSessionConfiguration through our compiled protobuf layout - that layout does not have to
    // match the engine's and crashed on the 2026-09-29 patch. A pointer is ABI-identical to the reference.
    g_NetworkServerService_StartupServer.thiscall<void>(this, &config, pWorldSession, pszMapName);
    g_Deadworks.On_StartupServer(pszMapName);
}

} // namespace hooks
} // namespace deadworks
