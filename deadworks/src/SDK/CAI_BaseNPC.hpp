#pragma once

#include "Schema/Schema.hpp"
#include "CBaseEntity.hpp"

class CAI_BaseNPC : public CBaseEntity {
public:
    DECLARE_SCHEMA_CLASS(CAI_BaseNPC);
    SCHEMA_FIELD(void *, m_pNavigator);
};
