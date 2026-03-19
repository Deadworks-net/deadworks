#pragma once

#include <cstdint>

namespace deadworks {

struct SchemaFieldResult {
    int32_t offset;      // byte offset of the field
    int16_t chainOffset; // __m_pChainEntity offset (0 if none)
    uint8_t networked;   // 1 if MNetworkEnable, else 0
    uint8_t _pad;        // explicit padding to 8 bytes
};

struct NativeCallbacks {
    void(__cdecl *Log)(const char16_t *);
    void *(__cdecl *GetPlayerController)(int slot);
    void *(__cdecl *GetHeroPawn)(void *controller);
    void(__cdecl *ModifyCurrency)(void *pPawnThis, uint32_t nCurrencyType, int32_t nAmount,
                                  uint32_t nSource, uint8_t bSilent, uint8_t bForceGain,
                                  uint8_t bSpendOnly, void *pSourceAbility, void *pSourceEntity);
    void(__cdecl *GetSchemaField)(const char *className, const char *fieldName, SchemaFieldResult *result);
    void(__cdecl *NotifyStateChanged)(void *entity, int32_t fieldOffset, int16_t chainOffset, int32_t networkStateChangedOffset);
    uint64_t(__cdecl *FindConVar)(const char *name);
    void(__cdecl *SetConVarInt)(uint64_t handle, int32_t value);
    void(__cdecl *SetConVarFloat)(uint64_t handle, float value);
    const char *(__cdecl *GetEntityDesignerName)(void *entity);
    const char *(__cdecl *GetEntityClassname)(void *entity);
    void *(__cdecl *GetEntityFromHandle)(uint32_t handle);
    void(__cdecl *RegisterGameEvent)(const char *name);
    uint8_t(__cdecl *GameEventGetBool)(void *event, const char *key, uint8_t def);
    int32_t(__cdecl *GameEventGetInt)(void *event, const char *key, int32_t def);
    float(__cdecl *GameEventGetFloat)(void *event, const char *key, float def);
    const char *(__cdecl *GameEventGetString)(void *event, const char *key, const char *def);
    void(__cdecl *GameEventSetBool)(void *event, const char *key, uint8_t val);
    void(__cdecl *GameEventSetInt)(void *event, const char *key, int32_t val);
    void(__cdecl *GameEventSetFloat)(void *event, const char *key, float val);
    void(__cdecl *GameEventSetString)(void *event, const char *key, const char *val);
    uint64_t(__cdecl *GameEventGetUint64)(void *event, const char *key, uint64_t def);
    void *(__cdecl *GameEventGetPlayerController)(void *event, const char *key);
    void *(__cdecl *GameEventGetPlayerPawn)(void *event, const char *key);
    uint32_t(__cdecl *GameEventGetEHandle)(void *event, const char *key);
    void(__cdecl *SendNetMessage)(int msgId, const uint8_t *protoBytes, int protoLen, uint64_t recipientMask);
    void(__cdecl *ClientCommand)(int slot, const char *command);
    void(__cdecl *RemoveEntity)(void *entity);
    void(__cdecl *SetPawn)(void *controller, void *pawn, uint8_t bRetainOldPawnTeam, uint8_t bCopyMovementState, uint8_t bAllowTeamMismatch, uint8_t bPreserveMovementState);
    void *(__cdecl *AddModifier)(void *entity, const char *modifierName, void *kv3, void *caster, void *ability, int32_t team,
                                 const char *const *overrideNames, const float *overrideValues, int32_t overrideCount);
    void *(__cdecl *KV3Create)();
    void(__cdecl *KV3Destroy)(void *kv3);
    void(__cdecl *KV3SetString)(void *kv3, const char *key, const char *value);
    void(__cdecl *KV3SetBool)(void *kv3, const char *key, uint8_t value);
    void(__cdecl *KV3SetInt)(void *kv3, const char *key, int32_t value);
    void(__cdecl *KV3SetUInt)(void *kv3, const char *key, uint32_t value);
    void(__cdecl *KV3SetInt64)(void *kv3, const char *key, int64_t value);
    void(__cdecl *KV3SetUInt64)(void *kv3, const char *key, uint64_t value);
    void(__cdecl *KV3SetFloat)(void *kv3, const char *key, float value);
    void(__cdecl *KV3SetDouble)(void *kv3, const char *key, double value);
    void(__cdecl *KV3SetVector)(void *kv3, const char *key, float x, float y, float z);
    void *(__cdecl *GetEntityByIndex)(int32_t index);
    uint32_t(__cdecl *GetEntityHandle)(void *entity);
    void *(__cdecl *CreateEntityByName)(const char *className);
    void(__cdecl *QueueSpawnEntity)(void *entity, void *ekv);
    void(__cdecl *ExecuteQueuedCreation)();
    void(__cdecl *Teleport)(void *entity, const float *position, const float *angles, const float *velocity);
    void(__cdecl *AcceptInput)(void *entity, const char *inputName, void *activator, void *caller, const char *value);
    void(__cdecl *SetSchemaString)(void *entity, const char *className, const char *fieldName, const char *value);
    void *(__cdecl *CreateEntityKeyValues)();
    void(__cdecl *EKVSetString)(void *ekv, const char *key, const char *value);
    void(__cdecl *EKVSetBool)(void *ekv, const char *key, uint8_t value);
    void(__cdecl *EKVSetVector)(void *ekv, const char *key, float x, float y, float z);
    void(__cdecl *EKVSetFloat)(void *ekv, const char *key, float value);
    void(__cdecl *EKVSetInt)(void *ekv, const char *key, int32_t value);
    void(__cdecl *EKVSetColor)(void *ekv, const char *key, uint8_t r, uint8_t g, uint8_t b, uint8_t a);
    void(__cdecl *PrecacheResource)(const char *path);
    void(__cdecl *EmitSound)(void *entity, const char *soundName, int32_t pitch, float volume, float delay);
    void *(__cdecl *CreateGameEvent)(const char *name, uint8_t bForce);
    uint8_t(__cdecl *FireGameEvent)(void *event, uint8_t bDontBroadcast);
    void(__cdecl *FreeGameEvent)(void *event);
    void(__cdecl *ResetHero)(void *pawn, uint8_t bReset);
    void *(__cdecl *GetHeroData)(const char *heroName);
    void(__cdecl *ChangeTeam)(void *controller, int32_t teamNum);
    void(__cdecl *SelectHero)(void *controller, const char *heroName);
    int32_t(__cdecl *GetUtlVectorSize)(void *vec);
    void *(__cdecl *GetUtlVectorData)(void *vec);
    uint8_t(__cdecl *RemoveAbility)(void *pawn, const char *abilityName);
    void *(__cdecl *AddAbility)(void *pawn, const char *abilityName, uint16_t slot);
    void *(__cdecl *AddItem)(void *pawn, const char *itemName, int32_t upgradeTier);
    uint8_t(__cdecl *SellItem)(void *pawn, const char *itemName, uint8_t bFullRefund, uint8_t bForceSellPrice);
    void(__cdecl *HurtEntity)(void *victim, void *attacker, void *inflictor, void *ability, float damage, int32_t damageType);
    void *(__cdecl *CreateDamageInfo)(void *inflictor, void *attacker, void *ability, float damage, int32_t damageType);
    void(__cdecl *DestroyDamageInfo)(void *info);
    void(__cdecl *TakeDamage)(void *victim, void *info);
    void(__cdecl *PrecacheHero)(const char *heroName);
    void(__cdecl *RegisterConCommand)(const char *name, const char *description, uint64_t flags);
    void(__cdecl *UnregisterConCommand)(const char *name);
    uint64_t(__cdecl *CreateConVar)(const char *name, const char *defaultValue, const char *description, uint64_t flags);
    void(__cdecl *ExecuteServerCommand)(const char *command);
    void(__cdecl *SetModel)(void *entity, const char *modelName);
    void *TraceShapeFn;   // Raw function pointer to TraceShape (called directly from C#)
    void **PhysicsQueryPtr; // Pointer to g_pPhysicsQuery (C# dereferences to get current value)
    uint8_t(__cdecl *GetConVarAt)(uint16_t index, void *result);      // ConVarInfoResult*
    uint8_t(__cdecl *GetConCommandAt)(uint16_t index, void *result);  // ConCommandInfoResult*
    int32_t(__cdecl *ExecuteAbilityBySlot)(void *abilityComponent, int16_t slot, uint8_t altCast, uint8_t flags);
    int32_t(__cdecl *ExecuteAbilityByID)(void *abilityComponent, int32_t abilityID, uint8_t altCast, uint8_t flags);
    int32_t(__cdecl *ExecuteAbility)(void *abilityComponent, void *ability, uint8_t altCast, uint8_t flags);
    void *(__cdecl *GetAbilityBySlot)(void *abilityComponent, int16_t slot);
    void(__cdecl *ToggleActivate)(void *ability, uint8_t activate);
    int32_t(__cdecl *GetMaxHealth)(void *entity);
    int32_t(__cdecl *Heal)(void *entity, float amount);
    void *(__cdecl *GetGlobalVars)();
    void(__cdecl *SetEngineLogCallback)(void(__cdecl *callback)(const char *message));
};

void PopulateNativeCallbacks(NativeCallbacks &callbacks);

// Resolve function pointers needed by Native* callbacks (called from PostInit)
void ResolveNativeStatics();

} // namespace deadworks
