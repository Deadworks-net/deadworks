#include "Schema.hpp"

#include <schemasystem/schemasystem.h>
#include <entity2/entityinstance.h>
#include <entity2/entitynetwork.h>
#include <entity2/entitysystem.h>
#include <networksystem/inetworkserializer.h>

#include <map>
#include <string_view>
#include <unordered_set>

#include "../../Core/Deadworks.hpp"
#include "../../Lib/Virtual.hpp"

using namespace deadworks;
using namespace std::literals;

using SchemaKeyValueMap_t = std::map<uint32_t, SchemaKey>;
using SchemaTableMap_t = std::map<uint32_t, SchemaKeyValueMap_t>;

static constexpr auto g_ChainKey = hash_32_fnv1a_const("__m_pChainEntity");

static bool IsFieldNetworked(SchemaClassFieldData_t &field) {
    for (auto i = 0; i < field.m_nStaticMetadataCount; i++) {
        static constexpr auto networkEnable = "MNetworkEnable"sv;
        if (field.m_pStaticMetadata[i].m_pszName == networkEnable)
            return true;
    }
    return false;
}

static void InitChainOffset(SchemaClassInfoData_t *pClassInfo, SchemaKeyValueMap_t &keyValueMap) {
    auto fieldSize = pClassInfo->m_nFieldCount;
    auto *pFields = pClassInfo->m_pFields;

    for (auto i = 0; i < fieldSize; i++) {
        auto &field = pFields[i];

        if (hash_32_fnv1a_const(field.m_pszName) != g_ChainKey) continue;

        std::pair<uint32_t, SchemaKey> keyValuePair;
        keyValuePair.first = g_ChainKey;
        keyValuePair.second.Offset = field.m_nSingleInheritanceOffset;
        keyValuePair.second.Networked = IsFieldNetworked(field);

        keyValueMap.insert(keyValuePair);
        return;
    }

    if (pClassInfo->m_nBaseClassCount)
        return InitChainOffset(pClassInfo->m_pBaseClasses[0].m_pClass, keyValueMap);
}

static void InitSchemaKeyValueMap(SchemaClassInfoData_t *pClassInfo, SchemaKeyValueMap_t &keyValueMap) {
    const auto fieldSize = pClassInfo->m_nFieldCount;
    auto *pFields = pClassInfo->m_pFields;

    for (auto i = 0; i < fieldSize; i++) {
        auto &field = pFields[i];

        std::pair<uint32_t, SchemaKey> keyValuePair;
        keyValuePair.first = hash_32_fnv1a_const(field.m_pszName);
        keyValuePair.second.Offset = field.m_nSingleInheritanceOffset;
        keyValuePair.second.Networked = IsFieldNetworked(field);

        keyValueMap.insert(keyValuePair);
    }

    if (!keyValueMap.contains(g_ChainKey) && pClassInfo->m_nBaseClassCount > 0)
        InitChainOffset(pClassInfo->m_pBaseClasses[0].m_pClass, keyValueMap);
}

// A database read with a stale SDK layout does not hold the class info it was reached through
static bool ContainsClassInfo(const CNetworkSerializerCodeGenDatabase *pDatabase, const CNetworkSerializerClassInfo *pClassInfo) {
    auto index = pDatabase->m_ClassInfos.Find(pClassInfo->m_pszClassName.Get());

    return index != pDatabase->m_ClassInfos.InvalidIndex() && pDatabase->m_ClassInfos.Element(index) == pClassInfo;
}

// Deadlock 6711 removed MNetworkEnable from the schema. What a class networks is now only in the server's network
// serializer database, which is reached through the serializer class info of any entity.
static const CNetworkSerializerCodeGenDatabase *GetNetworkDatabase() {
    static const CNetworkSerializerCodeGenDatabase *pDatabase = nullptr;
    static bool warned = false;
    if (pDatabase) return pDatabase;

    auto *pSystem = GameEntitySystem();
    if (!pSystem) return nullptr;

    for (auto *pIdentity = pSystem->m_EntityList.m_pFirstActiveEntity; pIdentity; pIdentity = pIdentity->m_pNext) {
        if (!pIdentity->m_pInstance) continue;

        auto *pClassInfo = pIdentity->m_pInstance->GetSerializerClassInfo();
        if (!pClassInfo) continue;

        auto *pCandidate = pClassInfo->m_pDatabase;

        if (!pCandidate || !ContainsClassInfo(pCandidate, pClassInfo)) {
            if (!warned)
                g_Log->Warning("GetNetworkDatabase(): the database of '{}' does not hold it, CNetworkSerializerClassInfo is out of date", pClassInfo->m_pszClassName.Get());
            warned = true;
            return nullptr;
        }

        pDatabase = pCandidate;
        return pDatabase;
    }

    return nullptr;
}

static const std::unordered_set<uint32_t> *GetNetworkedFields(const char *className, uint32_t classKey) {
    static std::map<uint32_t, std::unordered_set<uint32_t>> networkedFieldsMap;

    if (networkedFieldsMap.contains(classKey))
        return &networkedFieldsMap[classKey];

    auto *pDatabase = GetNetworkDatabase();
    if (!pDatabase) return nullptr;

    auto &networkedFields = networkedFieldsMap[classKey];

    auto index = pDatabase->m_ClassInfos.Find(className);
    if (index == pDatabase->m_ClassInfos.InvalidIndex())
        return &networkedFields;

    auto *pClassInfo = pDatabase->m_ClassInfos.Element(index);

    for (auto i = 0; i < pClassInfo->m_Fields.Count(); i++)
        networkedFields.insert(hash_32_fnv1a_const(pClassInfo->m_Fields[i]->m_pszFieldName.Get()));

    return &networkedFields;
}

static schema::FieldNetworking GetFieldNetworking(const char *className, uint32_t classKey, uint32_t memberKey, bool schemaNetworked) {
    if (schemaNetworked)
        return schema::FieldNetworking::Yes;

    auto *pNetworkedFields = GetNetworkedFields(className, classKey);
    if (!pNetworkedFields)
        return schema::FieldNetworking::Unknown;

    return pNetworkedFields->contains(memberKey) ? schema::FieldNetworking::Yes : schema::FieldNetworking::No;
}

static bool InitSchemaFieldsForClass(SchemaTableMap_t &tableMap, const char *className, uint32_t classKey) {
    auto *pType = g_pSchemaSystem->FindTypeScopeForModule("server.dll");
    if (!pType) return false;

    auto *pClassInfo = pType->FindDeclaredClass(className).Get();

    if (!pClassInfo) {
        SchemaKeyValueMap_t map;
        tableMap.insert({classKey, map});
        g_Log->Warning("InitSchemaFieldsForClass(): Schema class '{}' not found", className);
        return false;
    }

    auto &keyValueMap = tableMap.insert({classKey, {}}).first->second;

    InitSchemaKeyValueMap(pClassInfo, keyValueMap);

    return true;
}

namespace schema {
int16_t FindChainOffset(const char *className, uint32_t classNameHash) {
    return GetOffset(className, classNameHash, "__m_pChainEntity", g_ChainKey).Offset;
}

static SchemaTableMap_t &TableMap() {
    static SchemaTableMap_t schemaTableMap;
    return schemaTableMap;
}

SchemaKey GetOffset(const char *className, uint32_t classKey, const char *memberName, uint32_t memberKey) {
    auto &schemaTableMap = TableMap();

    if (!schemaTableMap.contains(classKey)) {
        if (InitSchemaFieldsForClass(schemaTableMap, className, classKey))
            return GetOffset(className, classKey, memberName, memberKey);
        return {0, 0};
    }

    auto &tableMap = schemaTableMap[classKey];

    if (!tableMap.contains(memberKey)) {
        if (memberKey != g_ChainKey)
            g_Log->Warning("GetOffset(): '{}' not found in '{}'", memberName, className);
        return {0, 0};
    }

    // Callers keep the key, so a field whose networking is not known yet counts as networked
    auto key = tableMap[memberKey];
    key.Networked = GetFieldNetworking(className, classKey, memberKey, key.Networked) != FieldNetworking::No;

    return key;
}

FieldNetworking FieldNetworked(const char *className, uint32_t classKey, uint32_t memberKey) {
    auto &schemaTableMap = TableMap();

    if (!schemaTableMap.contains(classKey) && !InitSchemaFieldsForClass(schemaTableMap, className, classKey))
        return FieldNetworking::No;

    auto &tableMap = schemaTableMap[classKey];

    if (!tableMap.contains(memberKey))
        return FieldNetworking::No;

    return GetFieldNetworking(className, classKey, memberKey, tableMap[memberKey].Networked);
}

int GetClassSize(const char *className) {
    auto *pType = g_pSchemaSystem->FindTypeScopeForModule("server.dll");
    if (!pType) return 0;

    auto *pClassInfo = pType->FindDeclaredClass(className).Get();
    if (!pClassInfo) return 0;

    return pClassInfo->m_nSize;
}

static bool ClassInfoDerivesFrom(SchemaClassInfoData_t *pClassInfo, std::string_view baseClassName) {
    if (pClassInfo->m_pszName == baseClassName)
        return true;
    for (auto i = 0; i < pClassInfo->m_nBaseClassCount; i++) {
        if (ClassInfoDerivesFrom(pClassInfo->m_pBaseClasses[i].m_pClass, baseClassName))
            return true;
    }
    return false;
}

bool IsDerivedFrom(const char *className, const char *baseClassName) {
    static std::map<uint64_t, bool> cache;

    const auto key = (static_cast<uint64_t>(hash_32_fnv1a_const(className)) << 32) | hash_32_fnv1a_const(baseClassName);
    if (const auto it = cache.find(key); it != cache.end())
        return it->second;

    auto *pType = g_pSchemaSystem->FindTypeScopeForModule("server.dll");
    if (!pType) return false;

    auto *pClassInfo = pType->FindDeclaredClass(className).Get();
    const bool derives = pClassInfo && ClassInfoDerivesFrom(pClassInfo, baseClassName);
    cache.emplace(key, derives);
    return derives;
}
} // namespace schema

void NetworkVarStateChanged(uintptr_t pNetworkVar, uint32_t nOffset, uint32_t nNetworkStateChangedOffset) {
    NetworkStateChanged_t data(nOffset);
    CallVirtual<void>(reinterpret_cast<void*>(pNetworkVar), nNetworkStateChangedOffset, &data);
}

void EntityNetworkStateChanged(uintptr_t pEntity, uint32_t nOffset) {
    NetworkStateChanged_t data(nOffset);
    reinterpret_cast<CEntityInstance *>(pEntity)->NetworkStateChanged(NetworkStateChanged_t(nOffset));
}

void ChainNetworkStateChanged(uintptr_t pNetworkVarChainer, uint32_t nLocalOffset) {
    CEntityInstance *pEntity = reinterpret_cast<CNetworkVarChainer *>(pNetworkVarChainer)->GetObject();

    if (pEntity)
        pEntity->NetworkStateChanged(NetworkStateChanged_t(nLocalOffset, -1, reinterpret_cast<CNetworkVarChainer *>(pNetworkVarChainer)->m_PathIndex));
}
