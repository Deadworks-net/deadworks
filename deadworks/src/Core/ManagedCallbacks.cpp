#include "ManagedCallbacks.hpp"
#include "NativeCallbacks.hpp"
#include "Deadworks.hpp"
#include "../Hosting/DotNetHost.hpp"

#include <cstdlib>

using namespace deadworks;

[[noreturn]] static void FatalDotNetInitFailure(const std::filesystem::path &runtimeConfig, const DotNetInitError &err) {
    g_Log->Critical(
        "Failed to initialize .NET scripting runtime. "
        "stage={} rc={} (0x{:08X}) runtimeConfig={}. "
        "This almost always means the required .NET runtime version is not installed, "
        "or does not match the TargetFramework in the runtime config. "
        "Install the matching .NET runtime and try again.",
        err.StageName(),
        err.rc,
        static_cast<unsigned int>(err.rc),
        runtimeConfig.string());

    std::_Exit(1);
}

namespace {

// CLR surfaces unhandled managed exceptions to native [UnmanagedCallersOnly] callers
// as SEH with this exception code. ExceptionInformation[0] holds the HResult.
constexpr DWORD kClrExceptionCode = 0xE0434352;

struct ManagedInvokeFailure {
    DWORD exceptionCode = 0;
    ULONG_PTR hresult = 0;
};

DWORD CaptureManagedException(EXCEPTION_POINTERS *ep, ManagedInvokeFailure *out) {
    out->exceptionCode = ep->ExceptionRecord->ExceptionCode;
    if (ep->ExceptionRecord->NumberParameters > 0)
        out->hresult = ep->ExceptionRecord->ExceptionInformation[0];
    return EXCEPTION_EXECUTE_HANDLER;
}

using ManagedInitializeFn = void(CORECLR_DELEGATE_CALLTYPE *)(NativeCallbacks *);

// Isolated from callers with C++ objects because __try/__except forbids
// destructor unwinding through the guarded region.
bool InvokeManagedInitialize(ManagedInitializeFn fn, NativeCallbacks *callbacks,
                             ManagedInvokeFailure &failure) {
    __try {
        fn(callbacks);
        return true;
    } __except (CaptureManagedException(GetExceptionInformation(), &failure)) {
        return false;
    }
}

} // namespace

[[noreturn]] static void FatalManagedInvokeFailure(const std::filesystem::path &managedDir,
                                                   const ManagedInvokeFailure &failure) {
    if (failure.exceptionCode == kClrExceptionCode) {
        g_Log->Critical(
            "Managed EntryPoint.Initialize threw an unhandled exception "
            "(HResult=0x{:08X}). This almost always means DeadworksManaged.dll "
            "could not load one of its dependencies. Verify every DLL listed in "
            "DeadworksManaged.deps.json (e.g. Google.Protobuf.dll) is present "
            "alongside DeadworksManaged.dll in {}.",
            static_cast<unsigned int>(failure.hresult),
            managedDir.string());
    } else {
        g_Log->Critical(
            "Managed EntryPoint.Initialize crashed with native exception code "
            "0x{:08X} (param0=0x{:016X}).",
            static_cast<unsigned int>(failure.exceptionCode),
            static_cast<uint64_t>(failure.hresult));
    }

    std::_Exit(1);
}

// ConCommand dispatch fn pointer defined in NativeCallbacks.cpp
using ManagedConCommandDispatchFn = void(CORECLR_DELEGATE_CALLTYPE *)(int playerSlot, const char *command, int argc, const char **argv);
extern ManagedConCommandDispatchFn g_ManagedConCommandDispatch;

static constexpr const wchar_t *kManagedTypeName = L"DeadworksManaged.EntryPoint, DeadworksManaged";

template <typename T>
static void BindCallback(DotNetHost &host, const std::filesystem::path &assemblyPath,
                          T &outFn, const wchar_t *methodName) {
    outFn = host.GetManagedFunction<T>(assemblyPath, kManagedTypeName, methodName);
    if (!outFn) {
        std::string name(methodName, methodName + wcslen(methodName));
        g_Log->Error("Failed to get managed EntryPoint.{}", name);
    }
}

void deadworks::InitializeManagedCallbacks(DotNetHost &host, ManagedCallbacks &managed) {
    auto exePath = std::filesystem::current_path();
    auto managedDir = exePath / "managed";
    auto runtimeConfig = managedDir / "DeadworksManaged.runtimeconfig.json";
    auto assemblyPath = managedDir / "DeadworksManaged.dll";

    if (!host.Initialize(runtimeConfig)) {
        auto err = host.LastError().value_or(DotNetInitError{});
        FatalDotNetInitFailure(runtimeConfig, err);
    }

    g_Log->Info(".NET runtime initialized");

    using InitializeFn = void(CORECLR_DELEGATE_CALLTYPE *)(NativeCallbacks *);
    auto initialize = host.GetManagedFunction<InitializeFn>(
        assemblyPath, kManagedTypeName, L"Initialize");

    if (!initialize) {
        FatalDotNetInitFailure(runtimeConfig, DotNetInitError{DotNetInitError::Stage::LoadManagedEntryPoint, 0});
    }

    NativeCallbacks callbacks{};
    PopulateNativeCallbacks(callbacks);

    ManagedInvokeFailure failure{};
    if (!InvokeManagedInitialize(initialize, &callbacks, failure)) {
        FatalManagedInvokeFailure(managedDir, failure);
    }

    g_Log->Info(".NET managed code invoked successfully");

    BindCallback(host, assemblyPath, managed.onStartupServer, L"OnStartupServer");
    BindCallback(host, assemblyPath, managed.onTakeDamageOld, L"OnTakeDamageOld");
    BindCallback(host, assemblyPath, managed.onModifyCurrency, L"OnModifyCurrency");
    BindCallback(host, assemblyPath, managed.onClientConCommand, L"OnClientConCommand");
    BindCallback(host, assemblyPath, managed.onGameEvent, L"OnGameEvent");
    BindCallback(host, assemblyPath, managed.onGameFrame, L"OnGameFrame");
    BindCallback(host, assemblyPath, managed.onNetMessageOutgoing, L"OnNetMessageOutgoing");
    BindCallback(host, assemblyPath, managed.onNetMessageIncoming, L"OnNetMessageIncoming");
    BindCallback(host, assemblyPath, managed.onClientConnect, L"OnClientConnect");
    BindCallback(host, assemblyPath, managed.onClientPutInServer, L"OnClientPutInServer");
    BindCallback(host, assemblyPath, managed.onClientFullConnect, L"OnClientFullConnect");
    BindCallback(host, assemblyPath, managed.onClientDisconnect, L"OnClientDisconnect");
    BindCallback(host, assemblyPath, managed.onEntityCreated, L"OnEntityCreated");
    BindCallback(host, assemblyPath, managed.onEntitySpawned, L"OnEntitySpawned");
    BindCallback(host, assemblyPath, managed.onEntityDeleted, L"OnEntityDeleted");
    BindCallback(host, assemblyPath, managed.onPrecacheResources, L"OnPrecacheResources");
    BindCallback(host, assemblyPath, managed.onEntityStartTouch, L"OnEntityStartTouch");
    BindCallback(host, assemblyPath, managed.onEntityEndTouch, L"OnEntityEndTouch");
    BindCallback(host, assemblyPath, managed.onEntityFireOutput, L"OnEntityFireOutput");
    BindCallback(host, assemblyPath, managed.onEntityAcceptInput, L"OnEntityAcceptInput");
    BindCallback(host, assemblyPath, managed.onProcessUsercmds, L"OnProcessUsercmds");
    BindCallback(host, assemblyPath, managed.onAbilityAttempt, L"OnAbilityAttempt");
    BindCallback(host, assemblyPath, g_ManagedConCommandDispatch, L"OnConCommandDispatch");
    BindCallback(host, assemblyPath, managed.onAddModifier, L"OnAddModifier");
    BindCallback(host, assemblyPath, managed.onSignonState, L"OnSignonState");
    BindCallback(host, assemblyPath, managed.onCheckTransmit, L"OnCheckTransmit");
}
