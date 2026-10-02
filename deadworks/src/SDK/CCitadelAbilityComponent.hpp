#pragma once

#include "Schema/Schema.hpp"
#include "CBaseEntity.hpp"
#include <tier1/utlvector.h>

#include "../Memory/MemoryDataLoader.hpp"

class CCitadelAbilityComponent {
    DECLARE_SCHEMA_CLASS(CCitadelAbilityComponent);
    SCHEMA_FIELD(CUtlVector<uint32_t>, m_vecAbilities);
    SCHEMA_FIELD(bool, m_bThinkableAbilitiesDirty);

    CBaseEntity *FindAbilityByName(const char *name) {
        static const auto fn = reinterpret_cast<CBaseEntity *(__fastcall *)(void *, const char *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::FindAbilityByName").value());
        return fn(this, name);
    }

    // (component, def, slot, upgrade, flag, extra spawn keyvalues). Since 6711 the upgrade argument is
    // 64-bit: the low word is the initial upgrade bits (SetUpgradeBits), the high dword goes to a second
    // setter on the ability and is 0 from the engine's own callers. They pass true for the flag, which the
    // function does not read. The last argument is an optional keyvalues node merged into the spawn keyvalues.
    void *CreateAndRegisterAbility(void *def, uint16_t slot, uint16_t upgradeBits = 0) {
        static const auto fn = reinterpret_cast<void *(__fastcall *)(void *, void *, uint16_t, uint64_t, bool, void *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::CreateAndRegisterAbility").value());
        return fn(this, def, slot, upgradeBits, true, nullptr);
    }

    int ExecuteAbilityBySlot(int16_t slot, char altCast = 0, uint8_t flags = 0) {
        static const auto fn = reinterpret_cast<int(__fastcall *)(void *, int16_t, char, uint8_t)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::ExecuteAbilityBySlot").value());
        return fn(this, slot, altCast, flags);
    }

    int ExecuteAbilityByID(int abilityID, char altCast = 0, uint8_t flags = 0) {
        static const auto fn = reinterpret_cast<int(__fastcall *)(void *, int, char, uint8_t)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::ExecuteAbilityByID").value());
        return fn(this, abilityID, altCast, flags);
    }

    int ExecuteAbility(void *ability, char altCast = 0, uint8_t flags = 0) {
        static const auto fn = reinterpret_cast<int(__fastcall *)(void *, void *, char, uint8_t)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::ExecuteAbility").value());
        return fn(this, ability, altCast, flags);
    }

    void *GetAbilityBySlot(int16_t slot) {
        static const auto fn = reinterpret_cast<void *(__fastcall *)(void *, int16_t)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelAbilityComponent::GetAbilityBySlot").value());
        return fn(this, slot);
    }
};
