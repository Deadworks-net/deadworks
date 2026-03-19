#include "NativeAbility.hpp"
#include "NativeOffsets.hpp"
#include "Deadworks.hpp"

#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CBaseEntity.hpp"
#include "../SDK/CBaseModifier.hpp"
#include "../SDK/CCitadelBaseAbility.hpp"
#include "../SDK/CCitadelModifierVData.hpp"
#include "../SDK/CitadelAbilityProperty.hpp"
#include "../SDK/CitadelAbilityVData.hpp"
#include "../SDK/CCitadelPlayerPawn.hpp"
#include "../SDK/CEntitySystem.hpp"
#include "../SDK/CModifierProperty.hpp"
#include "../SDK/Core.hpp"
#include "../SDK/Util.hpp"

#include <tier0/utlstring.h>
#include <tier0/utlstringtoken.h>
#include <tier1/convar.h>
#include <tier1/keyvalues3.h>
#include <tier1/utlvector.h>
#include <safetyhook.hpp>
#include <cstring>

#include "Hooks/CCitadelPlayerController.hpp"

#include <tier0/memdbgon.h>

using namespace deadworks;
using namespace deadworks::offsets;

// --- Ability system types ---

using FindSlotEntryFn = int(__fastcall *)(void *slotTable, uint16_t *slot);
using RemoveSlotEntryFn = void(__fastcall *)(void *slotTable, int entryIndex);
using LookupSubclassDefFn = void *(__fastcall *)(int typeIndex, const char *name);

// --- Modifier system types ---

enum class EntitySubclassScope_t : uint32_t {
    SUBCLASS_SCOPE_NONE = 0xFFFFFFFF,
    SUBCLASS_SCOPE_MISC = 0x0,
    SUBCLASS_SCOPE_PRECIPITATION = 0x1,
    SUBCLASS_SCOPE_MODIFIERS = 0x2,
    SUBCLASS_SCOPE_NPC_UNITS = 0x3,
    SUBCLASS_SCOPE_ABILITIES = 0x4,
    SUBCLASS_SCOPE_SCALE_FUNCTIONS = 0x5,
    SUBCLASS_SCOPE_LOOT_TABLES = 0x6,
    SUBCLASS_SCOPE_COUNT = 0x7,
};

// CModifierVData is forward-declared in CModifierProperty.hpp

// --- Modifier ability value override system ---
//
// Modifiers read ability property values LAZILY every tick via m_nAbilitySubclassID
// → ability VData lookup → m_mapAbilityProperties[propertyName].
//
// For per-instance overrides, we clone the ability VData, modify the property
// values in the clone, register it under a unique hash, and set the modifier's
// m_nAbilitySubclassID to that hash. Each modifier reads from its own VData clone.
//
// The modifier VData's auto-register list (m_vecAutoRegisterModifierValueFromAbilityPropertyName)
// is temporarily expanded to include any override properties not already in the list.

namespace {

struct ModifierOverrideState {
    const char *const *names = nullptr;
    const float *values = nullptr;
    int32_t count = 0;
    uint32_t abilityHash = 0;
    uint32_t clonedVDataHash = 0;
};
thread_local ModifierOverrideState g_modOverride;

safetyhook::InlineHook g_Hook_AutoRegisterValues;
safetyhook::InlineHook g_Hook_LookupVDataByHash;

// Cloned VData storage. Each per-instance modifier gets its own VData clone.
std::unordered_map<uint32_t, void *> g_clonedVDataMap;
std::atomic<uint32_t> g_nextCloneHash{0xDE000001};

static std::string ExtractParentAbilityName(const char *scopedName) {
    const char *slash = strchr(scopedName, '/');
    if (!slash) return {};
    return std::string(scopedName, slash - scopedName);
}

// Hook for VData lookup — intercepts lookups for our cloned VData entries.
static __int64 __fastcall Hook_LookupVDataByHash(int typeIndex, int hash) {
    if (hash != 0) {
        auto it = g_clonedVDataMap.find(static_cast<uint32_t>(hash));
        if (it != g_clonedVDataMap.end())
            return reinterpret_cast<__int64>(it->second);
    }
    return g_Hook_LookupVDataByHash.call<__int64>(typeIndex, hash);
}

// Clone ability VData and modify property values for per-instance overrides.
// Accesses CitadelAbilityVData::m_mapAbilityProperties (CUtlOrderedMap) via schema,
// then iterates AbilityPropertyMapEntry/CitadelAbilityProperty_t to find and override values.
static uint32_t CloneAbilityVDataWithOverrides(
    uint32_t abilityHash,
    const char *const *overrideNames, const float *overrideValues, int32_t overrideCount)
{
    auto originalVData = g_Hook_LookupVDataByHash.call<__int64>(4, static_cast<int>(abilityHash));
    if (!originalVData) {
        g_Log->Warning("CloneVData: ability VData not found for hash 0x{:X}", abilityHash);
        return 0;
    }

    // We don't know the exact VData subclass size at runtime without RTTI.
    // Use a generous fixed buffer that covers all known ability VData subclasses.
    constexpr int kVDataCloneSize = 16384;

    auto *clone = reinterpret_cast<uint8_t *>(malloc(kVDataCloneSize));
    if (!clone) return 0;
    memcpy(clone, reinterpret_cast<void *>(originalVData), kVDataCloneSize);

    // Deep-copy the tree's node array so modifications don't affect the original.
    // We use the original map's SDK API for iteration (safe tree state), and manually
    // deep-copy the node array for the clone. CopyFrom doesn't work because the
    // engine's CUtlString keys use interned storage that doesn't survive re-copy.
    auto *originalVDataTyped = reinterpret_cast<CitadelAbilityVData *>(originalVData);
    auto *origMap = originalVDataTyped->m_mapAbilityProperties.Get();
    if (!origMap || origMap->Count() <= 0) {
        free(clone);
        return 0;
    }

    // The map's internal CUtlRBTree node array: count at map+0x08, data ptr at map+0x10.
    auto *cloneVData = reinterpret_cast<CitadelAbilityVData *>(clone);
    auto *cloneMapBytes = reinterpret_cast<uint8_t *>(cloneVData->m_mapAbilityProperties.Get());
    int nodeCount = *reinterpret_cast<int *>(cloneMapBytes + 0x08);
    auto *origNodeData = *reinterpret_cast<uint8_t **>(cloneMapBytes + 0x10);

    // Node stride: tree metadata (8 bytes) + CUtlString key + CitadelAbilityProperty_t elem
    constexpr int kTreeNodeMeta = 8;
    int nodeStride = kTreeNodeMeta + static_cast<int>(sizeof(CUtlString)) + static_cast<int>(sizeof(CitadelAbilityProperty_t));
    auto *clonedNodes = static_cast<uint8_t *>(malloc(static_cast<size_t>(nodeCount) * nodeStride));
    memcpy(clonedNodes, origNodeData, static_cast<size_t>(nodeCount) * nodeStride);
    *reinterpret_cast<uint8_t **>(cloneMapBytes + 0x10) = clonedNodes;

    // Iterate original map (SDK API), apply overrides to cloned node array by offset.
    for (int i = 0; i < origMap->MaxElement(); i++) {
        if (!origMap->IsValidIndex(i)) continue;

        const CUtlString &key = origMap->Key(i);
        for (int oi = 0; oi < overrideCount; oi++) {
            if (overrideNames[oi] && _stricmp(key.Get(), overrideNames[oi]) == 0) {
                auto &origElem = origMap->Element(i);
                auto offset = reinterpret_cast<uintptr_t>(&origElem) - reinterpret_cast<uintptr_t>(origNodeData);
                float *parsed = reinterpret_cast<CitadelAbilityProperty_t *>(clonedNodes + offset)->As().GetParsedFloats();
                g_Log->Info("ModifierOverride: {}={} (was {})", overrideNames[oi], overrideValues[oi], parsed[0]);
                parsed[0] = overrideValues[oi];
                parsed[1] = overrideValues[oi];
                break;
            }
        }
    }

    uint32_t cloneHash = g_nextCloneHash.fetch_add(1);
    g_clonedVDataMap[cloneHash] = clone;
    return cloneHash;
}

// Hook for the auto-register processor — fixes up m_nAbilitySubclassID and
// temporarily expands the auto-register property list for override entries.
static __int64 __fastcall Hook_AutoRegisterValues(__int64 *modifierRaw) {
    auto &ov = g_modOverride;

    if (ov.abilityHash == 0 || ov.count <= 0 || !ov.names)
        return g_Hook_AutoRegisterValues.call<__int64>(modifierRaw);

    auto *modifier = reinterpret_cast<CBaseModifier *>(modifierRaw);
    uint32_t savedSubclassID = modifier->m_nAbilitySubclassID.Get();

    if (savedSubclassID == 0 && ov.abilityHash != 0)
        modifier->m_nAbilitySubclassID = ov.abilityHash;

    // Temporarily expand the modifier VData's auto-register list with any
    // override property names not already present (e.g., "DebuffAccuracy").
    // The modifier's VData pointer is at +0x10 (not in schema, internal field).
    auto *modVData = *reinterpret_cast<CCitadelModifierVData **>(
        reinterpret_cast<uintptr_t>(modifierRaw) + 0x10);
    auto &autoRegVec = modVData->m_vecAutoRegisterModifierValueFromAbilityPropertyName.Get();

    int extraCount = 0;
    if (modVData && ov.count > 0 && ov.names) {
        for (int oi = 0; oi < ov.count; oi++) {
            if (!ov.names[oi]) continue;
            bool found = false;
            for (int ri = 0; ri < autoRegVec.Count(); ri++) {
                if (V_stricmp(autoRegVec[ri].Get(), ov.names[oi]) == 0) {
                    found = true;
                    break;
                }
            }
            if (!found) {
                autoRegVec.AddToTail(CUtlString(ov.names[oi]));
                ++extraCount;
            }
        }
    }

    auto result = g_Hook_AutoRegisterValues.call<__int64>(modifierRaw);

    // Remove the temporarily added entries
    if (extraCount > 0)
        autoRegVec.RemoveMultipleFromTail(extraCount);

    // Set the cloned VData hash for per-instance lazy reads
    if (ov.clonedVDataHash != 0)
        modifier->m_nAbilitySubclassID = ov.clonedVDataHash;
    else if (savedSubclassID != 0)
        modifier->m_nAbilitySubclassID = savedSubclassID;

    return result;
}

} // namespace

// ---------------------------------------------------------------------------
// Resolved wrappers
// ---------------------------------------------------------------------------

static void *LookupSubclassDefinitionByName(EntitySubclassScope_t scope, const char *name) {
    static const auto fn = reinterpret_cast<LookupSubclassDefFn>(
        MemoryDataLoader::Get().GetOffset("GetVDataInstanceByName").value());
    return fn(static_cast<int>(scope), name);
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void ResolveSlotTableFns(FindSlotEntryFn &findFn, RemoveSlotEntryFn &removeFn) {
    auto addr = MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::OnAbilityRemoved").value();
    findFn = reinterpret_cast<FindSlotEntryFn>(ResolveE8Call(addr + kOnAbilityRemoved_FindSlotCall));
    removeFn = reinterpret_cast<RemoveSlotEntryFn>(ResolveE8Call(addr + kOnAbilityRemoved_RemoveSlotCall));
    g_Log->Info("SlotTable: FindEntry={} RemoveEntry={}", (void *)findFn, (void *)removeFn);
}

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

static uint8_t __cdecl NativeRemoveAbility(void *pawn, const char *abilityName) {
    if (!pawn || !abilityName)
        return 0;

    auto *pPawn = static_cast<CCitadelPlayerPawn *>(pawn);
    auto *comp = pPawn->m_CCitadelAbilityComponent.Get();
    auto compAddr = reinterpret_cast<uintptr_t>(comp);

    auto *ability = static_cast<CCitadelBaseAbility *>(comp->FindAbilityByName(abilityName));
    if (!ability) {
        g_Log->Info("RemoveAbility: FindAbilityByName('{}') returned null", abilityName);
        return 0;
    }

    uint32_t rawHandle = static_cast<uint32_t>(ability->GetRefEHandle().ToInt());
    g_Log->Info("RemoveAbility: found '{}' handle=0x{:X}", abilityName, rawHandle);

    static FindSlotEntryFn findSlotFn = nullptr;
    static RemoveSlotEntryFn removeSlotFn = nullptr;
    if (!findSlotFn)
        ResolveSlotTableFns(findSlotFn, removeSlotFn);
    uint16_t slot = ability->m_eAbilitySlot.Get();
    auto *slotTable = reinterpret_cast<void *>(compAddr + kAbilityCompSlotTable);
    int entryIdx = findSlotFn(slotTable, &slot);
    g_Log->Info("RemoveAbility: slot={} entry={}", slot, entryIdx);
    if (entryIdx != -1)
        removeSlotFn(slotTable, entryIdx);

    auto &vecAbilities = comp->m_vecAbilities.Get();
    for (int i = 0; i < vecAbilities.Count(); i++) {
        if (vecAbilities[i] == rawHandle) {
            vecAbilities.Remove(i);
            break;
        }
    }

    auto &vecThinkable = comp->m_vecThinkableAbilities.Get();
    for (int i = 0; i < vecThinkable.Count(); i++) {
        if (vecThinkable[i] == rawHandle) {
            vecThinkable.Remove(i);
            break;
        }
    }

    comp->m_vecAbilities.NetworkStateChanged();
    comp->m_vecThinkableAbilities.NetworkStateChanged();

    UTIL_Remove(static_cast<CEntityInstance *>(ability));
    g_Log->Info("RemoveAbility: done for '{}'", abilityName);
    return 1;
}

static void *__cdecl NativeAddAbility(void *pawn, const char *abilityName, uint16_t slot, int32_t upgradeLevel) {
    if (!pawn || !abilityName)
        return nullptr;

    auto *pPawn = static_cast<CCitadelPlayerPawn *>(pawn);
    auto *comp = pPawn->m_CCitadelAbilityComponent.Get();

    auto *def = LookupSubclassDefinitionByName(EntitySubclassScope_t::SUBCLASS_SCOPE_ABILITIES, abilityName);
    if (!def) {
        g_Log->Info("AddAbility: LookupSubclassDefinitionByName returned null for '{}'", abilityName);
        return nullptr;
    }
    if (*reinterpret_cast<uint8_t *>(reinterpret_cast<uintptr_t>(def) + kSubclassDefDisabled)) {
        g_Log->Info("AddAbility: ability '{}' is disabled", abilityName);
        return nullptr;
    }

    return comp->CreateAndRegisterAbility(def, slot, 0, upgradeLevel);
}

static void *__cdecl NativeAddItem(void *pawn, const char *itemName, int32_t upgradeTier) {
    if (!pawn || !itemName) return nullptr;
    return static_cast<CCitadelPlayerPawn *>(pawn)->AddItem(itemName, 0, upgradeTier);
}

static uint8_t __cdecl NativeSellItem(void *pawn, const char *itemName, uint8_t bFullRefund, uint8_t bForceSellPrice) {
    if (!pawn || !itemName) return 0;
    return static_cast<CCitadelPlayerPawn *>(pawn)->SellItem(itemName, bFullRefund, bForceSellPrice);
}

static void *__cdecl NativeAddModifier(void *entity, const char *modifierName, void *kv3,
                                       void *caster, void *ability, int32_t team,
                                       const char *const *overrideNames, const float *overrideValues, int32_t overrideCount) {
    if (!entity || !modifierName)
        return nullptr;

    auto *vdata = reinterpret_cast<CModifierVData *>(
        LookupSubclassDefinitionByName(EntitySubclassScope_t::SUBCLASS_SCOPE_MODIFIERS, modifierName));
    if (!vdata) {
        g_Log->Error("AddModifier: VData not found for '{}'", modifierName);
        return nullptr;
    }

    auto *ent = static_cast<CBaseEntity *>(entity);
    CModifierProperty *modProp = ent->m_pModifierProp;
    if (!modProp) {
        g_Log->Error("AddModifier: Entity has no modifier property");
        return nullptr;
    }

    bool savedPredictedOwner = modProp->m_bPredictedOwner.Get();
    modProp->m_bPredictedOwner = true;

    auto *pCaster = caster ? static_cast<CBaseEntity *>(caster) : ent;
    uint32_t hAbility = 0xFFFFFFFF;
    if (ability) {
        auto *pAbility = static_cast<CBaseEntity *>(ability);
        hAbility = static_cast<uint32_t>(pAbility->GetRefEHandle().ToInt());
    }

    // Set up per-call overrides for the auto-register hook
    auto &ov = g_modOverride;
    ov.names = overrideNames;
    ov.values = overrideValues;
    ov.count = overrideCount;
    ov.abilityHash = 0;

    // Extract parent ability name hash from scoped modifier name (e.g. "ability_doorman_bomb/debuff")
    if (overrideCount > 0 || hAbility == 0xFFFFFFFF) {
        auto parentName = ExtractParentAbilityName(modifierName);
        if (!parentName.empty()) {
            ov.abilityHash = CUtlStringToken(parentName.c_str(), static_cast<int>(parentName.size()));

            if (overrideCount > 0 && g_Hook_LookupVDataByHash) {
                ov.clonedVDataHash = CloneAbilityVDataWithOverrides(
                    ov.abilityHash, overrideNames, overrideValues, overrideCount);
            }
            g_Log->Info("AddModifier: parent='{}' hash=0x{:X} cloneHash=0x{:X} hookValid={}",
                        parentName, ov.abilityHash, ov.clonedVDataHash,
                        static_cast<bool>(g_Hook_LookupVDataByHash));
        } else {
            g_Log->Info("AddModifier: no scope in '{}', overrides={}", modifierName, overrideCount);
        }
    }

    auto *result = modProp->AddModifier(pCaster, hAbility, team, vdata, static_cast<KeyValues3 *>(kv3));

    // Clear overrides
    ov = {};

    modProp->m_bPredictedOwner = savedPredictedOwner;

    return result;
}

// ---------------------------------------------------------------------------
// Ability execution natives
// ---------------------------------------------------------------------------

static int32_t __cdecl NativeExecuteAbilityBySlot(void *abilityComponent, int16_t slot, uint8_t altCast, uint8_t flags) {
    if (!abilityComponent) return -1;
    return static_cast<CCitadelAbilityComponent *>(abilityComponent)->ExecuteAbilityBySlot(slot, altCast, flags);
}

static int32_t __cdecl NativeExecuteAbilityByID(void *abilityComponent, int32_t abilityID, uint8_t altCast, uint8_t flags) {
    if (!abilityComponent) return -1;
    return static_cast<CCitadelAbilityComponent *>(abilityComponent)->ExecuteAbilityByID(abilityID, altCast, flags);
}

static int32_t __cdecl NativeExecuteAbility(void *abilityComponent, void *ability, uint8_t altCast, uint8_t flags) {
    if (!abilityComponent || !ability) return -1;
    return static_cast<CCitadelAbilityComponent *>(abilityComponent)->ExecuteAbility(ability, altCast, flags);
}

static void *__cdecl NativeGetAbilityBySlot(void *abilityComponent, int16_t slot) {
    if (!abilityComponent) return nullptr;
    return static_cast<CCitadelAbilityComponent *>(abilityComponent)->GetAbilityBySlot(slot);
}

static void __cdecl NativeToggleActivate(void *ability, uint8_t activate) {
    if (!ability) return;
    static_cast<CCitadelBaseAbility *>(ability)->ToggleActivate(activate);
}

static void __cdecl NativeServerControllerCommand(void *controller, const char *command) {
    if (!controller || !command) return;
    CCommand cmd;
    cmd.Tokenize(CUtlString(command));
    hooks::g_CCitadelPlayerController_ClientConCommand.thiscall<char>(controller, &cmd);
}

// ---------------------------------------------------------------------------
// Populate
// ---------------------------------------------------------------------------

void deadworks::PopulateAbilityNatives(NativeCallbacks &cb) {
    cb.RemoveAbility = &NativeRemoveAbility;
    cb.AddAbility = &NativeAddAbility;
    cb.AddItem = &NativeAddItem;
    cb.SellItem = &NativeSellItem;
    cb.AddModifier = &NativeAddModifier;
    cb.ExecuteAbilityBySlot = &NativeExecuteAbilityBySlot;
    cb.ExecuteAbilityByID = &NativeExecuteAbilityByID;
    cb.ExecuteAbility = &NativeExecuteAbility;
    cb.GetAbilityBySlot = &NativeGetAbilityBySlot;
    cb.ToggleActivate = &NativeToggleActivate;
    cb.ServerControllerCommand = &NativeServerControllerCommand;

    // Install hooks for modifier ability value overrides (optional)
    auto opt = MemoryDataLoader::Get().GetOffset("CCitadelModifier::AutoRegisterAbilityValues");
    if (opt) {
        g_Hook_AutoRegisterValues = safetyhook::create_inline(opt.value(), &Hook_AutoRegisterValues);
        g_Log->Info("Hooked CCitadelModifier::AutoRegisterAbilityValues");
    } else {
        g_Log->Warning("CCitadelModifier::AutoRegisterAbilityValues signature not found");
    }

    auto opt2 = MemoryDataLoader::Get().GetOffset("LookupVDataByHash");
    if (opt2) {
        g_Hook_LookupVDataByHash = safetyhook::create_inline(opt2.value(), &Hook_LookupVDataByHash);
        g_Log->Info("Hooked LookupVDataByHash for per-instance modifier value overrides");
    } else {
        g_Log->Warning("LookupVDataByHash signature not found — per-instance overrides unavailable");
    }
}
