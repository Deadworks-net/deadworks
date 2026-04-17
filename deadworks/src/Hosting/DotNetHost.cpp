#include "DotNetHost.hpp"

#include <nethost.h>

#include <Windows.h>

namespace deadworks {

std::string_view DotNetInitError::StageName() const {
    switch (stage) {
    case Stage::HostFxrPathLookup: return "get_hostfxr_path";
    case Stage::HostFxrLoadLibrary: return "LoadLibrary(hostfxr.dll)";
    case Stage::HostFxrSymbolLookup: return "GetProcAddress(hostfxr symbols)";
    case Stage::InitForRuntimeConfig: return "hostfxr_initialize_for_runtime_config";
    case Stage::GetRuntimeDelegate: return "hostfxr_get_runtime_delegate";
    case Stage::LoadManagedEntryPoint: return "load DeadworksManaged.EntryPoint.Initialize";
    }
    return "unknown";
}

bool DotNetHost::LoadHostFxr() {
    wchar_t buffer[MAX_PATH];
    size_t bufferSize = sizeof(buffer) / sizeof(wchar_t);

    int rc = get_hostfxr_path(buffer, &bufferSize, nullptr);
    if (rc != 0) {
        m_lastError = DotNetInitError{DotNetInitError::Stage::HostFxrPathLookup, rc};
        return false;
    }

    m_hostfxrLib = LoadLibraryW(buffer);
    if (!m_hostfxrLib) {
        m_lastError = DotNetInitError{DotNetInitError::Stage::HostFxrLoadLibrary, static_cast<int>(GetLastError())};
        return false;
    }

    m_initForConfig = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(m_hostfxrLib, "hostfxr_initialize_for_runtime_config"));
    m_getRuntimeDelegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(m_hostfxrLib, "hostfxr_get_runtime_delegate"));
    m_close = reinterpret_cast<hostfxr_close_fn>(
        GetProcAddress(m_hostfxrLib, "hostfxr_close"));

    if (!(m_initForConfig && m_getRuntimeDelegate && m_close)) {
        m_lastError = DotNetInitError{DotNetInitError::Stage::HostFxrSymbolLookup, 0};
        return false;
    }
    return true;
}

bool DotNetHost::InitializeRuntime(const std::filesystem::path &runtimeConfigPath) {
    int rc = m_initForConfig(runtimeConfigPath.c_str(), nullptr, &m_hostContext);
    if (rc != 0 || !m_hostContext) {
        m_lastError = DotNetInitError{DotNetInitError::Stage::InitForRuntimeConfig, rc};
        return false;
    }

    void *delegate = nullptr;
    rc = m_getRuntimeDelegate(
        m_hostContext,
        hdt_load_assembly_and_get_function_pointer,
        &delegate);

    if (rc != 0 || !delegate) {
        m_lastError = DotNetInitError{DotNetInitError::Stage::GetRuntimeDelegate, rc};
        return false;
    }

    m_loadAssemblyAndGetFunctionPointer =
        reinterpret_cast<load_assembly_and_get_function_pointer_fn>(delegate);

    return true;
}

bool DotNetHost::Initialize(const std::filesystem::path &runtimeConfigPath) {
    m_lastError.reset();

    if (!LoadHostFxr())
        return false;

    if (!InitializeRuntime(runtimeConfigPath))
        return false;

    return true;
}

void DotNetHost::Close() {
    if (m_close && m_hostContext) {
        m_close(m_hostContext);
        m_hostContext = nullptr;
    }

    if (m_hostfxrLib) {
        FreeLibrary(m_hostfxrLib);
        m_hostfxrLib = nullptr;
    }

    m_loadAssemblyAndGetFunctionPointer = nullptr;
}

} // namespace deadworks
