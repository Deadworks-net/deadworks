#pragma once

#include <entityinstance.h>

#include "Schema/Schema.hpp"

class CModifierProperty;

struct CEntitySubclassVDataBase {
    void **vft;
    void *unk;
    const char *m_pszName;
    uint8_t pad[0x10];
};

class CBaseEntity : public CEntityInstance {
    DECLARE_SCHEMA_CLASS(CBaseEntity);
    SCHEMA_FIELD(int32_t, m_iHealth);
    SCHEMA_FIELD(uint8_t, m_iTeamNum);
    SCHEMA_FIELD(CModifierProperty *, m_pModifierProp);
    SCHEMA_FIELD(uint32_t, m_nSubclassID);

    // m_pSubclassVData is not exposed through the schema - it lives right after
    // m_nSubclassID (CUtlStringToken, 4 bytes). Null for entities without a subclass.
    CEntitySubclassVDataBase *GetSubclassVData() {
        auto fieldAddr = reinterpret_cast<uintptr_t>(&m_nSubclassID.Get());
        return *reinterpret_cast<CEntitySubclassVDataBase **>(fieldAddr + sizeof(uint32_t));
    }
};
