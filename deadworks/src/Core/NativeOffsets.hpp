#pragma once

#include <cstdint>

namespace deadworks::offsets {

// CCitadelAbilityComponent
constexpr uintptr_t kAbilityCompSlotTable = 0x30;

// CCitadelPlayerBot (pawn->m_pBot) and its brain entity, CCitadelPlayerBotNPCBrain. Raw fields, none in
// the schema; the same on 6722 and 6762.
constexpr uintptr_t kBotBrain = 0x4B90;            // CCitadelPlayerBot: brain entity pointer
constexpr uintptr_t kBrainPawn = 0x1868;           // the hero the brain drives
constexpr uintptr_t kBrainMoveForward = 0x1724;    // float, written by UpdateMovement
constexpr uintptr_t kBrainMoveSide = 0x1728;       // float
constexpr uintptr_t kBrainHeldButtons = 0x1730;    // uint64
constexpr uintptr_t kBrainPressedButtons = 0x1738; // uint64
constexpr uintptr_t kBrainViewAngles = 0x17A8;     // QAngle the brain aims with
constexpr uintptr_t kBotAssignedLane = 0x4994;     // CCitadelPlayerBot: int, the lane SetLane gave it
constexpr uintptr_t kBotDifficulty = 0x4B98;       // CCitadelPlayerBot: CBotDifficultyDefinition *

// CCitadelGameRules::IsBotMatch: the disp32 of its cmp byte [rcx+disp32], 0
constexpr uintptr_t kIsBotMatch_FlagDisp = 15;
constexpr int32_t kMaxGameRulesSize = 0x10000;

// CNavMesh: number of hulls (NPC size classes) the map's navmesh was generated for.
constexpr uintptr_t kNavMeshHullCount = 0x178;

// CNavPath (0x4880 bytes): up to 256 segments of 72 bytes from +8, then the count. A segment's waypoint
// position is at +44 and its nav area index at +56.
constexpr size_t kNavPathSize = 0x4880;
constexpr uintptr_t kNavPathSegments = 0x8;
constexpr uintptr_t kNavPathSegmentSize = 72;
constexpr uintptr_t kNavPathSegmentType = 4; // int: 0/10 ground, 1-3 jump, 4 climb, 5/6 ladder, 7/8 nav link
constexpr uintptr_t kNavPathSegmentPos = 44;
constexpr int32_t kNavPathMaxSegments = 256;
constexpr uintptr_t kNavPathSegmentCount = 0x4808;
constexpr uintptr_t kNavPathOptimizer = 0x4870; // CPathOptimizerNavmesh *, not owned by the path

// CPathOptimizerNavmesh: zeroed storage plus its vtable is a valid one. Slot 1 is its destructor.
constexpr size_t kPathOptimizerSize = 0x68;

// CNavArea (raw reads, no schema; same on 6722 and 6762)
constexpr uintptr_t kNavAreaCenter = 0x0C;       // Vector
constexpr uintptr_t kNavAreaAttributes = 0x40;   // uint64 static attributes
constexpr uintptr_t kNavAreaDynamicFlags = 0x48; // uint32
constexpr uintptr_t kNavAreaHull = 0x58;         // uint8
constexpr uintptr_t kNavAreaId = 0x60;           // int
constexpr uintptr_t kNavAreaBlockedMask = 0x6C;  // uint8, one bit per team parity; nonzero = blocked

// CNavQuery::FindRandomConnectedPoint floods out to its max distance on about 30 KB of stack.
constexpr float kMaxRandomPointDistance = 6000.0f;

// CAI_PathCost (the default INavPathCost; its ctor takes the hull)
constexpr size_t kPathCostSize = 208;

// CAI_Navigator: the movement interface (second vtable) that npc_stop_moving calls StopMoving on.
constexpr uintptr_t kNavigatorMoveInterface = 0x48;

// CEntitySubclassVDataBase
constexpr uintptr_t kSubclassDefType = 0x28;
constexpr uintptr_t kSubclassDefDisabled = 0x2A;

// CCitadelAbilityComponent::OnAbilityRemoved
constexpr uintptr_t kOnAbilityRemoved_FindSlotCall = 0x7B;
constexpr uintptr_t kOnAbilityRemoved_RemoveSlotCall = 0x8B;

// CCitadelPlayerController::ChangeTeamKeepHero
// The bool has no schema entry, so read its offset from the disp32 in the
// wrapper's first store: C6 81 <disp32> 00
constexpr uintptr_t kChangeTeamKeepHero_FlagDisp = 0xE;
constexpr int32_t kMaxPlayerControllerSize = 0x2000;

// CHeroDefinitionManager::GetManagerAnchor
// The manager getter is a generic TLS-guarded singleton stub that cannot be
// signatured directly, so it is resolved from the E8 call inside this anchor.
constexpr uintptr_t kHeroDefMgrAnchor_GetManagerCall = 0xE;

// CCitadelServerGCSystem
// Unix time of the maximum-uptime shutdown; 0 means none. No schema entry; server_dump_state prints it.
constexpr uintptr_t kServerGCSystem_MaxUptimeShutdownTime = 0x430;

// CCitadelGameRules::BuildGameSessionManifest
constexpr uintptr_t kBGSM_PrecacheGlobalLea = 0x377;
constexpr uintptr_t kBGSM_PrecacheCall = 0x382;

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
