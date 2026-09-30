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

static ForceNpcGoFn g_pForceNpcGo = nullptr;
static SetEnemyFn g_pSetEnemy = nullptr;
static SetStateFn g_pSetState = nullptr;

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

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveNpcStatics() {
    auto &loader = MemoryDataLoader::Get();
    const auto resolve = [&loader](const char *key) { return loader.GetOffset(key).value(); };

    g_pForceNpcGo = reinterpret_cast<ForceNpcGoFn>(resolve("CAI_BaseNPC::ForceNpcGo"));
    g_pSetEnemy = reinterpret_cast<SetEnemyFn>(resolve("CAI_BaseNPC::SetEnemy"));
    g_pSetState = reinterpret_cast<SetStateFn>(resolve("CAI_BaseNPC::SetState"));
}

void deadworks::PopulateNpcNatives(NativeCallbacks &cb) {
    cb.NpcMoveTo = &NativeNpcMoveTo;
    cb.NpcStopMoving = &NativeNpcStopMoving;
    cb.NpcSetEnemy = &NativeNpcSetEnemy;
    cb.NpcSetState = &NativeNpcSetState;
}
