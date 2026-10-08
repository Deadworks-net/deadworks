#include "NativeNav.hpp"

#include <mathlib/vector.h>

#include "NativeOffsets.hpp"
#include "../Memory/MemoryDataLoader.hpp"

using namespace deadworks;
using namespace deadworks::offsets;

// --- Function pointer types ---

// The query settings GetNearestNav takes, as the game's own ctor (sub_180504340 in 6722) fills them.
// Only the fields that ctor writes are named; what the height/radius limits do depends on the flags.
struct NavNearestParams {
    uint64_t unk0 = 0;
    float unk8 = -1.0f;
    uint32_t unk12 = 0;
    float heightLimitA = 0.0f;
    float heightLimitB = 0.0f;
    float radiusLimit = 0.0f;
    uint32_t unk28 = 0;
    int64_t unk32 = -1;
    uint64_t areaAttributes = 0x20000;
    void *acceptFilter = nullptr; // object with a vfunc (+48) that can reject areas
    void *unk56 = nullptr;
    uint8_t unk64 = 1;
    uint8_t pad[15] = {};
};
static_assert(sizeof(NavNearestParams) == 80);

using GetNearestNavFn = void *(__fastcall *)(void *navMesh, const Vector *pos, const int32_t *hull, uint32_t flags,
                                             Vector *outPos, float maxDistance, const NavNearestParams *params);

// What CNavPath::ComputePathPosToPos reads its settings from, as the nav_test_path debug code fills it:
// the cost object, two limits it leaves at -1, a 12-unit value and 0. With allowPartial, a path that
// stops short of the goal still counts as found.
struct NavCostQuery {
    uint8_t unk0 = 0;
    void *unk8 = nullptr;
    void *cost = nullptr; // CAI_PathCost
    float unk24 = -1.0f;
    float unk28 = -1.0f;
    float unk32 = 12.0f;
    float unk36 = 0.0f;
    uint8_t allowPartial = 0;
};
static_assert(sizeof(NavCostQuery) == 48);

using NavPathCtorFn = void *(__fastcall *)(void *path);
using NavPathDtorFn = void *(__fastcall *)(void *path, uint8_t deleteFlags);
using PathCostCtorFn = void *(__fastcall *)(void *cost, int32_t hull);
using ComputePathPosToPosFn = bool(__fastcall *)(void *path, const NavCostQuery *query, const Vector *start, void *startArea,
                                                 const Vector *goal, void *goalArea, const void *extra);

static GetNearestNavFn g_pGetNearestNav = nullptr;
static void **g_ppNavMesh = nullptr;
static NavPathCtorFn g_pNavPathCtor = nullptr;
static PathCostCtorFn g_pPathCostCtor = nullptr;
static ComputePathPosToPosFn g_pComputePathPosToPos = nullptr;

// One CAI_PathCost per hull, built on first use and kept: the game's own are heap objects it owns, and
// these are never handed to anything that would free them.
constexpr int32_t kMaxCachedHulls = 16;
alignas(16) static uint8_t g_pathCosts[kMaxCachedHulls][kPathCostSize];
static bool g_pathCostBuilt[kMaxCachedHulls] = {};

// What Necro's skeletons pass when they snap their target onto the navmesh (TASK_NECROSKELE_MOVE_TO_TARGET):
// flag 0x20, and limits of 80 / 18 / 16 (18 * 0.5 + 71, 18, 16).
constexpr uint32_t kNearestNavFlags = 0x20;
constexpr float kHeightLimitA = 80.0f;
constexpr float kHeightLimitB = 18.0f;
constexpr float kRadiusLimit = 16.0f;

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

// Snaps pos to the closest point on the navmesh within maxDistance, for the given hull (an out-of-range
// hull index falls back to 0). Writes the point to out and returns 1, or returns 0 if nothing was found.
// A query costs a few microseconds.
static uint8_t __cdecl NativeNavGetNearestPoint(const float *pos, float maxDistance, int32_t hull, float *out) {
    if (!pos || !out || !g_pGetNearestNav || !g_ppNavMesh || !*g_ppNavMesh) return 0;

    NavNearestParams params;
    params.heightLimitA = kHeightLimitA;
    params.heightLimitB = kHeightLimitB;
    params.radiusLimit = kRadiusLimit;

    Vector result(pos[0], pos[1], pos[2]);
    void *area = g_pGetNearestNav(*g_ppNavMesh, reinterpret_cast<const Vector *>(pos), &hull, kNearestNavFlags,
                                  &result, maxDistance, &params);
    if (!area) return 0;
    out[0] = result.x;
    out[1] = result.y;
    out[2] = result.z;
    return 1;
}

// A hull the navmesh was generated for, or 0. The count is read from the navmesh and only trusted when it
// looks like one.
static int32_t ClampHull(int32_t hull) {
    const int32_t count = *reinterpret_cast<int32_t *>(reinterpret_cast<uint8_t *>(*g_ppNavMesh) + kNavMeshHullCount);
    if (count < 1 || count > kMaxCachedHulls || hull < 0 || hull >= count) return 0;
    return hull;
}

// CNavPath::ComputePathPosToPos, the way the nav_test_path debug view runs it: no NPC, a default path cost
// for the hull, and the start and goal areas left for the pathfinder to find. Copies up to maxPoints
// waypoints (x, y, z each) into outPoints and returns how many the path has; 0 means no path.
static int32_t __cdecl NativeNavFindPath(const float *start, const float *goal, int32_t hull, uint8_t allowPartial,
                                         float *outPoints, int32_t maxPoints) {
    if (!start || !goal || !outPoints || maxPoints <= 0) return 0;
    if (!g_pNavPathCtor || !g_pPathCostCtor || !g_pComputePathPosToPos || !g_ppNavMesh || !*g_ppNavMesh) return 0;

    hull = ClampHull(hull);
    if (!g_pathCostBuilt[hull]) {
        g_pPathCostCtor(g_pathCosts[hull], hull);
        g_pathCostBuilt[hull] = true;
    }

    NavCostQuery query;
    query.cost = g_pathCosts[hull];
    query.allowPartial = allowPartial ? 1 : 0;

    alignas(16) uint8_t path[kNavPathSize];
    g_pNavPathCtor(path);

    const uint64_t extra[2] = {0, 0};
    int32_t count = 0;
    if (g_pComputePathPosToPos(path, &query, reinterpret_cast<const Vector *>(start), nullptr,
                               reinterpret_cast<const Vector *>(goal), nullptr, extra)) {
        count = *reinterpret_cast<int32_t *>(path + kNavPathSegmentCount);
        if (count < 0 || count > kNavPathMaxSegments) count = 0;
        const int32_t copied = count < maxPoints ? count : maxPoints;
        for (int32_t i = 0; i < copied; i++) {
            const float *pos = reinterpret_cast<const float *>(path + kNavPathSegments + i * kNavPathSegmentSize + kNavPathSegmentPos);
            outPoints[i * 3 + 0] = pos[0];
            outPoints[i * 3 + 1] = pos[1];
            outPoints[i * 3 + 2] = pos[2];
        }
    }

    // Vtable slot 0 is the scalar deleting destructor; 0 = destroy without freeing (the path is on our stack).
    (*reinterpret_cast<NavPathDtorFn **>(path))[0](path, 0);
    return count;
}

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveNavStatics() {
    auto &loader = MemoryDataLoader::Get();

    g_pGetNearestNav = reinterpret_cast<GetNearestNavFn>(loader.GetOffset("CNavMesh::GetNearestNav").value());
    g_pNavPathCtor = reinterpret_cast<NavPathCtorFn>(loader.GetOffset("CNavPath::CNavPath").value());
    g_pPathCostCtor = reinterpret_cast<PathCostCtorFn>(loader.GetOffset("CAI_PathCost::CAI_PathCost").value());
    g_pComputePathPosToPos = reinterpret_cast<ComputePathPosToPosFn>(loader.GetOffset("CNavPath::ComputePathPosToPos").value());

    // The match starts with mov rax, [rip + disp32] (48 8B 05), loading the CNavMesh global.
    auto globalRef = loader.GetOffset("CNavMesh::GlobalRef").value();
    auto disp = *reinterpret_cast<int32_t *>(globalRef + 3);
    g_ppNavMesh = reinterpret_cast<void **>(globalRef + 7 + disp);
}

void deadworks::PopulateNavNatives(NativeCallbacks &cb) {
    cb.NavGetNearestPoint = &NativeNavGetNearestPoint;
    cb.NavFindPath = &NativeNavFindPath;
}
