#pragma once

#include "Schema/Schema.hpp"
#include "CBaseEntity.hpp"

#include "../Lib/Virtual.hpp"
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

    // Clears m_flCooldownStart/End. Virtual because some abilities also reset their own cast state here.
    void EndCooldown() {
        static const auto idx = deadworks::MemoryDataLoader::Get().GetVirtual("CCitadelBaseAbility::EndCooldown").value();
        CallVirtual<void>(this, static_cast<uint32_t>(idx));
    }

    bool UsesCharges() {
        static const auto idx = deadworks::MemoryDataLoader::Get().GetVirtual("CCitadelBaseAbility::UsesCharges").value();
        return CallVirtual<uint8_t>(this, static_cast<uint32_t>(idx)) != 0;
    }

    // Adds to m_iRemainingCharges, capped at GetMaxCharges(), and runs the ability's charges-changed hook.
    void AddCharges(int count) {
        static const auto idx = deadworks::MemoryDataLoader::Get().GetVirtual("CCitadelBaseAbility::AddCharges").value();
        CallVirtual<void>(this, static_cast<uint32_t>(idx), count);
    }

    // Includes charges added by upgrades and items.
    int GetMaxCharges() {
        static const auto idx = deadworks::MemoryDataLoader::Get().GetVirtual("CCitadelBaseAbility::GetMaxCharges").value();
        return CallVirtual<int>(this, static_cast<uint32_t>(idx));
    }
};
