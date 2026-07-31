#pragma once

#include <cstdint>

namespace deadworks::offsets {

// CCitadelAbilityComponent
constexpr uintptr_t kAbilityCompSlotTable = 0x30;

// CEntitySubclassVDataBase
constexpr uintptr_t kSubclassDefType = 0x28;
constexpr uintptr_t kSubclassDefDisabled = 0x2A;

// Vtable

// CBaseEntity
constexpr int kVtblTeleport = 163;

// CBaseEntity
constexpr int kVtblHeal = 123;
constexpr int kVtblGetMaxHealth = 181;
constexpr int kVtblSetScale = 246;

// CBaseModifier
constexpr int kVtblModifierDestroy = 22;

// CBasePlayerController
constexpr int kVtblChangeTeam = 103;

// CCitadelAbilityComponent::OnAbilityRemoved
constexpr uintptr_t kOnAbilityRemoved_FindSlotCall = 0x7D;
constexpr uintptr_t kOnAbilityRemoved_RemoveSlotCall = 0x8D;

// CHeroDefinitionManager::GetManagerAnchor
// The manager getter is a generic TLS-guarded singleton stub that cannot be
// signatured directly, so it is resolved from the E8 call inside this anchor.
constexpr uintptr_t kHeroDefMgrAnchor_GetManagerCall = 0xE;

// CCitadelGameRules::BuildGameSessionManifest
constexpr uintptr_t kBGSM_GetHeroTableCall = 0x2BB;
constexpr uintptr_t kBGSM_PrecacheGlobalLea = 0x389;
constexpr uintptr_t kBGSM_PrecacheCall = 0x394;

} // namespace deadworks::offsets

namespace deadworks {

/// Resolve an x86-64 E8 relative CALL instruction at `callAddr` to an absolute target.
/// E8 encoding: [E8 rel32] - 5 bytes total. Target = callAddr + 5 + rel32.
inline uintptr_t ResolveE8Call(uintptr_t callAddr) {
    int32_t rel = *reinterpret_cast<int32_t *>(callAddr + 1);
    return callAddr + 5 + rel;
}

/// Resolve a 7-byte LEA instruction (REX.W + 8D + ModRM + disp32) at `leaAddr`
/// to the absolute address of the referenced data.
/// Encoding: [48 8D xx rel32] - 7 bytes total. Target = leaAddr + 7 + rel32.
inline uintptr_t ResolveLea(uintptr_t leaAddr) {
    int32_t rel = *reinterpret_cast<int32_t *>(leaAddr + 3);
    return leaAddr + 7 + rel;
}

} // namespace deadworks
