#pragma once

#include "Schema/Schema.hpp"
#include "CitadelAbilityProperty.hpp"
#include <tier1/utlmap.h>

#include "../Memory/MemoryDataLoader.hpp"

using AbilityPropertyMap_t = CUtlOrderedMap<CUtlString, CitadelAbilityProperty_t>;

// ECitadelTargetAbilityEffects - what an item does to the ability it gets imbued into.
// None means the item declares no imbue behaviour at all, i.e. it cannot be imbued.
enum class ECitadelTargetAbilityEffects : uint32_t {
    None = 0x0,
    ImbueModifierValue = 0x1,
    ImbueActive = 0x2,
    ImbueActiveNonUlt = 0x4,
};

class CitadelAbilityVData {
    DECLARE_SCHEMA_CLASS(CitadelAbilityVData);
    SCHEMA_FIELD_POINTER(AbilityPropertyMap_t, m_mapAbilityProperties);
    SCHEMA_FIELD(ECitadelTargetAbilityEffects, m_TargetAbilityEffectsToApply);

    // True when this VData belongs to an item that has to pick an ability to imbue.
    bool CanBeImbued() {
        return m_TargetAbilityEffectsToApply.Get() != ECitadelTargetAbilityEffects::None;
    }

    // `this` is the VData of the ability being imbued into, pItemVData the item doing the
    // imbuing. Rejects e.g. imbuing an active-only item into a passive or into an ultimate.
    // This is the same check `giveitem <item> <slot>` runs before handing the item out.
    bool CanImbueAbility(CitadelAbilityVData *pItemVData) {
        static const auto fn = reinterpret_cast<bool(__fastcall *)(void *, void *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CitadelAbilityVData::CanImbueAbility").value());
        return fn(this, pItemVData);
    }
};
