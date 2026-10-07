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

// Deadlock 6711 took MNetworkEnable and MNetworkVarNames out of the schema, so the schema no longer says which fields
// are networked. That is now only in the server's network serializer database (what upstream DumpSource2 dumps as
// network classes). Every entity reaches the database through its serializer class info, so it is taken from the
// first entity there is; before any entity exists it cannot be reached and the answer is not known yet.
static bool ReadDatabase(CEntityInstance *entity, const CNetworkSerializerCodeGenDatabase **database, const char **problem) {
    // These SDK types go out of date with game updates, so what is read is checked before it is trusted.
    __try {
        const auto *info = entity->GetSerializerClassInfo();
        if (!info) return false;
        const auto *candidate = info->m_pDatabase;
        if (!candidate || candidate->m_ClassInfos.Count() == 0) {
            *problem = "the serializer class info has no database";
            return false;
        }
        auto index = candidate->m_ClassInfos.Find(info->m_pszClassName.Get());
        if (index == candidate->m_ClassInfos.InvalidIndex() || candidate->m_ClassInfos.Element(index) != info) {
            *problem = "the database does not hold the class it was reached through";
            return false;
        }
        *database = candidate;
        return true;
    } __except (1) {
        *problem = "reading it faulted";
        return false;
    }
}

static const CNetworkSerializerCodeGenDatabase *NetworkDatabase() {
    static const CNetworkSerializerCodeGenDatabase *database = nullptr;
    static bool warned = false;
    if (database) return database;

    auto *system = GameEntitySystem();
    if (!system) return nullptr;

    for (auto *identity = system->m_EntityList.m_pFirstActiveEntity; identity; identity = identity->m_pNext) {
        if (!identity->m_pInstance) continue;
        const char *problem = nullptr;
        if (ReadDatabase(identity->m_pInstance, &database, &problem)) {
            g_Log->Info("Networked fields come from the network serializer database ({} classes)", database->m_ClassInfos.Count());
            return database;
        }
        if (problem && !warned) {
            warned = true;
            g_Log->Warning("Cannot read the network serializer database: {}. CNetworkSerializerClassInfo in the SDK needs "
                           "updating; until then every schema write is announced as if its field were networked.", problem);
        }
        if (problem) return nullptr;
    }
    return nullptr;
}

// The fields of a class that the database lists, by name hash. Null while the database cannot be reached.
static const std::unordered_set<uint32_t> *NetworkedFieldsOf(const char *className, uint32_t classKey) {
    static std::map<uint32_t, std::unordered_set<uint32_t>> classes;
    if (auto found = classes.find(classKey); found != classes.end()) return &found->second;

    const auto *database = NetworkDatabase();
    if (!database) return nullptr;

    auto &fields = classes[classKey];
    auto index = database->m_ClassInfos.Find(className);
    if (index == database->m_ClassInfos.InvalidIndex()) return &fields;

    const auto *info = database->m_ClassInfos.Element(index);
    for (int i = 0; i < info->m_Fields.Count(); i++)
        if (const auto *field = info->m_Fields[i])
            fields.insert(hash_32_fnv1a_const(field->m_pszFieldName.Get()));
    return &fields;
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

    // A caller that keeps the key keeps this answer, so while it is not known the field counts as networked:
    // announcing a write nobody listens for costs little, and not announcing one loses it.
    SchemaKey key = tableMap[memberKey];
    key.Networked = FieldNetworked(className, classKey, memberKey) != NotNetworked;
    return key;
}

FieldNetworking FieldNetworked(const char *className, uint32_t classKey, uint32_t memberKey) {
    auto &schemaTableMap = TableMap();
    if (!schemaTableMap.contains(classKey) && !InitSchemaFieldsForClass(schemaTableMap, className, classKey))
        return NotNetworked;

    auto &tableMap = schemaTableMap[classKey];
    auto found = tableMap.find(memberKey);
    if (found == tableMap.end()) return NotNetworked;
    if (found->second.Networked) return Networked;   // the schema says so itself: builds before 6711

    const auto *fields = NetworkedFieldsOf(className, classKey);
    if (!fields) return NetworkingUnknown;
    return fields->contains(memberKey) ? Networked : NotNetworked;
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
