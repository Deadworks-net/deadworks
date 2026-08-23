#pragma once

#include <iappsystem.h>

#include "../Logging/S2Logger.hpp"
#include "../Lib/Module.hpp"
#include "../Hosting/DotNetHost.hpp"

#include "../SDK/Core.hpp"
#include "../SDK/Enums.hpp"

#include "ManagedCallbacks.hpp"

class ISource2Server;
class ISource2GameClients;
class INetworkServerService;
class IVEngineServer2;
class CServerSideClientBase;
class CNetMessage;
class INetworkChannelNotify;
class INetworkMessageProcessingPreFilter;
class IGameEventSystem;
class CBaseEntity;
class CEntityInstance;
class CTakeDamageResult;
class CTakeDamageInfo;
class CCheckTransmitInfo;

extern IGameEventSystem *g_pGameEventSystem;

namespace deadworks {

class Deadworks {
public:
    void InitFromAppSystem(CAppSystemDict *pAppSystem);

    void PostInit();

    // ISource2Server
    void On_ISource2Server_ApplyGameSettings();
    void On_ISource2Server_GameFrame(bool simulating, bool bFirstTick, bool bLastTick);
    // ISource2GameClients
    void On_ISource2GameClients_ClientPutInServer(CPlayerSlot slot, const char *pszName, int type, uint64 xuid);
    bool On_ISource2GameClients_ClientConnect(CPlayerSlot slot, const char *pszName, uint64 xuid, const char *pszNetworkID, bool unk1, CBufferString *pRejectReason);
    void On_ISource2GameClients_ClientDisconnect(CPlayerSlot slot, ENetworkDisconnectionReason reason, const char *pszName, uint64 xuid, const char *pszNetworkID);
    // INetworkServerService
    void On_StartupServer(const char *pszMapName);
    // CServerSideClientBase
    std::optional<bool> OnPre_CServerSideClientBase_FilterMessage(INetworkMessageProcessingPreFilter *thisptr, const CNetMessage *pData);
    // CBaseEntity
    bool OnPre_CBaseEntity_TakeDamageOld(CBaseEntity *entity, CTakeDamageInfo *info, CTakeDamageResult *result);
    // CCitadelPlayerPawn
    bool OnPre_CCitadelPlayerPawn_ModifyCurrency(void *pawn, ECurrencyType nCurrencyType, int32_t nAmount,
                                                  ECurrencySource nSource, bool bSilent, bool bForceGain, bool bSpendOnly,
                                                  void *pSourceAbility, void *pSourceEntity);
    // Game Events
    int OnPre_GameEvent(const char *eventName, void *eventPtr);
    // Net Messages (outgoing - broadcast via game event system)
    bool OnPre_PostEventAbstract(int msgId, const CNetMessage *pData, uint64 *clientsMask);
    // ReplyConnection - temporarily inject addons into the server object
    void OnPre_ReplyConnection(void *server, CServerSideClientBase *client);
    void OnPost_ReplyConnection(void *server, CServerSideClientBase *client);
    // Called from NativeSetServerAddons to store the desired value
    void SetDesiredAddons(const char *addons) { m_desiredServerAddons = addons ? addons : ""; }
    // Entity Listener
    void OnEntityCreated(CEntityInstance *pEntity);
    void OnEntitySpawned(CEntityInstance *pEntity);
    void OnEntityDeleted(CEntityInstance *pEntity);
    // Client ConCommands
    bool OnPre_ClientConCommand(void *controller, void *args);
    // Precache
    void OnBuildGameSessionManifest(void *manifest);
    // Game state
    void OnGameStateChanged(int newState);
    bool ShouldAllowGameStateChange(int currentState, int newState);
    // Touch events
    void OnStartTouch(CBaseEntity *entity, CBaseEntity *other);
    void OnEndTouch(CBaseEntity *entity, CBaseEntity *other);
    // Modifier events - dispatched before the native FireModifierEvent runs (observe-only)
    void OnPre_FireModifierEvent(EModifierEvent event, CBaseEntity *caster, CBaseEntity *target,
                               CBaseEntity *castEntity, void *eventData);
    // Entity I/O — Pre returns HookResult int (0=Continue, 1=Stop, 2=Handled); Post returns void.
    int OnEntityAcceptInputPre(const char *className, const char *inputName,
                                void *entity, void *activator, void *caller, void *variantValue);
    void OnEntityAcceptInputPost(const char *className, const char *inputName,
                                  void *entity, void *activator, void *caller, void *variantValue);
    int OnEntityFireOutputPre(const char *callerClass, const char *outputName,
                               void *activator, void *caller, const void *variantValue, float delay);
    void OnEntityFireOutputPost(const char *callerClass, const char *outputName,
                                 void *activator, void *caller, const void *variantValue, float delay);
    // Usercmds
    void OnPre_ProcessUsercmds(int playerSlot, const uint8_t *batchBytes, int batchLen, int numCmds, bool paused, float margin, uint8_t *outBytes, int *outLen);
    // Ability think - returns bitmask of buttons to block, outForcedButtons receives bits to force
    uint64_t OnPre_AbilityThink(int playerSlot, void *pawnEntity, uint64_t heldButtons, uint64_t changedButtons, uint64_t scrollButtons, uint64_t *outForcedButtons);
    // AddModifier
    bool OnPre_AddModifier(void *modifierProp, CBaseEntity *&pCaster, uint32_t &hAbility, int &iTeam,
                           void *vdata, void *pParams, void *pKV);
    // CheckTransmit - dispatches per-player to managed code
    void OnPost_CheckTransmit(CCheckTransmitInfo **ppInfoList, int nInfoCount);
    // InitializeHeroOnPawn - dispatched after the pawn's hero abilities/modifiers
    // have been (re)populated server-side, regardless of which path got us here
    void OnPost_InitializeHeroOnPawn(void *pawn);

    template <typename T>
    T *GetEntity(CEntityIndex index) {
        return reinterpret_cast<T *>(GameEntitySystem()->GetEntityInstance(index));
    }

    template <typename T>
    T *GetEntity(int index) {
        return GetEntity<T>(CEntityIndex(index));
    }

private:
    void GetInterfaceFactories();

    struct CInterfaceFactories {
        CreateInterfaceFn server;
        CreateInterfaceFn engine2;
        CreateInterfaceFn schemasystem;
        CreateInterfaceFn networksystem;
        CreateInterfaceFn tier0;
        CreateInterfaceFn filesystem_stdio;
        CreateInterfaceFn soundsystem;
    } InterfaceFactories;

    bool m_clientFullyConnected[64]{};
    bool m_touchHooksInitialized = false;
    std::string m_savedServerAddons;   // stash original value for ReplyConnection restore
    std::string m_desiredServerAddons; // value set by managed plugins via SetServerAddons
    DotNetHost m_dotnetHost;
    bool m_dotnetInitialized = false;
    ManagedCallbacks m_managed{};
};

inline Deadworks g_Deadworks;
inline std::unique_ptr<Logger> g_Log;
} // namespace deadworks
