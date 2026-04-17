#pragma once

#include <Windows.h>

#include <filesystem>
#include <optional>
#include <string>

#include <hostfxr.h>
#include <coreclr_delegates.h>

namespace deadworks {

struct DotNetInitError {
    enum class Stage {
        HostFxrPathLookup,
        HostFxrLoadLibrary,
        HostFxrSymbolLookup,
        InitForRuntimeConfig,
        GetRuntimeDelegate,
        LoadManagedEntryPoint,
    };
    Stage stage{};
    int rc = 0;

    std::string_view StageName() const;
};

class DotNetHost {
public:
    bool Initialize(const std::filesystem::path &runtimeConfigPath);
    void Close();

    const std::optional<DotNetInitError> &LastError() const { return m_lastError; }

    // Load an assembly and get a function pointer to a static method marked [UnmanagedCallersOnly]
    template <typename TFunc>
    TFunc GetManagedFunction(const std::filesystem::path &assemblyPath,
                             const wchar_t *typeName,
                             const wchar_t *methodName) {
        if (!m_loadAssemblyAndGetFunctionPointer)
            return nullptr;

        TFunc func = nullptr;
        int rc = m_loadAssemblyAndGetFunctionPointer(
            assemblyPath.c_str(),
            typeName,
            methodName,
            UNMANAGEDCALLERSONLY_METHOD,
            nullptr,
            reinterpret_cast<void **>(&func));

        if (rc != 0 || !func)
            return nullptr;

        return func;
    }

private:
    bool LoadHostFxr();
    bool InitializeRuntime(const std::filesystem::path &runtimeConfigPath);

    HMODULE m_hostfxrLib = nullptr;
    hostfxr_handle m_hostContext = nullptr;

    hostfxr_initialize_for_runtime_config_fn m_initForConfig = nullptr;
    hostfxr_get_runtime_delegate_fn m_getRuntimeDelegate = nullptr;
    hostfxr_close_fn m_close = nullptr;

    load_assembly_and_get_function_pointer_fn m_loadAssemblyAndGetFunctionPointer = nullptr;

    std::optional<DotNetInitError> m_lastError;
};

} // namespace deadworks
