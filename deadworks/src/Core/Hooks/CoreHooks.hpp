#pragma once

#include <safetyhook.hpp>

#include <set>
#include <string>

class CAppSystemDict;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_OnAppSystemLoaded;
inline std::set<std::string> g_AppSystemHookLoadList;
void Hook_OnAppSystemLoaded(CAppSystemDict *pThis);

inline safetyhook::InlineHook g_ServerCreateInterface;
void *Hook_ServerCreateInterface(const char *pszName, int *pReturnCode);

inline safetyhook::InlineHook g_CGCClientSystem_OnServerVersionCheck;
bool Hook_CGCClientSystem_OnServerVersionCheck(void *a1, int a2);

inline safetyhook::InlineHook g_CCitadelServerGCSystem_Init;

// Init schedules a shutdown 48 to 72 hours ahead, and the server quits once that time passes unless matchmaking
// has given it a lobby. A server people connect to directly never has one, so it quit mid-match with players on
// it. This clears the scheduled time.
bool Hook_CCitadelServerGCSystem_Init(void *gcSystem);

} // namespace hooks
} // namespace deadworks