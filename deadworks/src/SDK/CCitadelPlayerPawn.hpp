#pragma once

#include "Schema/Schema.hpp"
#include "CBasePlayerPawn.hpp"
#include "CCitadelAbilityComponent.hpp"
#include "Enums.hpp"

#include "../Memory/MemoryDataLoader.hpp"

class CCitadelPlayerPawn : public CBasePlayerPawn {
public:
    DECLARE_SCHEMA_CLASS(CCitadelPlayerPawn);
    SCHEMA_FIELD_POINTER(CCitadelAbilityComponent, m_CCitadelAbilityComponent);

    void ModifyCurrency(ECurrencyType nCurrencyType, int32_t nAmount, ECurrencySource nSource, bool bSilent, bool bForceGain, bool bSpendOnly, void *pSourceAbility, void *pSourceEntity) {
        static const auto fn = reinterpret_cast<void(__fastcall *)(void *, ECurrencyType, int32_t, ECurrencySource, int32_t, int32_t, int32_t, void *, void *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelPlayerPawn::ModifyCurrency").value());
        fn(this, nCurrencyType, nAmount, nSource, bSilent, bForceGain, bSpendOnly, pSourceAbility, pSourceEntity);
    }

    // Since 6711 the last two arguments go straight to CreateAndRegisterAbility: the 64-bit upgrade value
    // (low word = upgrade bits) and the optional extra spawn keyvalues, which must be null or valid.
    void *AddItem(const char *pszItemName, uint16_t nInitialUpgradeBits) {
        static const auto fn = reinterpret_cast<void *(__fastcall *)(void *, const char *, uint64_t, void *)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelPlayerPawn::AddItem").value());
        return fn(this, pszItemName, nInitialUpgradeBits, nullptr);
    }

    uint8_t SellItem(const char *itemName, uint8_t bFullRefund = 0, uint8_t bForceSellPrice = 0) {
        static const auto fn = reinterpret_cast<uint8_t(__fastcall *)(void *, const char *, uint8_t, uint8_t)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelPlayerPawn::SellItem").value());
        return fn(this, itemName, bFullRefund, bForceSellPrice);
    }
};
