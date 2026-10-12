#include "CoreHooks.hpp"

#include <safetyhook.hpp>

#include <iappsystem.h>

#include <set>
#include <string>

#include "../Deadworks.hpp"
#include "../NativeOffsets.hpp"

namespace deadworks {
namespace hooks {

void Hook_OnAppSystemLoaded(CAppSystemDict *pThis) {
    // no logging until after InitFromAppSystem
    g_OnAppSystemLoaded.call(pThis);

    constexpr std::string_view moduleName = "materialsystem2";

    for (const auto &module : pThis->m_Modules) {
        auto [_, result] = g_AppSystemHookLoadList.insert(module.m_pModuleName);
        if (result) {
            if (module.m_pModuleName == moduleName) {
                g_Deadworks.InitFromAppSystem(pThis);
            }
        }
    }
}

void *Hook_ServerCreateInterface(const char *pszName, int *pReturnCode) {
    void *result = g_ServerCreateInterface.call<void *>(pszName, pReturnCode);

    constexpr std::string_view interfaceName = "Source2Server001";
    if (pszName == interfaceName) {
        if (!g_ServerCreateInterface.disable().has_value()) {
            g_Log->Warning("Failed to disable ServerCreateInterface hook");
        }
        g_Deadworks.PostInit();
    }

    return result;
}
bool Hook_CGCClientSystem_OnServerVersionCheck(void *a1, int a2) {
    g_Log->Info("Engine version check. Forcing true. GC wants: {}", a2);
    return true;
}

bool Hook_CCitadelServerGCSystem_Init(void *gcSystem) {
    const bool result = g_CCitadelServerGCSystem_Init.call<bool>(gcSystem);
    *reinterpret_cast<uint32_t *>(reinterpret_cast<uintptr_t>(gcSystem) + offsets::kServerGCSystem_MaxUptimeShutdownTime) = 0;
    return result;
}

} // namespace hooks
} // namespace deadworks
