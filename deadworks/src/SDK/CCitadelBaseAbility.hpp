#pragma once

#include "Schema/Schema.hpp"
#include "CBaseEntity.hpp"

#include "../Memory/MemoryDataLoader.hpp"

class CCitadelBaseAbility : public CBaseEntity {
    DECLARE_SCHEMA_CLASS(CCitadelBaseAbility);
    SCHEMA_FIELD(uint16_t, m_eAbilitySlot);

    void ToggleActivate(char activate) {
        static const auto fn = reinterpret_cast<void(__fastcall *)(void *, char)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelBaseAbility::ToggleActivate").value());
        fn(this, activate);
    }

    void SetUpgradeBits(int newBits) {
        static const auto fn = reinterpret_cast<void(__fastcall *)(void *, int)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelBaseAbility::SetUpgradeBits").value());
        fn(this, newBits);
    }

    // Imbues this item into pTargetAbility: appends the target's subclass ID to
    // m_vecImbuedAbilities, refreshes the item's modifiers and propagates the imbuement to
    // any ability that links to the target. A no-op when already imbued into that ability.
    // Callers must validate with CitadelAbilityVData::CanImbueAbility first - the engine
    // does not re-check here.
    void ImbueAbility(void *pTargetAbility) {
        static const auto fn = reinterpret_cast<void(__fastcall *)(void *, void *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelBaseAbility::ImbueAbility").value());
        fn(this, pTargetAbility);
    }
};
