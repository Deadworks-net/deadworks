#include "Deadworks.hpp"
#include "NativeCallbacks.hpp"
#include "NativeHero.hpp"
#include "ManagedCallbacks.hpp"
#include "NetMessageVisitorRuntime.hpp"

#include "Hooks/CoreHooks.hpp"
#include "Hooks/Source2GameClients.hpp"
#include "Hooks/Source2Server.hpp"
#include "Hooks/CServerSideClientBase.hpp"
#include "Hooks/NetworkServerService.hpp"
#include "Hooks/CBaseEntity.hpp"
#include "Hooks/GameEvents.hpp"
#include "Hooks/PostEventAbstract.hpp"
#include "Hooks/CCitadelPlayerPawn.hpp"
#include "Hooks/BuildGameSessionManifest.hpp"
#include "Hooks/CCitadelPlayerController.hpp"
#include "Hooks/EntityIO.hpp"
#include "Hooks/TraceShape.hpp"
#include "Hooks/ProcessUsercmds.hpp"
#include "Hooks/AbilityThink.hpp"
#include "Hooks/AddModifier.hpp"
#include "Hooks/ReplyConnection.hpp"
#include "Hooks/CheckTransmit.hpp"
#include "Hooks/InitializeHeroOnPawn.hpp"
#include "A2SPatch.hpp"

#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CBaseEntity.hpp"
#include "../SDK/CCitadelPlayerController.hpp"
#include "../SDK/CEntitySystem.hpp"
#include "../Utils/String.hpp"
#include "../SDK/Util.hpp"

#include <tier1/convar.h>

#include <netmessages.h>
#include <netmessages.pb.h>
#include <serversideclient.h>
#include <irecipientfilter.h>
#include <igameevents.h>
#include <igameeventsystem.h>

#include <icvar.h>
#include <entity2/entitysystem.h>
#include <iservernetworkable.h>
#include <interfaces/interfaces.h>
#include <soundsystem/isoundsystem.h>

IGameEventSystem *g_pGameEventSystem = nullptr;

namespace deadworks {

class CEntityListener : public IEntityListener {
public:
    void OnEntityCreated(CEntityInstance *pEntity) override { g_Deadworks.OnEntityCreated(pEntity); }
    void OnEntitySpawned(CEntityInstance *pEntity) override { g_Deadworks.OnEntitySpawned(pEntity); }
    void OnEntityDeleted(CEntityInstance *pEntity) override { g_Deadworks.OnEntityDeleted(pEntity); }
};

static CEntityListener g_EntityListener;

template <typename Fn>
static void HookInline(safetyhook::InlineHook &hook, const char *name, Fn detour) {
    auto offset = MemoryDataLoader::Get().GetOffset(name).value();
    hook = safetyhook::create_inline(offset, detour);
    g_Log->Info("Hooked {}", name);
}

void Deadworks::InitFromAppSystem(CAppSystemDict *pAppSystem) {
    // server must be loaded before logging
    Module server("../../citadel/bin/win64/server.dll");
    GetInterfaceFactories();

    g_Log = std::make_unique<S2Logger>("deadworks");
    g_Log->Info("Log Startup");
    g_Log->Info("InitFromAppSystem");

    hooks::g_ServerCreateInterface = safetyhook::create_inline(InterfaceFactories.server, hooks::Hook_ServerCreateInterface);

    g_pNetworkServerService = reinterpret_cast<INetworkServerService *>(InterfaceFactories.engine2(NETWORKSERVERSERVICE_INTERFACE_VERSION, nullptr));
    g_pEngineServer = reinterpret_cast<IVEngineServer2 *>(InterfaceFactories.engine2(SOURCE2ENGINETOSERVER_INTERFACE_VERSION, nullptr));

    if (!g_pNetworkServerService) {
        g_Log->Error("Failed to load INetworkServerService. Abandoning ship!");
        return;
    }

    if (!g_pEngineServer) {
        g_Log->Error("Failed to load IVEngineServer2. Abandoning ship!");
        return;
    }
}

void Deadworks::PostInit() {
    g_Log->Info("PostInit");

    g_pSource2Server = reinterpret_cast<ISource2Server *>(InterfaceFactories.server(SOURCE2SERVER_INTERFACE_VERSION, nullptr));
    g_pSource2GameClients = reinterpret_cast<ISource2GameClients *>(InterfaceFactories.server(SOURCE2GAMECLIENTS_INTERFACE_VERSION, nullptr));
    g_pSource2GameEntities = reinterpret_cast<ISource2GameEntities *>(InterfaceFactories.server(SOURCE2GAMEENTITIES_INTERFACE_VERSION, nullptr));
    g_pGameResourceServiceServer = reinterpret_cast<IGameResourceService *>(InterfaceFactories.engine2(GAMERESOURCESERVICESERVER_INTERFACE_VERSION, nullptr));
    g_pSchemaSystem = reinterpret_cast<ISchemaSystem *>(InterfaceFactories.schemasystem(SCHEMASYSTEM_INTERFACE_VERSION, nullptr));
    g_pNetworkMessages = reinterpret_cast<INetworkMessages *>(InterfaceFactories.networksystem(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    g_pGameEventSystem = reinterpret_cast<IGameEventSystem *>(InterfaceFactories.engine2(GAMEEVENTSYSTEM_INTERFACE_VERSION, nullptr));
    g_pCVar = reinterpret_cast<ICvar *>(InterfaceFactories.tier0(CVAR_INTERFACE_VERSION, nullptr));
    g_pFullFileSystem = reinterpret_cast<IFileSystem *>(InterfaceFactories.filesystem_stdio(FILESYSTEM_INTERFACE_VERSION, nullptr));
    g_pSoundSystem = reinterpret_cast<ISoundSystem *>(InterfaceFactories.soundsystem(SOUNDSYSTEM_INTERFACE_VERSION, nullptr));

    if (!g_pSoundSystem) {
		g_Log->Error("Failed to load ISoundSystem. Abandoning ship!");
		return;
    }

    if (!g_pSource2Server) {
        g_Log->Error("Failed to load ISource2Server. Abandoning ship!");
        return;
    }

    if (!g_pSource2GameClients) {
        g_Log->Error("Failed to load ISource2GameClients. Abandoning ship!");
        return;
    }

    if (!g_pGameResourceServiceServer) {
        g_Log->Error("Failed to load IGameResourceServiceServer. Abandoning ship!");
        return;
    }

    if (!g_pSchemaSystem) {
        g_Log->Error("Failed to load ISchemaSystem. Abandoning ship!");
        return;
    }

    if (!g_pNetworkMessages) {
        g_Log->Error("Failed to load INetworkMessages. Abandoning ship!");
        return;
    }

    if (!g_pGameEventSystem) {
        g_Log->Error("Failed to load IGameEventSystem. Abandoning ship!");
        return;
    }

    if (!g_pCVar) {
        g_Log->Error("Failed to load ICvar. Abandoning ship!");
        return;
    }

	if (!g_pFullFileSystem) {
		g_Log->Error("Failed to load IFileSystem. Abandoning ship!");
		return;
	}

    ConVar_Register(FCVAR_RELEASE | FCVAR_CLIENT_CAN_EXECUTE | FCVAR_GAMEDLL);

    HookInline(hooks::g_CGCClientSystem_OnServerVersionCheck,
               "CGCClientSystem::OnServerVersionCheck",
               &hooks::Hook_CGCClientSystem_OnServerVersionCheck);

    // VMT hooks - these use virtual indices, not signatures
    auto &mem = MemoryDataLoader::Get();

    hooks::g_Source2ServerVmt = safetyhook::create_vmt(g_pSource2Server);
    hooks::g_Source2Server_ApplyGameSettings = safetyhook::create_vm(hooks::g_Source2ServerVmt, mem.GetVirtual("ISource2Server::ApplyGameSettings").value(), &hooks::Source2ServerHook::Hook_ApplyGameSettings);
    hooks::g_Source2Server_GameFrame = safetyhook::create_vm(hooks::g_Source2ServerVmt, mem.GetVirtual("ISource2Server::GameFrame").value(), &hooks::Source2ServerHook::Hook_GameFrame);

    hooks::g_Source2GameClientsVmt = safetyhook::create_vmt(g_pSource2GameClients);
    hooks::g_Source2GameClients_ClientPutInServer = safetyhook::create_vm(hooks::g_Source2GameClientsVmt, mem.GetVirtual("ISource2GameClients::ClientPutInServer").value(), &hooks::Source2GameClientsHook::Hook_ClientPutInServer);
    hooks::g_Source2GameClients_ClientConnect = safetyhook::create_vm(hooks::g_Source2GameClientsVmt, mem.GetVirtual("ISource2GameClients::ClientConnect").value(), &hooks::Source2GameClientsHook::Hook_ClientConnect);
    hooks::g_Source2GameClients_ClientDisconnect = safetyhook::create_vm(hooks::g_Source2GameClientsVmt, mem.GetVirtual("ISource2GameClients::ClientDisconnect").value(), &hooks::Source2GameClientsHook::Hook_ClientDisconnect);

    hooks::g_NetworkServerServiceVmt = safetyhook::create_vmt(g_pNetworkServerService);
    hooks::g_NetworkServerService_StartupServer = safetyhook::create_vm(hooks::g_NetworkServerServiceVmt, mem.GetVirtual("INetworkServerService::StartupServer").value(), &hooks::NetworkServerServiceHook::Hook_StartupServer);

    HookInline(hooks::g_CServerSideClientBase_FilterMessage,
               "CServerSideClientBase::FilterMessage",
               &hooks::Hook_CServerSideClientBase_FilterMessage);
    HookInline(hooks::g_CServerSideClientBase_IsReservedSlot,
               "CServerSideClientBase::IsReservedSlot",
               &hooks::Hook_CServerSideClientBase_IsReservedSlot);
    HookInline(hooks::g_ReplyConnection,
               "CNetworkGameServerBase::ReplyConnection",
               &hooks::Hook_ReplyConnection);
    HookInline(hooks::g_CBaseEntity_TakeDamageOld,
               "CBaseEntity::TakeDamageOld",
               &hooks::Hook_CBaseEntity_TakeDamageOld);
    HookInline(hooks::g_CCitadelPlayerPawn_ModifyCurrency,
               "CCitadelPlayerPawn::ModifyCurrency",
               &hooks::Hook_CCitadelPlayerPawn_ModifyCurrency);
    HookInline(hooks::g_CCitadelPlayerController_ClientConCommand,
               "CCitadelPlayerController::ClientConCommand",
               &hooks::Hook_CCitadelPlayerController_ClientConCommand);

    // Resolve IGameEventManager2 from a known xref
    {
        auto xref = MemoryDataLoader::Get().GetOffset("g_GameEventManager").value();
        auto disp = *reinterpret_cast<int32_t *>(xref + 9);
        g_pGameEventManager2 = *reinterpret_cast<IGameEventManager2 **>(xref + 13 + disp);

        if (g_pGameEventManager2) {
            g_Log->Info("IGameEventManager2 resolved: {:p}", static_cast<void *>(g_pGameEventManager2));
            hooks::g_GameEventManager2Vmt = safetyhook::create_vmt(g_pGameEventManager2);
            hooks::g_GameEventManager2_FireEvent = safetyhook::create_vm(hooks::g_GameEventManager2Vmt, mem.GetVirtual("IGameEventManager2::FireEvent").value(), &hooks::Hook_GameEventManager2_FireEvent);
        } else {
            g_Log->Error("Failed to dereference g_GameEventManager pointer");
        }
    }

    hooks::g_GameEventSystemVmt = safetyhook::create_vmt(g_pGameEventSystem);
    hooks::g_PostEventAbstract = safetyhook::create_vm(hooks::g_GameEventSystemVmt, mem.GetVirtual("IGameEventSystem::PostEventAbstract").value(), &hooks::Hook_PostEventAbstract);

    {
        int idx = mem.GetVirtual("ISource2GameEntities::CheckTransmit").value();
        auto **vtable = *reinterpret_cast<void ***>(g_pSource2GameEntities);
        hooks::g_Source2GameEntities_CheckTransmit_Original = reinterpret_cast<hooks::CheckTransmitFn>(vtable[idx]);

        DWORD oldProtect = 0;
        if (VirtualProtect(&vtable[idx], sizeof(void *), PAGE_READWRITE, &oldProtect)) {
            vtable[idx] = reinterpret_cast<void *>(&hooks::Hook_CheckTransmit);
            VirtualProtect(&vtable[idx], sizeof(void *), oldProtect, &oldProtect);
            g_Log->Info("Patched ISource2GameEntities vtable[{}] for CheckTransmit", idx);
        } else {
            g_Log->Error("VirtualProtect failed on ISource2GameEntities vtable - CheckTransmit unavailable");
            hooks::g_Source2GameEntities_CheckTransmit_Original = nullptr;
        }
    }

    HookInline(hooks::g_BuildGameSessionManifest,
               "CCitadelGameRules::BuildGameSessionManifest",
               &hooks::Hook_BuildGameSessionManifest);
    HookInline(hooks::g_TraceShape,
               "TraceShape",
               &hooks::Hook_TraceShape);

    // Touch hooks (StartTouch / EndTouch) are initialized lazily in OnEntityCreated
    // because we need an entity vtable to resolve the virtual function addresses.

    HookInline(hooks::g_CEntityInstance_AcceptInput,
               "CEntityInstance::AcceptInput",
               &hooks::Hook_CEntityInstance_AcceptInput);
    HookInline(hooks::g_CEntityIOOutput_FireOutputInternal,
               "CEntityIOOutput::FireOutputInternal",
               &hooks::Hook_CEntityIOOutput_FireOutputInternal);
    HookInline(hooks::g_ProcessUsercmds,
               "CBasePlayerController::ProcessUsercmds",
               &hooks::Hook_ProcessUsercmds);
    HookInline(hooks::g_AbilityThink,
               "CCitadelPlayerPawn::AbilityThink",
               &hooks::Hook_AbilityThink);
    HookInline(hooks::g_CModifierProperty_AddModifier,
               "CModifierProperty::AddModifier",
               &hooks::Hook_CModifierProperty_AddModifier);
    HookInline(hooks::g_InitializeHeroOnPawn,
               "CCitadelPlayerPawn::InitializeHeroOnPawn",
               &hooks::Hook_InitializeHeroOnPawn);

    // Enable A2S_INFO responses on community servers
    A2SPatch::Apply();

    // Resolve statics needed by Native* callbacks
    ResolveNativeStatics();
}

void Deadworks::On_ISource2Server_ApplyGameSettings() {
    if (!m_dotnetInitialized) {
        m_dotnetInitialized = true;
        InitializeManagedCallbacks(m_dotnetHost, m_managed);
    }
}

void Deadworks::On_StartupServer(const char *pszMapName) {
    g_Log->Info("StartupServer (map: {})", pszMapName ? pszMapName : "");

    // Register entity listener
    GameEntitySystem()->AddListenerEntity(&g_EntityListener);

    // Unhide all cvars and concommands by raw index iteration
    if (g_pCVar) {
        constexpr uint64 flagsToRemove = FCVAR_HIDDEN | FCVAR_DEVELOPMENTONLY | FCVAR_DEFENSIVE;
        int unhiddenCvars = 0;
        int unhiddenCmds = 0;

        {
            ConVarRef invalidRef{};
            auto *invalidData = g_pCVar->GetConVarData(invalidRef);
            for (uint16 i = 0;; i++) {
                ConVarRef ref(i);
                auto *data = g_pCVar->GetConVarData(ref);
                if (data == invalidData)
                    break;
                if (data->GetFlags() & flagsToRemove) {
                    data->RemoveFlags(flagsToRemove);
                    unhiddenCvars++;
                }
            }
        }

        {
            ConCommandRef invalidRef{};
            auto *invalidData = g_pCVar->GetConCommandData(invalidRef);
            for (uint16 i = 0;; i++) {
                ConCommandRef ref(i);
                auto *data = g_pCVar->GetConCommandData(ref);
                if (data == invalidData)
                    break;
                if (data->GetFlags() & flagsToRemove) {
                    data->RemoveFlags(flagsToRemove);
                    unhiddenCmds++;
                }
            }
        }

        g_Log->Info("Unhid {} cvars and {} concommands", unhiddenCvars, unhiddenCmds);
    }

    if (m_managed.onStartupServer)
        m_managed.onStartupServer(pszMapName ? pszMapName : "");
}

bool Deadworks::OnPre_CBaseEntity_TakeDamageOld(CBaseEntity *entity, CTakeDamageInfo *info, CTakeDamageResult *result) {
    if (m_managed.onTakeDamageOld)
        return m_managed.onTakeDamageOld(entity, info, result);
    return false;
}

bool Deadworks::OnPre_CCitadelPlayerPawn_ModifyCurrency(void *pawn, ECurrencyType nCurrencyType, int32_t nAmount,
                                                        ECurrencySource nSource, bool bSilent, bool bForceGain, bool bSpendOnly,
                                                        void *pSourceAbility, void *pSourceEntity) {
    if (m_managed.onModifyCurrency)
        return m_managed.onModifyCurrency(pawn, static_cast<uint32_t>(nCurrencyType), nAmount,
                                          static_cast<uint32_t>(nSource), bSilent ? 1 : 0, bForceGain ? 1 : 0,
                                          bSpendOnly ? 1 : 0, pSourceAbility, pSourceEntity);
    return false;
}

bool Deadworks::OnPre_ClientConCommand(void *controller, void *args) {
    if (!m_managed.onClientConCommand)
        return false;

    auto *cmd = reinterpret_cast<CCommand *>(args);
    int argc = cmd->ArgC();
    const char *command = argc > 0 ? (*cmd)[0] : "";
    const char **argv = cmd->ArgV();

    int result = m_managed.onClientConCommand(controller, command, argc, argv);
    return result >= 1;
}

int Deadworks::OnPre_GameEvent(const char *eventName, void *eventPtr) {
    if (m_managed.onGameEvent)
        return m_managed.onGameEvent(eventName, eventPtr);
    return 0;
}

bool Deadworks::OnPre_PostEventAbstract(int msgId, const CNetMessage *pData, uint64 *clientsMask) {
    if (!pData)
        return false;

    ProcessFastNetMessage(NetMessageDirection::Outgoing, -1, msgId, pData, clientsMask ? *clientsMask : 0);
    LogNetMessageVisitorStatsIfDue();

    if (!m_managed.onNetMessageOutgoing || !clientsMask)
        return false;

    static thread_local uint8_t outBuf[65536];

    if (msgId == svc_UserMessage && HasAnyUserMessageSerializedInterest()) {
        auto *pb = pData->AsMessageLite();
        auto *user = pb ? static_cast<const CSVCMsg_UserMessage *>(pb) : nullptr;
        if (user && user->has_msg_type() && HasUserMessageSerializedInterest(user->msg_type())) {
            const std::string &payload = user->msg_data();
            int outLen = -1;
            uint64 outRecipientMask = *clientsMask;
            int result = m_managed.onNetMessageOutgoing(
                user->msg_type(), reinterpret_cast<const uint8_t *>(payload.data()), static_cast<int>(payload.size()),
                *clientsMask, outBuf, &outLen, &outRecipientMask);

            *clientsMask = outRecipientMask;
            if (outLen >= 0)
                const_cast<CSVCMsg_UserMessage *>(user)->set_msg_data(outBuf, outLen);

            if (result >= 1)
                return true;
            if (!HasNetMessageSerializedInterest(NetMessageDirection::Outgoing, msgId))
                return false;
        }
    }

    if (!HasNetMessageSerializedInterest(NetMessageDirection::Outgoing, msgId))
        return false;

    auto *pb = pData->AsMessageLite();
    if (!pb)
        return false;

    int size = static_cast<int>(pb->ByteSizeLong());
    if (size <= 0)
        return false;

    std::vector<uint8_t> buf(size);
    if (!pb->SerializeToArray(buf.data(), size))
        return false;

    int outLen = -1;
    uint64 outRecipientMask = *clientsMask;

    int result = m_managed.onNetMessageOutgoing(msgId, buf.data(), size, *clientsMask, outBuf, &outLen, &outRecipientMask);

    *clientsMask = outRecipientMask;

    if (outLen >= 0) {
        auto *mutablePb = const_cast<google::protobuf::MessageLite *>(pb);
        mutablePb->ParseFromArray(outBuf, outLen);
    }

    return result >= 1;
}

static constexpr ptrdiff_t kServerAddonsOffset = 0x158;

void Deadworks::OnPre_ReplyConnection(void *server, CServerSideClientBase *client) {
    if (m_desiredServerAddons.empty())
        return;

    // Save the engine's current addons value
    auto *pAddons = reinterpret_cast<CUtlString *>(reinterpret_cast<uintptr_t>(server) + kServerAddonsOffset);
    m_savedServerAddons = pAddons->Get();

    // Inject ours
    *pAddons = m_desiredServerAddons.c_str();
    g_Log->Info("[ReplyConnection] Injected addons='{}' (was '{}')", m_desiredServerAddons, m_savedServerAddons);
}

void Deadworks::OnPost_ReplyConnection(void *server, CServerSideClientBase *client) {
    if (m_desiredServerAddons.empty())
        return;

    // Restore original
    auto *pAddons = reinterpret_cast<CUtlString *>(reinterpret_cast<uintptr_t>(server) + kServerAddonsOffset);
    *pAddons = m_savedServerAddons.c_str();
}

void Deadworks::OnEntityCreated(CEntityInstance *pEntity) {
    // Lazily initialize touch hooks from the first entity's vtable
    if (!m_touchHooksInitialized && pEntity) {
        m_touchHooksInitialized = true;
        auto &mem = MemoryDataLoader::Get();

        auto *vtable = *reinterpret_cast<void ***>(pEntity);
        auto startTouchIdx = mem.GetVirtual("CBaseEntity::StartTouch").value();
        auto endTouchIdx = mem.GetVirtual("CBaseEntity::EndTouch").value();

        hooks::g_CBaseEntity_StartTouch = safetyhook::create_inline(vtable[startTouchIdx], &hooks::Hook_CBaseEntity_StartTouch);
        g_Log->Info("Hooked CBaseEntity::StartTouch (vtable index {})", startTouchIdx);

        hooks::g_CBaseEntity_EndTouch = safetyhook::create_inline(vtable[endTouchIdx], &hooks::Hook_CBaseEntity_EndTouch);
        g_Log->Info("Hooked CBaseEntity::EndTouch (vtable index {})", endTouchIdx);
    }

    if (m_managed.onEntityCreated && pEntity)
        m_managed.onEntityCreated(pEntity);
}

void Deadworks::OnEntitySpawned(CEntityInstance *pEntity) {
    if (m_managed.onEntitySpawned && pEntity)
        m_managed.onEntitySpawned(pEntity);
}

void Deadworks::OnEntityDeleted(CEntityInstance *pEntity) {
    if (m_managed.onEntityDeleted && pEntity)
        m_managed.onEntityDeleted(pEntity);
}

void Deadworks::OnBuildGameSessionManifest(void *manifest) {
    if (!m_managed.onPrecacheResources || !manifest)
        return;

    g_pCurrentManifest = manifest;
    m_managed.onPrecacheResources();
    g_pCurrentManifest = nullptr;
}

void Deadworks::On_ISource2Server_GameFrame(bool simulating, bool bFirstTick, bool bLastTick) {
    // Poll for fully-connected transitions
    if (m_managed.onClientFullConnect) {
        auto *server = g_pNetworkServerService->GetIGameServer();
        for (int i = 0; i < 64; ++i) {
            if (m_clientFullyConnected[i])
                continue;
            auto *client = server->GetClientBySlot(CPlayerSlot(i));
            if (client && client->IsInGame()) {
                m_clientFullyConnected[i] = true;
                m_managed.onClientFullConnect(i);
            }
        }
    }

    if (m_managed.onGameFrame)
        m_managed.onGameFrame(simulating ? 1 : 0, bFirstTick ? 1 : 0, bLastTick ? 1 : 0);
}

void Deadworks::On_ISource2GameClients_ClientPutInServer(CPlayerSlot slot, const char *pszName, int type, uint64 xuid) {
    if (m_managed.onClientPutInServer) {
        int wlen = MultiByteToWideChar(CP_UTF8, 0, pszName, -1, nullptr, 0);
        std::wstring wname(wlen - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, pszName, -1, wname.data(), wlen);

        m_managed.onClientPutInServer(
            slot.Get(),
            reinterpret_cast<const char16_t *>(wname.c_str()),
            xuid,
            type != 0 ? 1 : 0);
    }
}

bool Deadworks::On_ISource2GameClients_ClientConnect(CPlayerSlot slot, const char *pszName, uint64 xuid, const char *pszNetworkID, bool unk1, CBufferString *pRejectReason) {
    m_clientFullyConnected[slot.Get()] = false;

    if (m_managed.onClientConnect) {
        // Convert name to UTF-16
        int nameLen = MultiByteToWideChar(CP_UTF8, 0, pszName, -1, nullptr, 0);
        std::wstring wname(nameLen - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, pszName, -1, wname.data(), nameLen);

        // Strip port from networkID (format is "ip:port")
        std::string ip(pszNetworkID ? pszNetworkID : "");
        auto colon = ip.find(':');
        if (colon != std::string::npos)
            ip.resize(colon);

        // Convert IP to UTF-16
        int ipLen = MultiByteToWideChar(CP_UTF8, 0, ip.c_str(), -1, nullptr, 0);
        std::wstring wip(ipLen - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, ip.c_str(), -1, wip.data(), ipLen);

        uint8_t allowed = m_managed.onClientConnect(
            slot.Get(),
            reinterpret_cast<const char16_t *>(wname.c_str()),
            xuid,
            reinterpret_cast<const char16_t *>(wip.c_str()));

        if (!allowed)
            return false;
    }

    return true;
}

void Deadworks::On_ISource2GameClients_ClientDisconnect(CPlayerSlot slot, ENetworkDisconnectionReason reason, const char *pszName, uint64 xuid, const char *pszNetworkID) {
    m_clientFullyConnected[slot.Get()] = false;
    if (m_managed.onClientDisconnect)
        m_managed.onClientDisconnect(slot.Get(), static_cast<int>(reason));
}

std::optional<bool> Deadworks::OnPre_CServerSideClientBase_FilterMessage(INetworkMessageProcessingPreFilter *thisptr, const CNetMessage *pData) {
    if (!pData)
        return std::nullopt;

    auto *client = static_cast<CServerSideClientBase *>(thisptr);
    auto *serializer = pData->GetSerializerPB();
    if (!serializer)
        return std::nullopt;

    auto *info = serializer->GetNetMessageInfo();
    if (!info)
        return std::nullopt;

    int msgId = info->m_MessageId;
    int senderSlot = client ? client->GetPlayerSlot().Get() : -1;

    ProcessFastNetMessage(NetMessageDirection::Incoming, senderSlot, msgId, pData, 0);
    LogNetMessageVisitorStatsIfDue();

    if (m_managed.onNetMessageIncoming && HasNetMessageSerializedInterest(NetMessageDirection::Incoming, msgId)) {
        auto *pb = pData->AsMessageLite();
        if (pb) {
            int size = static_cast<int>(pb->ByteSizeLong());
            if (size > 0) {
                std::vector<uint8_t> buf(size);
                if (pb->SerializeToArray(buf.data(), size)) {
                    int result = m_managed.onNetMessageIncoming(senderSlot, msgId, buf.data(), size);
                    if (result >= 1) // Stop/block
                        return true;
                }
            }
        }
    }

    return std::nullopt;
}

void Deadworks::OnStartTouch(CBaseEntity *entity, CBaseEntity *other) {
    if (m_managed.onEntityStartTouch && entity && other)
        m_managed.onEntityStartTouch(entity, other);
}

void Deadworks::OnEndTouch(CBaseEntity *entity, CBaseEntity *other) {
    if (m_managed.onEntityEndTouch && entity && other)
        m_managed.onEntityEndTouch(entity, other);
}

int Deadworks::OnEntityAcceptInputPre(const char *className, const char *inputName,
                                       void *entity, void *activator, void *caller, void *variantValue) {
    if (m_managed.onEntityAcceptInput)
        return m_managed.onEntityAcceptInput(className ? className : "", inputName ? inputName : "",
                                              entity, activator, caller, variantValue);
    return 0;
}

void Deadworks::OnEntityAcceptInputPost(const char *className, const char *inputName,
                                         void *entity, void *activator, void *caller, void *variantValue) {
    if (m_managed.onEntityAcceptInputPost)
        m_managed.onEntityAcceptInputPost(className ? className : "", inputName ? inputName : "",
                                           entity, activator, caller, variantValue);
}

int Deadworks::OnEntityFireOutputPre(const char *callerClass, const char *outputName,
                                      void *activator, void *caller, const void *variantValue, float delay) {
    if (m_managed.onEntityFireOutput)
        return m_managed.onEntityFireOutput(callerClass ? callerClass : "", outputName ? outputName : "",
                                             activator, caller, variantValue, delay);
    return 0;
}

void Deadworks::OnEntityFireOutputPost(const char *callerClass, const char *outputName,
                                        void *activator, void *caller, const void *variantValue, float delay) {
    if (m_managed.onEntityFireOutputPost)
        m_managed.onEntityFireOutputPost(callerClass ? callerClass : "", outputName ? outputName : "",
                                          activator, caller, variantValue, delay);
}

void Deadworks::OnPre_ProcessUsercmds(int playerSlot, const uint8_t *batchBytes, int batchLen, int numCmds, bool paused, float margin, uint8_t *outBytes, int *outLen) {
    if (m_managed.onProcessUsercmds)
        m_managed.onProcessUsercmds(playerSlot, batchBytes, batchLen, numCmds, paused ? 1 : 0, margin, outBytes, outLen);
}

void Deadworks::OnFast_ProcessUsercmds(int playerSlot, const FastUsercmdNative *cmds, int numCmds, bool paused, float margin) {
    if (m_managed.onFastProcessUsercmds)
        m_managed.onFastProcessUsercmds(playerSlot, cmds, numCmds, paused ? 1 : 0, margin);
}

void Deadworks::OnUsercmdTrigger(int playerSlot, const FastUsercmdNative *cmd, uint64_t pressedButtons, uint64_t triggerButtons, bool paused, float margin) {
    if (m_managed.onUsercmdTrigger)
        m_managed.onUsercmdTrigger(playerSlot, cmd, pressedButtons, triggerButtons, paused ? 1 : 0, margin);
}

bool Deadworks::HasFastNetMessageCallback() const {
    return m_managed.onFastNetMessage != nullptr;
}

void Deadworks::OnFastNetMessage(int32_t direction, int32_t endpointSlot, int32_t msgId, uint64_t recipientMask,
                                 int32_t userMessageType, uint8_t hasUserMessageType,
                                 int32_t pauseType, int32_t pauseGroup, uint8_t hasPauseRequest,
                                 uint8_t paused, uint8_t hasPauseState) {
    if (m_managed.onFastNetMessage) {
        m_managed.onFastNetMessage(direction, endpointSlot, msgId, recipientMask,
                                   userMessageType, hasUserMessageType,
                                   pauseType, pauseGroup, hasPauseRequest,
                                   paused, hasPauseState);
    }
}

uint64_t Deadworks::OnPre_AbilityThink(int playerSlot, void *pawnEntity, uint64_t heldButtons, uint64_t changedButtons, uint64_t scrollButtons, uint64_t *outForcedButtons) {
    if (m_managed.onAbilityAttempt)
        return m_managed.onAbilityAttempt(playerSlot, pawnEntity, heldButtons, changedButtons, scrollButtons, outForcedButtons);
    return 0;
}

bool Deadworks::OnPre_AddModifier(void *modifierProp, CBaseEntity *&pCaster, uint32_t &hAbility, int &iTeam,
                                  void *vdata, void *pParams, void *pKV) {
    if (m_managed.onAddModifier) {
        void *casterPtr = pCaster;
        int result = m_managed.onAddModifier(modifierProp, &casterPtr, &hAbility, &iTeam, vdata, pParams, pKV);
        pCaster = static_cast<CBaseEntity *>(casterPtr);
        return result >= 1;
    }
    return false;
}

void Deadworks::OnPost_CheckTransmit(CCheckTransmitInfo **ppInfoList, int nInfoCount) {
    if (!m_managed.onCheckTransmit)
        return;

    for (int i = 0; i < nInfoCount; i++) {
        auto *pInfo = ppInfoList[i];
        if (!pInfo || !pInfo->m_pTransmitEntity)
            continue;

        int playerSlot = pInfo->m_nPlayerSlot.Get();
        m_managed.onCheckTransmit(playerSlot, pInfo->m_pTransmitEntity);
    }
}

void Deadworks::OnPost_InitializeHeroOnPawn(void *pawn) {
    if (m_managed.onPawnHeroInitialized && pawn)
        m_managed.onPawnHeroInitialized(pawn);
}

void Deadworks::GetInterfaceFactories() {
    Module server("server");
    Module engine2("engine2");
    Module schemasystem("schemasystem");
    Module networksystem("networksystem");
    Module tier0("tier0");
    Module filesystem_stdio("filesystem_stdio");
    Module soundsystem("soundsystem");

    InterfaceFactories.server = server.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.engine2 = engine2.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.schemasystem = schemasystem.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.networksystem = networksystem.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.tier0 = tier0.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.filesystem_stdio = filesystem_stdio.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.soundsystem = soundsystem.GetSymbol<CreateInterfaceFn>("CreateInterface");
}
} // namespace deadworks
