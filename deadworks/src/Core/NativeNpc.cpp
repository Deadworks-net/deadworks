#include "NativeNpc.hpp"
#include "NativeOffsets.hpp"

#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CAI_BaseNPC.hpp"
#include "../SDK/Util.hpp"

using namespace deadworks;
using namespace deadworks::offsets;

// Every call here assumes a CAI_BaseNPC (troopers, neutrals, bosses, summons, the bot brain). The
// managed wrappers only hand over entities whose class is one of those, so nothing is checked here
// beyond null.

// --- Function pointer types ---

// CUtlSourceLocation. Functions that log their caller copy it, so it must point somewhere valid.
struct SourceLocation {
    const char *file;
    int line;
    const char *function;
};

using ForceNpcGoFn = void(__fastcall *)(void *npc, const Vector *goal, bool run, float tolerance,
                                        const Vector *teleportFirst, bool log);
using SetEnemyFn = void(__fastcall *)(void *npc, void *enemy);
using SetStateFn = void(__fastcall *)(void *npc, int32_t state);
using StopMovingFn = void(__fastcall *)(void *moveInterface, int32_t mode, int32_t facing, const SourceLocation *caller);
using MakeGlobalSymbolFn = const char *(*)(const char *name);
using GetScheduleOfTypeFn = void *(__fastcall *)(void *npc, const char **id, const char **outName);
using SchedulerSetScheduleFn = void(__fastcall *)(void *scheduler, void *schedule, const char *const *name, const char *const *id);
using VetoScheduleFn = bool(__fastcall *)(void *npc, const char *const *id);
using SetMinionFn = void(__fastcall *)(void *npc, bool minion);
using GetSummonerFn = void *(__fastcall *)(void *npc);
using TrooperSpawnAtFn = void *(__fastcall *)(const Vector *origin, const QAngle *angles, int32_t team, int32_t variant, int32_t type);
using SnapToNavGroundFn = bool(__fastcall *)(void *trooper);

static ForceNpcGoFn g_pForceNpcGo = nullptr;
static SetEnemyFn g_pSetEnemy = nullptr;
static SetStateFn g_pSetState = nullptr;
static MakeGlobalSymbolFn g_pMakeGlobalSymbol = nullptr;
static GetScheduleOfTypeFn g_pGetScheduleOfType = nullptr;
static SchedulerSetScheduleFn g_pSchedulerSetSchedule = nullptr;
static SetMinionFn g_pSetMinion = nullptr;
static GetSummonerFn g_pGetSummoner = nullptr;
static TrooperSpawnAtFn g_pTrooperSpawnAt = nullptr;
static SnapToNavGroundFn g_pSnapToNavGround = nullptr;
static int32_t g_summonerHandleOffset = 0; // CAI_CitadelNPC::m_hCaster, read from GetSummoner's first instruction

// AI_NavStopMovingMode_t, as npc_stop_moving picks them ("asap" = instant).
constexpr int32_t kStopMovingInstant = 0;
constexpr int32_t kStopMovingSmooth = 2;

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

// npc_go without the cheat flag or the NPC selection. The goal doesn't need to be on the navmesh;
// ForceNpcGo falls back to the raw point. A tolerance of 0 is what npc_go passes.
static void __cdecl NativeNpcMoveTo(void *npc, const float *goal, uint8_t run, float tolerance) {
    if (!npc || !goal || !g_pForceNpcGo) return;
    g_pForceNpcGo(npc, reinterpret_cast<const Vector *>(goal), run != 0, tolerance, nullptr, false);
}

// npc_stop_moving: drops the navigator's goal. Instant stops on the spot, otherwise the NPC slows
// down first. The schedule that was moving it fails and the AI picks a new one on its next think.
static void __cdecl NativeNpcStopMoving(void *npc, uint8_t instant) {
    if (!npc) return;
    void *navigator = static_cast<CAI_BaseNPC *>(npc)->m_pNavigator.Get();
    if (!navigator) return;

    static const SourceLocation caller{__FILE__, __LINE__, "NativeNpcStopMoving"};
    static const int index = MemoryDataLoader::Get().GetVirtual("CAI_Navigator::StopMoving").value();
    void *moveInterface = reinterpret_cast<uint8_t *>(navigator) + kNavigatorMoveInterface;
    GetVFunc<StopMovingFn>(moveInterface, index)(moveInterface, instant ? kStopMovingInstant : kStopMovingSmooth, 0, &caller);
}

static void __cdecl NativeNpcSetEnemy(void *npc, void *enemy) {
    if (!npc || !g_pSetEnemy) return;
    g_pSetEnemy(npc, enemy);
}

static void __cdecl NativeNpcSetState(void *npc, int32_t state) {
    if (!npc || !g_pSetState) return;
    g_pSetState(npc, state);
}

// Switches to the named schedule now, the way the SetSchedule task does. Returns 0 if the NPC vetoes it.
// A name the NPC's class has no schedule for falls back to SCHED_IDLE_STAND (the game logs "No CASE").
static uint8_t __cdecl NativeNpcSetSchedule(void *npc, const char *name) {
    if (!npc || !name || !g_pMakeGlobalSymbol || !g_pGetScheduleOfType || !g_pSchedulerSetSchedule) return 0;

    const char *id = g_pMakeGlobalSymbol(name);
    static const int vetoIndex = MemoryDataLoader::Get().GetVirtual("CAI_BaseNPC::VetoSchedule").value();
    if (GetVFunc<VetoScheduleFn>(npc, vetoIndex)(npc, &id)) return 0;

    const char *translated = nullptr;
    void *schedule = g_pGetScheduleOfType(npc, &id, &translated);
    if (!schedule) return 0;
    g_pSchedulerSetSchedule(&static_cast<CAI_BaseNPC *>(npc)->m_Scheduler.Get(), schedule, &translated, &id);
    return 1;
}

// CAI_CitadelNPC only, like the two below.
static void __cdecl NativeNpcSetMinion(void *npc, uint8_t minion) {
    if (!npc || !g_pSetMinion) return;
    g_pSetMinion(npc, minion != 0);
}

static void *__cdecl NativeNpcGetSummoner(void *npc) {
    if (!npc || !g_pGetSummoner) return nullptr;
    return g_pGetSummoner(npc);
}

// Stores the handle in m_hCaster, which GetSummoner prefers over the owning ability's owner.
static void __cdecl NativeNpcSetSummoner(void *npc, uint32_t handle) {
    if (!npc || !g_summonerHandleOffset) return;
    *reinterpret_cast<uint32_t *>(reinterpret_cast<uint8_t *>(npc) + g_summonerHandleOffset) = handle;
}

// citadel_spawn_trooper without the cheat flag or the player: a trooper of the type at origin, on team, in the
// lane nearest to origin. Null angles face east. Returns the trooper, or null if it could not be spawned.
static void *__cdecl NativeNpcCreateTrooper(const float *origin, const float *angles, int32_t team, int32_t type,
                                            int32_t variant, uint8_t snapToGround) {
    if (!origin || !g_pTrooperSpawnAt) return nullptr;
    const QAngle facing = angles ? QAngle(angles[0], angles[1], angles[2]) : QAngle(0, 0, 0);
    void *trooper = g_pTrooperSpawnAt(reinterpret_cast<const Vector *>(origin), &facing, team, variant, type);
    if (trooper && snapToGround && g_pSnapToNavGround) g_pSnapToNavGround(trooper);
    return trooper;
}

const char *deadworks::npc::MakeScheduleId(const char *name) {
    return g_pMakeGlobalSymbol ? g_pMakeGlobalSymbol(name) : nullptr;
}

void *deadworks::npc::GetScheduleOfType(void *npc, const char **id, const char **outName) {
    return g_pGetScheduleOfType ? g_pGetScheduleOfType(npc, id, outName) : nullptr;
}

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveNpcStatics() {
    auto &loader = MemoryDataLoader::Get();
    const auto resolve = [&loader](const char *key) { return loader.GetOffset(key).value(); };

    g_pForceNpcGo = reinterpret_cast<ForceNpcGoFn>(resolve("CAI_BaseNPC::ForceNpcGo"));
    g_pSetEnemy = reinterpret_cast<SetEnemyFn>(resolve("CAI_BaseNPC::SetEnemy"));
    g_pSetState = reinterpret_cast<SetStateFn>(resolve("CAI_BaseNPC::SetState"));
    g_pGetScheduleOfType = reinterpret_cast<GetScheduleOfTypeFn>(resolve("CAI_BaseNPC::GetScheduleOfType"));
    g_pSchedulerSetSchedule = reinterpret_cast<SchedulerSetScheduleFn>(resolve("CAI_Scheduler::SetSchedule"));
    g_pSetMinion = reinterpret_cast<SetMinionFn>(resolve("CAI_CitadelNPC::SetMinion"));
    g_pTrooperSpawnAt = reinterpret_cast<TrooperSpawnAtFn>(resolve("Trooper_SpawnAt"));
    g_pSnapToNavGround = reinterpret_cast<SnapToNavGroundFn>(resolve("CNPC_Trooper::SnapToNavGround"));

    // GetSummoner starts with mov edx, [rcx + disp32] (8B 91): the disp32 is m_hCaster's offset.
    auto getSummoner = resolve("CAI_CitadelNPC::GetSummoner");
    g_pGetSummoner = reinterpret_cast<GetSummonerFn>(getSummoner);
    g_summonerHandleOffset = *reinterpret_cast<int32_t *>(getSummoner + 2);

    // Exported by tier0 as _MakeGlobalSymbol; the CGlobalSymbol it returns is just the interned string.
    if (HMODULE tier0 = GetModuleHandleA("tier0.dll"))
        g_pMakeGlobalSymbol = reinterpret_cast<MakeGlobalSymbolFn>(GetProcAddress(tier0, "_MakeGlobalSymbol"));
}

void deadworks::PopulateNpcNatives(NativeCallbacks &cb) {
    cb.NpcMoveTo = &NativeNpcMoveTo;
    cb.NpcStopMoving = &NativeNpcStopMoving;
    cb.NpcSetEnemy = &NativeNpcSetEnemy;
    cb.NpcSetState = &NativeNpcSetState;
    cb.NpcSetSchedule = &NativeNpcSetSchedule;
    cb.NpcSetMinion = &NativeNpcSetMinion;
    cb.NpcGetSummoner = &NativeNpcGetSummoner;
    cb.NpcSetSummoner = &NativeNpcSetSummoner;
    cb.NpcCreateTrooper = &NativeNpcCreateTrooper;
}
