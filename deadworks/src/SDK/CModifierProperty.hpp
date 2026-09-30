#pragma once

#include "Schema/Schema.hpp"
#include "CBaseEntity.hpp"

#include "../Memory/MemoryDataLoader.hpp"

struct CModifierVData {};
class KeyValues3;

class CModifierProperty {
    DECLARE_SCHEMA_CLASS(CModifierProperty);
    SCHEMA_FIELD(bool, m_bPredictedOwner);

    void *AddModifier(CBaseEntity *pCaster, uint32_t hAbility, int iTeam,
                      CModifierVData *vdata, KeyValues3 *pParams = nullptr, KeyValues3 *pKV = nullptr) {
        static const auto fn = reinterpret_cast<void *(__fastcall *)(void *, CBaseEntity *, uint32_t, int, CModifierVData *, KeyValues3 *, KeyValues3 *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CModifierProperty::AddModifier").value());
        return fn(this, pCaster, hAbility, iTeam, vdata, pParams, pKV);
    }
};
