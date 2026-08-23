#include "Schema.hpp"

#include <schemasystem/schemasystem.h>
#include <entity2/entityinstance.h>
#include <entity2/entitynetwork.h>

#include <map>
#include <string_view>

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

SchemaKey GetOffset(const char *className, uint32_t classKey, const char *memberName, uint32_t memberKey) {
    static SchemaTableMap_t schemaTableMap;

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

    return tableMap[memberKey];
}
int GetClassSize(const char *className) {
    auto *pType = g_pSchemaSystem->FindTypeScopeForModule("server.dll");
    if (!pType) return 0;

    auto *pClassInfo = pType->FindDeclaredClass(className).Get();
    if (!pClassInfo) return 0;

    return pClassInfo->m_nSize;
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
