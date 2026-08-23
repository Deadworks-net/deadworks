#pragma once

#include <coreclr_delegates.h>
#include <cstdint>

namespace deadworks {

class DotNetHost;

struct ManagedCallbacks {
    using OnStartupServerFn = void(CORECLR_DELEGATE_CALLTYPE *)(const char *mapName);
    using OnTakeDamageOldFn = bool(CORECLR_DELEGATE_CALLTYPE *)(void *entity, void *info, void *result);
    using OnModifyCurrencyFn = bool(CORECLR_DELEGATE_CALLTYPE *)(void *pawn, uint32_t nCurrencyType, int32_t nAmount,
                                                                   uint32_t nSource, uint8_t bSilent, uint8_t bForceGain,
                                                                   uint8_t bSpendOnly, void *pSourceAbility, void *pSourceEntity);
    using OnGameEventFn = int(CORECLR_DELEGATE_CALLTYPE *)(const char *eventName, void *eventPtr);
    using OnGameFrameFn = void(CORECLR_DELEGATE_CALLTYPE *)(uint8_t simulating, uint8_t firstTick, uint8_t lastTick);
    using OnNetMessageOutgoingFn = int(CORECLR_DELEGATE_CALLTYPE *)(int msgId, const uint8_t *protoBytes, int protoLen, uint64_t recipientMask, uint8_t *outBytes, int *outLen, uint64_t *outRecipientMask);
    using OnNetMessageIncomingFn = int(CORECLR_DELEGATE_CALLTYPE *)(int senderSlot, int msgId, const uint8_t *protoBytes, int protoLen);
    using OnClientConnectFn = uint8_t(CORECLR_DELEGATE_CALLTYPE *)(int slot, const char16_t *name, uint64_t xuid, const char16_t *ipAddress);
    using OnClientPutInServerFn = void(CORECLR_DELEGATE_CALLTYPE *)(int slot, const char16_t *name, uint64_t xuid, uint8_t isBot);
    using OnClientFullConnectFn = void(CORECLR_DELEGATE_CALLTYPE *)(int slot);
    using OnClientDisconnectFn = void(CORECLR_DELEGATE_CALLTYPE *)(int slot, int reason);
    using OnEntityCreatedFn = void(CORECLR_DELEGATE_CALLTYPE *)(void *entity);
    using OnEntitySpawnedFn = void(CORECLR_DELEGATE_CALLTYPE *)(void *entity);
    using OnEntityDeletedFn = void(CORECLR_DELEGATE_CALLTYPE *)(void *entity);
    using OnClientConCommandFn = int(CORECLR_DELEGATE_CALLTYPE *)(void *controller, const char *command, int argc, const char **argv);
    using OnPrecacheResourcesFn = void(CORECLR_DELEGATE_CALLTYPE *)();
    using OnEntityStartTouchFn = void(CORECLR_DELEGATE_CALLTYPE *)(void *entity, void *other);
    using OnEntityEndTouchFn = void(CORECLR_DELEGATE_CALLTYPE *)(void *entity, void *other);
    // Entity input (CEntityInstance::AcceptInput) — Pre returns HookResult int (0=Continue, 1=Stop, 2=Handled).
    using OnEntityAcceptInputFn = int(CORECLR_DELEGATE_CALLTYPE *)(const char *className, const char *inputName,
                                                                    void *entity, void *activator, void *caller,
                                                                    void *variantValue);
    using OnEntityAcceptInputPostFn = void(CORECLR_DELEGATE_CALLTYPE *)(const char *className, const char *inputName,
                                                                        void *entity, void *activator, void *caller,
                                                                        void *variantValue);
    // Entity output (CEntityIOOutput::FireOutputInternal) — Pre returns HookResult int.
    using OnEntityFireOutputFn = int(CORECLR_DELEGATE_CALLTYPE *)(const char *callerClass, const char *outputName,
                                                                   void *activator, void *caller,
                                                                   const void *variantValue, float delay);
    using OnEntityFireOutputPostFn = void(CORECLR_DELEGATE_CALLTYPE *)(const char *callerClass, const char *outputName,
                                                                       void *activator, void *caller,
                                                                       const void *variantValue, float delay);
    using OnProcessUsercmdsFn = void(CORECLR_DELEGATE_CALLTYPE *)(int playerSlot, const uint8_t *batchBytes, int batchLen, int numCmds, uint8_t paused, float margin, uint8_t *outBatchBytes, int *outBatchLen);
    using OnAbilityAttemptFn = uint64_t(CORECLR_DELEGATE_CALLTYPE *)(int playerSlot, void *pawnEntity, uint64_t heldButtons, uint64_t changedButtons, uint64_t scrollButtons, uint64_t *outForcedButtons);
    using OnAddModifierFn = int(CORECLR_DELEGATE_CALLTYPE *)(void *modifierProp, void **pCaster, uint32_t *pHAbility, int32_t *pITeam, void *vdata, void *params, void *kv);
    using OnCheckTransmitFn = void(CORECLR_DELEGATE_CALLTYPE *)(int playerSlot, void *transmitBits);
    using OnPawnHeroInitializedFn = void(CORECLR_DELEGATE_CALLTYPE *)(void *pawn);
    using OnGameStateChangedFn = void(CORECLR_DELEGATE_CALLTYPE *)(int32_t newState);
    using ShouldAllowGameStateChangeFn = uint8_t(CORECLR_DELEGATE_CALLTYPE *)(int32_t currentState, int32_t newState);

    OnStartupServerFn onStartupServer = nullptr;
    OnTakeDamageOldFn onTakeDamageOld = nullptr;
    OnModifyCurrencyFn onModifyCurrency = nullptr;
    OnGameEventFn onGameEvent = nullptr;
    OnGameFrameFn onGameFrame = nullptr;
    OnNetMessageOutgoingFn onNetMessageOutgoing = nullptr;
    OnNetMessageIncomingFn onNetMessageIncoming = nullptr;
    OnClientConnectFn onClientConnect = nullptr;
    OnClientPutInServerFn onClientPutInServer = nullptr;
    OnClientFullConnectFn onClientFullConnect = nullptr;
    OnClientDisconnectFn onClientDisconnect = nullptr;
    OnEntityCreatedFn onEntityCreated = nullptr;
    OnEntitySpawnedFn onEntitySpawned = nullptr;
    OnEntityDeletedFn onEntityDeleted = nullptr;
    OnClientConCommandFn onClientConCommand = nullptr;
    OnPrecacheResourcesFn onPrecacheResources = nullptr;
    OnEntityStartTouchFn onEntityStartTouch = nullptr;
    OnEntityEndTouchFn onEntityEndTouch = nullptr;
    OnEntityAcceptInputFn onEntityAcceptInput = nullptr;
    OnEntityAcceptInputPostFn onEntityAcceptInputPost = nullptr;
    OnEntityFireOutputFn onEntityFireOutput = nullptr;
    OnEntityFireOutputPostFn onEntityFireOutputPost = nullptr;
    OnProcessUsercmdsFn onProcessUsercmds = nullptr;
    OnAbilityAttemptFn onAbilityAttempt = nullptr;
    OnAddModifierFn onAddModifier = nullptr;
    OnCheckTransmitFn onCheckTransmit = nullptr;
    OnPawnHeroInitializedFn onPawnHeroInitialized = nullptr;
    OnGameStateChangedFn onGameStateChanged = nullptr;
    ShouldAllowGameStateChangeFn shouldAllowGameStateChange = nullptr;
};

void InitializeManagedCallbacks(DotNetHost &host, ManagedCallbacks &managed);

} // namespace deadworks
