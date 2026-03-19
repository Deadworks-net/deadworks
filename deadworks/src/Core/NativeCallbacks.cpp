#include "NativeCallbacks.hpp"
#include "NativeOffsets.hpp"
#include "NativeAbility.hpp"
#include "NativeDamage.hpp"
#include "NativeHero.hpp"
#include "Deadworks.hpp"

#include "Hooks/CCitadelPlayerPawn.hpp"
#include "Hooks/CBaseEntity.hpp"
#include "Hooks/GameEvents.hpp"
#include "Hooks/PostEventAbstract.hpp"
#include "Hooks/BuildGameSessionManifest.hpp"

#include "Hooks/TraceShape.hpp" // for g_pPhysicsQuery
#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CBaseEntity.hpp"
#include "../SDK/CCitadelPlayerController.hpp"
#include "../SDK/CEntitySystem.hpp"
#include "../SDK/Core.hpp"
#include "../SDK/Util.hpp"

#include <tier1/convar.h>
#include <igameevents.h>
#include <igameeventsystem.h>
#include <tier1/keyvalues3.h>
#include <tier1/utlvector.h>
#include <variant.h>
#include <irecipientfilter.h>
#include <netmessages.h>
#include <entity2/entitysystem.h>
#include <entity2/entityclass.h>
#include <server_class.h>
#include <icvar.h>

using namespace deadworks;

// --- Recipient filter classes for NativeSendNetMessage ---

class CRecipientFilter : public IRecipientFilter {
public:
    CRecipientFilter(NetChannelBufType_t nBufType = BUF_RELIABLE, bool bInitMessage = false)
        : m_nBufType(nBufType)
        , m_bInitMessage(bInitMessage) {}

    CRecipientFilter(IRecipientFilter *source, CPlayerSlot exceptSlot = -1) {
        m_Recipients = source->GetRecipients();
        m_nBufType = source->GetNetworkBufType();
        m_bInitMessage = source->IsInitMessage();

        if (exceptSlot.Get() != -1)
            m_Recipients.Clear(exceptSlot.Get());
    }

    ~CRecipientFilter() override {}

    NetChannelBufType_t GetNetworkBufType(void) const override { return m_nBufType; }
    bool IsInitMessage(void) const override { return m_bInitMessage; }
    const CPlayerBitVec &GetRecipients(void) const override { return m_Recipients; }
    CPlayerSlot GetPredictedByPlayerSlot(void) const override { return -1; }

    void AddRecipient(CPlayerSlot slot) {
        if (slot.Get() >= 0 && slot.Get() < ABSOLUTE_PLAYER_LIMIT)
            m_Recipients.Set(slot.Get());
    }

    int GetRecipientCount() {
        const uint64 bits = *reinterpret_cast<const uint64 *>(&GetRecipients());
        return std::popcount(bits);
    }

protected:
    NetChannelBufType_t m_nBufType;
    bool m_bInitMessage;
    CPlayerBitVec m_Recipients;
};

class CSingleRecipientFilter : public CRecipientFilter {
public:
    CSingleRecipientFilter(CPlayerSlot nRecipientSlot, NetChannelBufType_t nBufType = BUF_RELIABLE, bool bInitMessage = false)
        : CRecipientFilter(nBufType, bInitMessage) {
        if (nRecipientSlot.Get() >= 0 && nRecipientSlot.Get() < ABSOLUTE_PLAYER_LIMIT)
            m_Recipients.Set(nRecipientSlot.Get());
    }
};

// --- Entity manipulation types ---

using AcceptInputFn = bool(__thiscall *)(void *thisptr, const char *pInputName, CEntityInstance *pActivator, CEntityInstance *pCaller, variant_t *pValue, int nOutputID, void *);

// ---------------------------------------------------------------------------
// Native callback implementations — Core / Entity / Schema / ConVar / Events
// ---------------------------------------------------------------------------

static void __cdecl ManagedLogCallback(const char16_t *message) {
    if (!g_Log || !message)
        return;

    std::wstring_view wv(reinterpret_cast<const wchar_t *>(message));
    while (!wv.empty() && (wv.back() == L'\n' || wv.back() == L'\r'))
        wv.remove_suffix(1);
    if (wv.empty())
        return;

    int len = WideCharToMultiByte(CP_UTF8, 0, wv.data(), static_cast<int>(wv.size()), nullptr, 0, nullptr, nullptr);
    std::string utf8(len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, wv.data(), static_cast<int>(wv.size()), utf8.data(), len, nullptr, nullptr);

    g_Log->Info("[managed] {}", utf8);
}

static void *__cdecl NativeGetPlayerController(int slot) {
    return g_Deadworks.GetEntity<CCitadelPlayerController>(CEntityIndex(slot + 1));
}

static void *__cdecl NativeGetHeroPawn(void *controller) {
    if (!controller)
        return nullptr;
    return static_cast<CCitadelPlayerController *>(controller)->GetHeroPawn();
}

static void __cdecl NativeModifyCurrency(void *pPawnThis, uint32_t nCurrencyType, int32_t nAmount,
                                         uint32_t nSource, uint8_t bSilent, uint8_t bForceGain,
                                         uint8_t bSpendOnly, void *pSourceAbility, void *pSourceEntity) {
    if (!pPawnThis)
        return;
    hooks::g_CCitadelPlayerPawn_ModifyCurrency.thiscall<void>(
        pPawnThis, static_cast<ECurrencyType>(nCurrencyType), nAmount,
        static_cast<ECurrencySource>(nSource),
        bSilent != 0, bForceGain != 0, bSpendOnly != 0,
        pSourceAbility, pSourceEntity);
}

static const char *__cdecl NativeGetEntityDesignerName(void *entity) {
    if (!entity)
        return "";
    return static_cast<CBaseEntity *>(entity)->GetClassname();
}

static const char *__cdecl NativeGetEntityClassname(void *entity) {
    if (!entity)
        return "";
    auto *ent = static_cast<CBaseEntity *>(entity);
    if (!ent->m_pEntity ||
        !ent->m_pEntity->m_pClass ||
        !ent->m_pEntity->m_pClass->m_pServerClass ||
        !ent->m_pEntity->m_pClass->m_pServerClass->m_pDLLClassName) {
        return "";
    }
    return ent->m_pEntity->m_pClass->m_pServerClass->m_pDLLClassName;
}

static int32_t __cdecl NativeGetUtlVectorSize(void *vec) {
    if (!vec) return 0;
    return reinterpret_cast<CUtlVectorBase<uint8_t> *>(vec)->Count();
}

static void *__cdecl NativeGetUtlVectorData(void *vec) {
    if (!vec) return nullptr;
    return reinterpret_cast<CUtlVectorBase<uint8_t> *>(vec)->Base();
}

static void *__cdecl NativeGetEntityFromHandle(uint32_t handle) {
    CEntityHandle eh(handle);
    if (!eh.IsValid())
        return nullptr;
    return eh.Get();
}

static void *__cdecl NativeGetEntityByIndex(int32_t index) {
    return GameEntitySystem()->GetEntityInstance(CEntityIndex(index));
}

static uint32_t __cdecl NativeGetEntityHandle(void *entity) {
    if (!entity)
        return 0xFFFFFFFF;
    return static_cast<uint32_t>(static_cast<CBaseEntity *>(entity)->GetRefEHandle().ToInt());
}

static void __cdecl NativeGetSchemaField(const char *className, const char *fieldName, SchemaFieldResult *result) {
    uint32_t classHash = hash_32_fnv1a_const(className);
    uint32_t memberHash = hash_32_fnv1a_const(fieldName);
    SchemaKey key = schema::GetOffset(className, classHash, fieldName, memberHash);
    result->offset = key.Offset;
    result->chainOffset = schema::FindChainOffset(className, classHash);
    result->networked = key.Networked ? 1 : 0;
    result->_pad = 0;
}

static uint64_t __cdecl NativeFindConVar(const char *name) {
    if (!g_pCVar || !name)
        return 0;
    ConVarRef ref = g_pCVar->FindConVar(name);
    return ref.IsValidRef() ? static_cast<uint64_t>(ref) : 0;
}

static void __cdecl NativeSetConVarInt(uint64_t handle, int32_t value) {
    if (!handle)
        return;
    ConVarRef ref(handle);
    ConVarData *data = g_pCVar->GetConVarData(ref);
    if (!data)
        return;
    ConVarRefAbstract cvarAbs(ref, data);
    cvarAbs.SetAs<int>(value);
}

static void __cdecl NativeSetConVarFloat(uint64_t handle, float value) {
    if (!handle)
        return;
    ConVarRef ref(handle);
    ConVarData *data = g_pCVar->GetConVarData(ref);
    if (!data)
        return;
    ConVarRefAbstract cvarAbs(ref, data);
    cvarAbs.SetAs<float>(value);
}

static void __cdecl NativeNotifyStateChanged(void *entity, int32_t fieldOffset, int16_t chainOffset, int32_t networkStateChangedOffset) {
    if (!entity)
        return;
    auto p = reinterpret_cast<uintptr_t>(entity);
    if (chainOffset != 0)
        ChainNetworkStateChanged(p + chainOffset, static_cast<uint32_t>(fieldOffset));
    else if (!networkStateChangedOffset)
        EntityNetworkStateChanged(p, static_cast<uint32_t>(fieldOffset));
    else
        NetworkVarStateChanged(p, static_cast<uint32_t>(fieldOffset), static_cast<uint32_t>(networkStateChangedOffset));
}

static void __cdecl NativeRegisterGameEvent(const char *name) {
    if (!g_pGameEventManager2 || !name)
        return;
    g_pGameEventManager2->AddListener(&g_DeadworksEventListener, name, true);
}

static uint8_t __cdecl NativeGameEventGetBool(void *event, const char *key, uint8_t def) {
    if (!event || !key)
        return def;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetBool(keySymbol, def != 0) ? 1 : 0;
}

static int32_t __cdecl NativeGameEventGetInt(void *event, const char *key, int32_t def) {
    if (!event || !key)
        return def;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetInt(keySymbol, def);
}

static float __cdecl NativeGameEventGetFloat(void *event, const char *key, float def) {
    if (!event || !key)
        return def;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetFloat(keySymbol, def);
}

static const char *__cdecl NativeGameEventGetString(void *event, const char *key, const char *def) {
    if (!event || !key)
        return def ? def : "";
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetString(keySymbol, def ? def : "");
}

static void __cdecl NativeGameEventSetBool(void *event, const char *key, uint8_t val) {
    if (!event || !key)
        return;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    static_cast<IGameEvent *>(event)->SetBool(keySymbol, val != 0);
}

static void __cdecl NativeGameEventSetInt(void *event, const char *key, int32_t val) {
    if (!event || !key)
        return;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    static_cast<IGameEvent *>(event)->SetInt(keySymbol, val);
}

static void __cdecl NativeGameEventSetFloat(void *event, const char *key, float val) {
    if (!event || !key)
        return;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    static_cast<IGameEvent *>(event)->SetFloat(keySymbol, val);
}

static void __cdecl NativeGameEventSetString(void *event, const char *key, const char *val) {
    if (!event || !key)
        return;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    static_cast<IGameEvent *>(event)->SetString(keySymbol, val ? val : "");
}

static uint64_t __cdecl NativeGameEventGetUint64(void *event, const char *key, uint64_t def) {
    if (!event || !key)
        return def;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetUint64(keySymbol, def);
}

static void *__cdecl NativeGameEventGetPlayerController(void *event, const char *key) {
    if (!event || !key)
        return nullptr;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetPlayerController(keySymbol);
}

static void *__cdecl NativeGameEventGetPlayerPawn(void *event, const char *key) {
    if (!event || !key)
        return nullptr;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    return static_cast<IGameEvent *>(event)->GetPlayerPawn(keySymbol);
}

static uint32_t __cdecl NativeGameEventGetEHandle(void *event, const char *key) {
    if (!event || !key)
        return INVALID_EHANDLE_INDEX;
    auto keySymbol = GameEventKeySymbol_t::Make(key);
    CEntityHandle eh = static_cast<IGameEvent *>(event)->GetEHandle(keySymbol);
    return eh.IsValid() ? static_cast<uint32_t>(eh.ToInt()) : INVALID_EHANDLE_INDEX;
}

static void __cdecl NativeClientCommand(int slot, const char *command) {
    if (!g_pEngineServer || !command)
        return;
    g_pEngineServer->ClientCommand(CPlayerSlot(slot), "%s", command);
}

static void __cdecl NativeExecuteServerCommand(const char *command) {
    if (!g_pEngineServer || !command)
        return;
    g_pEngineServer->ServerCommand(command);
}

// --- Engine log forwarding to managed code ---
static void(__cdecl *g_ManagedLogCallback)(const char *message) = nullptr;

class ManagedLoggingListener : public ILoggingListener {
public:
    void Log(const LoggingContext_t *pContext, const tchar *pMessage) override {
        if (g_ManagedLogCallback && pMessage)
            g_ManagedLogCallback(pMessage);
    }
};

static ManagedLoggingListener g_ManagedLoggingListener;
static bool g_ManagedListenerRegistered = false;

static void __cdecl NativeSetEngineLogCallback(void(__cdecl *callback)(const char *message)) {
    g_ManagedLogCallback = callback;
    if (callback && !g_ManagedListenerRegistered) {
        LoggingSystem_RegisterLoggingListener(&g_ManagedLoggingListener);
        g_ManagedListenerRegistered = true;
    } else if (!callback && g_ManagedListenerRegistered) {
        LoggingSystem_UnregisterLoggingListener(&g_ManagedLoggingListener);
        g_ManagedListenerRegistered = false;
    }
}

static void __cdecl NativeSetModel(void *entity, const char *modelName) {
    if (!entity || !modelName)
        return;
    using SetModelFn = void(__thiscall *)(void *, const char *);
    static const auto fn = reinterpret_cast<SetModelFn>(
        MemoryDataLoader::Get().GetOffset("CBaseModelEntity::SetModel").value());
    fn(entity, modelName);
}

static void __cdecl NativeRemoveEntity(void *entity) {
    if (!entity)
        return;
    UTIL_Remove(static_cast<CEntityInstance *>(entity));
}

static void __cdecl NativeSetPawn(void *controller, void *pawn, uint8_t bRetainOldPawnTeam, uint8_t bCopyMovementState, uint8_t bAllowTeamMismatch, uint8_t bPreserveMovementState) {
    if (!controller)
        return;
    static_cast<CBasePlayerController *>(controller)->SetPawn(static_cast<CBasePlayerPawn *>(pawn), bRetainOldPawnTeam != 0, bCopyMovementState != 0, bAllowTeamMismatch != 0, bPreserveMovementState != 0);
}

// --- KV3 ---

static void *__cdecl NativeKV3Create() {
    auto *kv = new KeyValues3();
    kv->SetToEmptyTable();
    return kv;
}

static void __cdecl NativeKV3Destroy(void *kv3) {
    delete static_cast<KeyValues3 *>(kv3);
}

static void __cdecl NativeKV3SetString(void *kv3, const char *key, const char *value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberString(CKV3MemberName::Make(key), value ? value : "");
}

static void __cdecl NativeKV3SetBool(void *kv3, const char *key, uint8_t value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberBool(CKV3MemberName::Make(key), value != 0);
}

static void __cdecl NativeKV3SetInt(void *kv3, const char *key, int32_t value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberInt(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeKV3SetUInt(void *kv3, const char *key, uint32_t value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberUInt(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeKV3SetInt64(void *kv3, const char *key, int64_t value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberInt64(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeKV3SetUInt64(void *kv3, const char *key, uint64_t value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberUInt64(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeKV3SetFloat(void *kv3, const char *key, float value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberFloat(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeKV3SetDouble(void *kv3, const char *key, double value) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberDouble(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeKV3SetVector(void *kv3, const char *key, float x, float y, float z) {
    if (!kv3 || !key) return;
    static_cast<KeyValues3 *>(kv3)->SetMemberVector(CKV3MemberName::Make(key), Vector(x, y, z));
}

// --- Entity creation ---

static void *__cdecl NativeCreateEntityByName(const char *className) {
    if (!className)
        return nullptr;
    return EntitySystemHelper::CreateEntityByName(className);
}

static void __cdecl NativeQueueSpawnEntity(void *entity, void *ekv) {
    if (!entity)
        return;
    EntitySystemHelper::QueueSpawnEntity(static_cast<CEntityInstance *>(entity)->m_pEntity,
                                         static_cast<CEntityKeyValues *>(ekv));
}

static void __cdecl NativeExecuteQueuedCreation() {
    EntitySystemHelper::ExecuteQueuedCreation();
}

static void __cdecl NativeAcceptInput(void *entity, const char *inputName, void *activator, void *caller, const char *value) {
    if (!entity || !inputName)
        return;
    static const auto fn = reinterpret_cast<AcceptInputFn>(
        MemoryDataLoader::Get().GetOffset("CEntityInstance::AcceptInput").value());

    variant_t val(value ? value : "");
    fn(entity, inputName,
       static_cast<CEntityInstance *>(activator),
       static_cast<CEntityInstance *>(caller),
       &val, 0, nullptr);
}

static void __cdecl NativeSetSchemaString(void *entity, const char *className, const char *fieldName, const char *value) {
    if (!entity || !className || !fieldName)
        return;

    uint32_t classHash = hash_32_fnv1a_const(className);
    uint32_t memberHash = hash_32_fnv1a_const(fieldName);
    SchemaKey key = schema::GetOffset(className, classHash, fieldName, memberHash);
    int16_t chainOffset = schema::FindChainOffset(className, classHash);

    auto p = reinterpret_cast<uintptr_t>(entity);
    *reinterpret_cast<const char **>(p + key.Offset) = _strdup(value ? value : "");

    if (key.Networked) {
        if (chainOffset != 0)
            ChainNetworkStateChanged(p + chainOffset, static_cast<uint32_t>(key.Offset));
        else
            EntityNetworkStateChanged(p, static_cast<uint32_t>(key.Offset));
    }
}

static void *__cdecl NativeCreateEntityKeyValues() {
    void *mem = MemAlloc_Alloc(sizeof(CEntityKeyValues));
    return new (mem) CEntityKeyValues();
}

// --- EKV ---

static void __cdecl NativeEKVSetString(void *ekv, const char *key, const char *value) {
    if (!ekv || !key) return;
    static_cast<CEntityKeyValues *>(ekv)->SetString(CKV3MemberName::Make(key), value ? value : "");
}

static void __cdecl NativeEKVSetBool(void *ekv, const char *key, uint8_t value) {
    if (!ekv || !key) return;
    static_cast<CEntityKeyValues *>(ekv)->SetBool(CKV3MemberName::Make(key), value != 0);
}

static void __cdecl NativeEKVSetVector(void *ekv, const char *key, float x, float y, float z) {
    if (!ekv || !key) return;
    static_cast<CEntityKeyValues *>(ekv)->SetVector(CKV3MemberName::Make(key), Vector(x, y, z));
}

static void __cdecl NativeEKVSetFloat(void *ekv, const char *key, float value) {
    if (!ekv || !key) return;
    static_cast<CEntityKeyValues *>(ekv)->SetFloat(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeEKVSetInt(void *ekv, const char *key, int32_t value) {
    if (!ekv || !key) return;
    static_cast<CEntityKeyValues *>(ekv)->SetInt(CKV3MemberName::Make(key), value);
}

static void __cdecl NativeEKVSetColor(void *ekv, const char *key, uint8_t r, uint8_t g, uint8_t b, uint8_t a) {
    if (!ekv || !key) return;
    static_cast<CEntityKeyValues *>(ekv)->SetColor(CKV3MemberName::Make(key), Color(r, g, b, a));
}

// --- Game events ---

static void *__cdecl NativeCreateGameEvent(const char *name, uint8_t bForce) {
    if (!name || !g_pGameEventManager2)
        return nullptr;
    return g_pGameEventManager2->CreateEvent(name, bForce != 0);
}

static uint8_t __cdecl NativeFireGameEvent(void *event, uint8_t bDontBroadcast) {
    if (!event || !g_pGameEventManager2)
        return 0;
    return g_pGameEventManager2->FireEvent(static_cast<IGameEvent *>(event), bDontBroadcast != 0) ? 1 : 0;
}

static void __cdecl NativeFreeGameEvent(void *event) {
    if (!event || !g_pGameEventManager2)
        return;
    g_pGameEventManager2->FreeEvent(static_cast<IGameEvent *>(event));
}

// --- Networking ---

static void __cdecl NativeSendNetMessage(int msgId, const uint8_t *protoBytes, int protoLen, uint64_t recipientMask) {
    if (!g_pNetworkMessages || !protoBytes || protoLen <= 0)
        return;

    auto *serializer = g_pNetworkMessages->FindNetworkMessageById(static_cast<NetworkMessageId>(msgId));
    if (!serializer)
        return;

    CNetMessage *msg = serializer->AllocateMessage();
    if (!msg)
        return;

    auto *pbMsg = const_cast<google::protobuf::Message *>(msg->AsMessage());
    if (pbMsg && pbMsg->ParseFromArray(protoBytes, protoLen)) {
        CRecipientFilter filter;
        for (int i = 0; i < ABSOLUTE_PLAYER_LIMIT; ++i) {
            if (recipientMask & (1ULL << i))
                filter.AddRecipient(CPlayerSlot(i));
        }
        g_pGameEventSystem->PostEventAbstract(-1, false, &filter, serializer, msg, 0);
    }

    g_pNetworkMessages->DeallocateNetMessageAbstract(serializer, msg);
}

// ---------------------------------------------------------------------------
// ConCommand registration for managed plugins
// ---------------------------------------------------------------------------

static std::map<std::string, ConCommand *> g_RegisteredConCommands;
using ManagedConCommandDispatchFn = void(CORECLR_DELEGATE_CALLTYPE *)(int playerSlot, const char *command, int argc, const char **argv);
ManagedConCommandDispatchFn g_ManagedConCommandDispatch = nullptr;

static void ConCommandDispatchCallback(const CCommandContext &context, const CCommand &args) {
    if (!g_ManagedConCommandDispatch)
        return;

    int playerSlot = context.GetPlayerSlot().Get();
    int argc = args.ArgC();
    const char *command = argc > 0 ? args[0] : "";
    const char **argv = args.ArgV();

    g_ManagedConCommandDispatch(playerSlot, command, argc, argv);
}

static void __cdecl NativeRegisterConCommand(const char *name, const char *description, uint64_t flags) {
    if (!name)
        return;

    std::string key(name);
    if (g_RegisteredConCommands.count(key))
        return;

    auto *cmd = new ConCommand(
        strdup(name),
        ConCommandDispatchCallback,
        description ? strdup(description) : "",
        flags);

    g_RegisteredConCommands[key] = cmd;
}

static void __cdecl NativeUnregisterConCommand(const char *name) {
    if (!name)
        return;

    auto it = g_RegisteredConCommands.find(name);
    if (it != g_RegisteredConCommands.end()) {
        delete it->second;
        g_RegisteredConCommands.erase(it);
    }
}

static uint64_t __cdecl NativeCreateConVar(const char *name, const char *defaultValue, const char *description, uint64_t flags) {
    if (!name || !g_pCVar)
        return 0;

    ConVarRef ref = g_pCVar->FindConVar(name);
    if (ref.IsValidRef())
        return static_cast<uint64_t>(ref);

    return 0;
}

// ---------------------------------------------------------------------------
// Index-based ConVar / ConCommand access — C# drives the iteration
// ---------------------------------------------------------------------------

struct ConVarInfoResult {
    const char *name;
    const char *typeName;
    const char *value;
    const char *defaultValue;
    const char *description;
    uint64_t flags;
    const char *minValue;   // nullptr when absent
    const char *maxValue;   // nullptr when absent
};

struct ConCommandInfoResult {
    const char *name;
    const char *description;
    uint64_t flags;
};

static uint8_t __cdecl NativeGetConVarAt(uint16_t index, ConVarInfoResult *result) {
    if (!result || !g_pCVar)
        return 0;

    ConVarRef cvBase(index);
    ConVarRefAbstract cvRef(cvBase);
    if (!cvRef.IsValidRef() || !cvRef.IsConVarDataValid())
        return 0;

    static const char *s_TypeNames[] = {
        "bool", "int16", "uint16", "int32", "uint32",
        "int64", "uint64", "float32", "float64",
        "string", "color", "vector2", "vector3", "vector4", "qangle"
    };

    // Stable pointers — live in engine memory
    result->name = cvRef.GetName();
    result->description = cvRef.HasHelpText() ? cvRef.GetHelpText() : "";
    result->flags = cvRef.GetFlags();

    int16_t typeIdx = static_cast<int16_t>(cvRef.GetType());
    result->typeName = (typeIdx >= 0 && typeIdx < static_cast<int16_t>(std::size(s_TypeNames)))
        ? s_TypeNames[typeIdx] : "unknown";

    // Stringified values — use static buffers (game thread only)
    static CBufferStringGrowable<256> s_valueBuf, s_defaultBuf;
    static CBufferStringGrowable<64> s_minBuf, s_maxBuf;

    cvRef.GetValueAsString(s_valueBuf);
    result->value = s_valueBuf.Get();

    if (cvRef.HasDefault()) { cvRef.GetDefaultAsString(s_defaultBuf); result->defaultValue = s_defaultBuf.Get(); }
    else                   { result->defaultValue = ""; }

    if (cvRef.HasMin()) { cvRef.GetMinAsString(s_minBuf); result->minValue = s_minBuf.Get(); }
    else               { result->minValue = nullptr; }

    if (cvRef.HasMax()) { cvRef.GetMaxAsString(s_maxBuf); result->maxValue = s_maxBuf.Get(); }
    else               { result->maxValue = nullptr; }

    return 1;
}

static uint8_t __cdecl NativeGetConCommandAt(uint16_t index, ConCommandInfoResult *result) {
    if (!result || !g_pCVar)
        return 0;

    ConCommandRef cmd(index);
    // Default-constructed ConCommandRef gives the sentinel data
    if (cmd.GetRawData() == ConCommandRef().GetRawData())
        return 0;

    const char *name = cmd.GetName();
    if (!name || !name[0])
        return 0;

    result->name = name;
    result->description = cmd.HasHelpText() ? cmd.GetHelpText() : "";
    result->flags = cmd.GetFlags();
    return 1;
}

// ---------------------------------------------------------------------------
// Entity virtual function wrappers
// ---------------------------------------------------------------------------

static int32_t __cdecl NativeGetMaxHealth(void *entity) {
    if (!entity)
        return 0;
    return GetVFunc<int(__thiscall *)(void *)>(entity, offsets::kVtblGetMaxHealth)(entity);
}

static int32_t __cdecl NativeHeal(void *entity, float amount) {
    if (!entity)
        return 0;
    return GetVFunc<int(__thiscall *)(void *, float)>(entity, offsets::kVtblHeal)(entity, amount);
}

static void *__cdecl NativeGetGlobalVars() {
    if (!g_pEngineServer)
        return nullptr;
    return g_pEngineServer->GetServerGlobals();
}

// ---------------------------------------------------------------------------
// Resolve statics that PostInit needs
// ---------------------------------------------------------------------------

void deadworks::ResolveNativeStatics() {
    ResolveDamageStatics();
    ResolveHeroStatics();
}

// ---------------------------------------------------------------------------
// PopulateNativeCallbacks
// ---------------------------------------------------------------------------

void deadworks::PopulateNativeCallbacks(NativeCallbacks &callbacks) {
    // Core
    callbacks.Log = &ManagedLogCallback;
    callbacks.GetPlayerController = &NativeGetPlayerController;
    callbacks.GetHeroPawn = &NativeGetHeroPawn;
    callbacks.ModifyCurrency = &NativeModifyCurrency;

    // Schema
    callbacks.GetSchemaField = &NativeGetSchemaField;
    callbacks.NotifyStateChanged = &NativeNotifyStateChanged;
    callbacks.SetSchemaString = &NativeSetSchemaString;

    // ConVar
    callbacks.FindConVar = &NativeFindConVar;
    callbacks.SetConVarInt = &NativeSetConVarInt;
    callbacks.SetConVarFloat = &NativeSetConVarFloat;

    // Entity
    callbacks.GetEntityDesignerName = &NativeGetEntityDesignerName;
    callbacks.GetEntityClassname = &NativeGetEntityClassname;
    callbacks.GetEntityFromHandle = &NativeGetEntityFromHandle;
    callbacks.GetEntityByIndex = &NativeGetEntityByIndex;
    callbacks.GetEntityHandle = &NativeGetEntityHandle;
    callbacks.CreateEntityByName = &NativeCreateEntityByName;
    callbacks.QueueSpawnEntity = &NativeQueueSpawnEntity;
    callbacks.ExecuteQueuedCreation = &NativeExecuteQueuedCreation;
    callbacks.AcceptInput = &NativeAcceptInput;
    callbacks.RemoveEntity = &NativeRemoveEntity;
    callbacks.SetPawn = &NativeSetPawn;
    callbacks.ClientCommand = &NativeClientCommand;
    callbacks.GetUtlVectorSize = &NativeGetUtlVectorSize;
    callbacks.GetUtlVectorData = &NativeGetUtlVectorData;

    // Game events
    callbacks.RegisterGameEvent = &NativeRegisterGameEvent;
    callbacks.GameEventGetBool = &NativeGameEventGetBool;
    callbacks.GameEventGetInt = &NativeGameEventGetInt;
    callbacks.GameEventGetFloat = &NativeGameEventGetFloat;
    callbacks.GameEventGetString = &NativeGameEventGetString;
    callbacks.GameEventSetBool = &NativeGameEventSetBool;
    callbacks.GameEventSetInt = &NativeGameEventSetInt;
    callbacks.GameEventSetFloat = &NativeGameEventSetFloat;
    callbacks.GameEventSetString = &NativeGameEventSetString;
    callbacks.GameEventGetUint64 = &NativeGameEventGetUint64;
    callbacks.GameEventGetPlayerController = &NativeGameEventGetPlayerController;
    callbacks.GameEventGetPlayerPawn = &NativeGameEventGetPlayerPawn;
    callbacks.GameEventGetEHandle = &NativeGameEventGetEHandle;
    callbacks.CreateGameEvent = &NativeCreateGameEvent;
    callbacks.FireGameEvent = &NativeFireGameEvent;
    callbacks.FreeGameEvent = &NativeFreeGameEvent;

    // Networking
    callbacks.SendNetMessage = &NativeSendNetMessage;

    // KV3
    callbacks.KV3Create = &NativeKV3Create;
    callbacks.KV3Destroy = &NativeKV3Destroy;
    callbacks.KV3SetString = &NativeKV3SetString;
    callbacks.KV3SetBool = &NativeKV3SetBool;
    callbacks.KV3SetInt = &NativeKV3SetInt;
    callbacks.KV3SetUInt = &NativeKV3SetUInt;
    callbacks.KV3SetInt64 = &NativeKV3SetInt64;
    callbacks.KV3SetUInt64 = &NativeKV3SetUInt64;
    callbacks.KV3SetFloat = &NativeKV3SetFloat;
    callbacks.KV3SetDouble = &NativeKV3SetDouble;
    callbacks.KV3SetVector = &NativeKV3SetVector;

    // Entity KeyValues
    callbacks.CreateEntityKeyValues = &NativeCreateEntityKeyValues;
    callbacks.EKVSetString = &NativeEKVSetString;
    callbacks.EKVSetBool = &NativeEKVSetBool;
    callbacks.EKVSetVector = &NativeEKVSetVector;
    callbacks.EKVSetFloat = &NativeEKVSetFloat;
    callbacks.EKVSetInt = &NativeEKVSetInt;
    callbacks.EKVSetColor = &NativeEKVSetColor;

    // ConCommands
    callbacks.RegisterConCommand = &NativeRegisterConCommand;
    callbacks.UnregisterConCommand = &NativeUnregisterConCommand;
    callbacks.CreateConVar = &NativeCreateConVar;

    // Subsystems
    PopulateAbilityNatives(callbacks);
    PopulateDamageNatives(callbacks);
    PopulateHeroNatives(callbacks);

    // Server command execution
    callbacks.ExecuteServerCommand = &NativeExecuteServerCommand;

    // SetModel
    callbacks.SetModel = &NativeSetModel;

    // Pass raw TraceShape function pointer and physics query pointer to C#
    auto traceShapeOpt = MemoryDataLoader::Get().GetOffset("TraceShape");
    callbacks.TraceShapeFn = traceShapeOpt ? reinterpret_cast<void *>(traceShapeOpt.value()) : nullptr;
    callbacks.PhysicsQueryPtr = &g_pPhysicsQuery;

    // CVar / ConCommand index-based access
    callbacks.GetConVarAt = reinterpret_cast<decltype(callbacks.GetConVarAt)>(&NativeGetConVarAt);
    callbacks.GetConCommandAt = reinterpret_cast<decltype(callbacks.GetConCommandAt)>(&NativeGetConCommandAt);

    // Entity virtual function wrappers
    callbacks.GetMaxHealth = &NativeGetMaxHealth;
    callbacks.Heal = &NativeHeal;

    // Global vars
    callbacks.GetGlobalVars = &NativeGetGlobalVars;

    // Engine log forwarding
    callbacks.SetEngineLogCallback = &NativeSetEngineLogCallback;
}
