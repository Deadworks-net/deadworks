#include "ManagedCallbacks.hpp"
#include "NativeCallbacks.hpp"
#include "Deadworks.hpp"
#include "../Hosting/DotNetHost.hpp"

using namespace deadworks;

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
        g_Log->Error("Failed to initialize .NET runtime");
        return;
    }

    g_Log->Info(".NET runtime initialized");

    using InitializeFn = void(CORECLR_DELEGATE_CALLTYPE *)(NativeCallbacks *);
    auto initialize = host.GetManagedFunction<InitializeFn>(
        assemblyPath, kManagedTypeName, L"Initialize");

    if (!initialize) {
        g_Log->Error("Failed to get managed EntryPoint.Initialize");
        return;
    }

    NativeCallbacks callbacks{};
    PopulateNativeCallbacks(callbacks);
    initialize(&callbacks);
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
    BindCallback(host, assemblyPath, managed.onEntityAcceptInput, L"OnEntityAcceptInput");
    BindCallback(host, assemblyPath, managed.onEntityAcceptInputPost, L"OnEntityAcceptInputPost");
    BindCallback(host, assemblyPath, managed.onEntityFireOutput, L"OnEntityFireOutput");
    BindCallback(host, assemblyPath, managed.onEntityFireOutputPost, L"OnEntityFireOutputPost");
    BindCallback(host, assemblyPath, managed.onProcessUsercmds, L"OnProcessUsercmds");
    BindCallback(host, assemblyPath, managed.onFastProcessUsercmds, L"OnFastProcessUsercmds");
    BindCallback(host, assemblyPath, managed.onUsercmdTrigger, L"OnUsercmdTrigger");
    BindCallback(host, assemblyPath, managed.onFastNetMessage, L"OnFastNetMessage");
    BindCallback(host, assemblyPath, managed.onAbilityAttempt, L"OnAbilityAttempt");
    BindCallback(host, assemblyPath, g_ManagedConCommandDispatch, L"OnConCommandDispatch");
    BindCallback(host, assemblyPath, managed.onAddModifier, L"OnAddModifier");
    BindCallback(host, assemblyPath, managed.onCheckTransmit, L"OnCheckTransmit");
    BindCallback(host, assemblyPath, managed.onPawnHeroInitialized, L"OnPawnHeroInitialized");
}
