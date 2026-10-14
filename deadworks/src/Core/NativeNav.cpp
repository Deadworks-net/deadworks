#include "NativeNav.hpp"

#include <cfloat>
#include <cstring>

#include <mathlib/vector.h>

#include "NativeOffsets.hpp"
#include "../Memory/MemoryDataLoader.hpp"

using namespace deadworks;
using namespace deadworks::offsets;

// Everything here runs the game's own navmesh code with no NPC involved, the way its nav_test_*
// debug views do, and must be called on the game thread.

// --- Engine structs ---

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

// NavTraceResult_t, initialised the way its ctor does.
struct NavTraceResult {
    float fraction = -1.0f;
    Vector endPos{FLT_MAX, FLT_MAX, FLT_MAX}; // where the ray stopped, on the mesh
    void *area = nullptr;
    Vector direction{FLT_MAX, FLT_MAX, FLT_MAX};
    Vector hitNormal{FLT_MAX, FLT_MAX, FLT_MAX}; // normal of the edge that stopped it
    void *unk48 = nullptr;
};
static_assert(sizeof(NavTraceResult) == 56);

// What NativeNavGetArea hands to managed code. Must match NavAreaInfo in Nav.cs.
struct NavAreaInfo {
    float center[3];
    int32_t id;
    uint64_t attributes;
    uint32_t dynamicFlags;
    uint8_t hull;
    uint8_t blockedMask;
    uint8_t pad[2];
    float nearest[3]; // the queried position moved onto the area
    uint32_t pad2;
};
static_assert(sizeof(NavAreaInfo) == 48);

// --- Function pointer types ---

using GetNearestNavFn = void *(__fastcall *)(void *navMesh, const Vector *pos, const int32_t *hull, uint32_t flags,
                                             Vector *outPos, float maxDistance, const NavNearestParams *params);
using NavPathCtorFn = void *(__fastcall *)(void *path);
using VirtualDtorFn = void *(__fastcall *)(void *self, uint8_t deleteFlags);
using PathCostCtorFn = void *(__fastcall *)(void *cost, int32_t hull);
using ComputePathPosToPosFn = bool(__fastcall *)(void *path, const NavCostQuery *query, const Vector *start, void *startArea,
                                                 const Vector *goal, void *goalArea, const void *extra);
using AreaGetZFn = float(__fastcall *)(void *area, float x, float y);
using TestRayToNavMeshFn = bool(__fastcall *)(void *navMesh, const Vector *start, void *startArea, const Vector *end,
                                              int32_t hull, NavTraceResult *result, const void *params, const void *edgeCallback);
// Returns its result through the hidden pointer in rcx; the two distances are stack arguments, max first.
using FindRandomConnectedPointFn = Vector *(__fastcall *)(Vector *out, const Vector *start, void *startArea, int32_t hull,
                                                          float maxDistance, float minDistance);

static GetNearestNavFn g_pGetNearestNav = nullptr;
static void **g_ppNavMesh = nullptr;
static NavPathCtorFn g_pNavPathCtor = nullptr;
static PathCostCtorFn g_pPathCostCtor = nullptr;
static ComputePathPosToPosFn g_pComputePathPosToPos = nullptr;
static AreaGetZFn g_pAreaGetZ = nullptr;
static TestRayToNavMeshFn g_pTestRayToNavMesh = nullptr;
static FindRandomConnectedPointFn g_pFindRandomConnectedPoint = nullptr;
static void *g_pPathOptimizerVtable = nullptr; // CPathOptimizerNavmesh

// What Necro's skeletons pass when they snap their target onto the navmesh (TASK_NECROSKELE_MOVE_TO_TARGET):
// flag 0x20, and limits of 80 / 18 / 16 (18 * 0.5 + 71, 18, 16).
constexpr uint32_t kNearestNavFlags = 0x20;
constexpr float kHeightLimitA = 80.0f;
constexpr float kHeightLimitB = 18.0f;
constexpr float kRadiusLimit = 16.0f;

// One CAI_PathCost per hull, built on first use and kept: the game's own are heap objects it owns, and
// these are never handed to anything that would free them.
constexpr int32_t kMaxCachedHulls = 16;
alignas(16) static uint8_t g_pathCosts[kMaxCachedHulls][kPathCostSize];
static bool g_pathCostBuilt[kMaxCachedHulls] = {};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void *NavMesh() {
    return g_ppNavMesh ? *g_ppNavMesh : nullptr;
}

// A hull the navmesh was generated for, or 0. The count is read from the navmesh and only trusted when it
// looks like one.
static int32_t ClampHull(int32_t hull) {
    const int32_t count = *reinterpret_cast<int32_t *>(reinterpret_cast<uint8_t *>(NavMesh()) + kNavMeshHullCount);
    if (count < 1 || count > kMaxCachedHulls || hull < 0 || hull >= count) return 0;
    return hull;
}

static void *PathCost(int32_t hull) {
    if (!g_pathCostBuilt[hull]) {
        g_pPathCostCtor(g_pathCosts[hull], hull);
        g_pathCostBuilt[hull] = true;
    }
    return g_pathCosts[hull];
}

// The nav area nearest to pos within maxDistance, and pos moved onto it.
static void *NearestArea(const Vector &pos, int32_t hull, float maxDistance, Vector *outPos) {
    NavNearestParams params;
    params.heightLimitA = kHeightLimitA;
    params.heightLimitB = kHeightLimitB;
    params.radiusLimit = kRadiusLimit;
    return g_pGetNearestNav(NavMesh(), &pos, &hull, kNearestNavFlags, outPos, maxDistance, &params);
}

// The vtable of a class in server.dll, found through its RTTI: the type descriptor holding the decorated
// name, the complete object locator for offset 0 that points at it, and the vtable slot that points at that.
static void *FindVTable(const char *decoratedName) {
    auto *base = reinterpret_cast<uint8_t *>(GetModuleHandleA("server.dll"));
    if (!base) return nullptr;
    auto *nt = reinterpret_cast<IMAGE_NT_HEADERS64 *>(base + reinterpret_cast<IMAGE_DOS_HEADER *>(base)->e_lfanew);
    auto *sections = IMAGE_FIRST_SECTION(nt);
    const size_t nameLength = std::strlen(decoratedName) + 1;

    uint32_t descriptorRva = 0;
    for (int i = 0; i < nt->FileHeader.NumberOfSections && !descriptorRva; i++) {
        if (std::memcmp(sections[i].Name, ".data", 6) != 0) continue;
        auto *start = base + sections[i].VirtualAddress;
        const size_t size = sections[i].Misc.VirtualSize;
        for (size_t at = 0x10; at + nameLength <= size; at++) {
            if (std::memcmp(start + at, decoratedName, nameLength) == 0) {
                descriptorRva = static_cast<uint32_t>(sections[i].VirtualAddress + at - 0x10);
                break;
            }
        }
    }
    if (!descriptorRva) return nullptr;

    for (int i = 0; i < nt->FileHeader.NumberOfSections; i++) {
        if (std::memcmp(sections[i].Name, ".rdata", 7) != 0) continue;
        auto *start = base + sections[i].VirtualAddress;
        const size_t size = sections[i].Misc.VirtualSize;
        // Complete object locator: signature 1, offset 0, cdOffset, then the type descriptor's RVA.
        for (size_t at = 0; at + 16 <= size; at += 4) {
            auto *locator = reinterpret_cast<uint32_t *>(start + at);
            if (locator[0] != 1 || locator[1] != 0 || locator[3] != descriptorRva) continue;
            const auto locatorAddress = reinterpret_cast<uintptr_t>(locator);
            for (size_t slot = 0; slot + 8 <= size; slot += 8) {
                if (*reinterpret_cast<uintptr_t *>(start + slot) == locatorAddress) return start + slot + 8;
            }
        }
    }
    return nullptr;
}

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

// Snaps pos to the closest point on the navmesh within maxDistance, for the given hull (an out-of-range
// hull index falls back to 0). Writes the point to out and returns 1, or returns 0 if nothing was found.
// A query costs a few microseconds.
static uint8_t __cdecl NativeNavGetNearestPoint(const float *pos, float maxDistance, int32_t hull, float *out) {
    if (!pos || !out || !g_pGetNearestNav || !NavMesh()) return 0;

    Vector result(pos[0], pos[1], pos[2]);
    if (!NearestArea(Vector(pos[0], pos[1], pos[2]), hull, maxDistance, &result)) return 0;
    out[0] = result.x;
    out[1] = result.y;
    out[2] = result.z;
    return 1;
}

// The nav area nearest to pos: where it is, its id, what it allows and whether it is blocked.
static uint8_t __cdecl NativeNavGetArea(const float *pos, float maxDistance, int32_t hull, void *outInfo) {
    auto *out = static_cast<NavAreaInfo *>(outInfo);
    if (!pos || !out || !g_pGetNearestNav || !NavMesh()) return 0;

    Vector nearest(pos[0], pos[1], pos[2]);
    auto *area = static_cast<uint8_t *>(NearestArea(Vector(pos[0], pos[1], pos[2]), hull, maxDistance, &nearest));
    if (!area) return 0;

    std::memcpy(out->center, area + kNavAreaCenter, sizeof(out->center));
    out->id = *reinterpret_cast<int32_t *>(area + kNavAreaId);
    out->attributes = *reinterpret_cast<uint64_t *>(area + kNavAreaAttributes);
    out->dynamicFlags = *reinterpret_cast<uint32_t *>(area + kNavAreaDynamicFlags);
    out->hull = area[kNavAreaHull];
    out->blockedMask = area[kNavAreaBlockedMask];
    out->nearest[0] = nearest.x;
    out->nearest[1] = nearest.y;
    out->nearest[2] = nearest.z;
    return 1;
}

// nav_test_find_z: the nearest area to pos, then CNavArea::GetZ at pos's x and y.
static uint8_t __cdecl NativeNavGetGroundHeight(const float *pos, float maxDistance, int32_t hull, float *outZ) {
    if (!pos || !outZ || !g_pGetNearestNav || !g_pAreaGetZ || !NavMesh()) return 0;

    void *area = NearestArea(Vector(pos[0], pos[1], pos[2]), hull, maxDistance, nullptr);
    if (!area) return 0;
    *outZ = g_pAreaGetZ(area, pos[0], pos[1]);
    return 1;
}

// NavSearch::TestRayToNavMesh: walks a straight line across the navmesh from start towards end. Returns 1
// if it gets there. Either way outEnd is where it stopped and outNormal the edge that stopped it (zero when
// nothing did). The start is snapped onto the mesh first, since the test needs a start area; returns 0 with
// outEnd = start when there is none within 200 units.
static uint8_t __cdecl NativeNavRaycast(const float *start, const float *end, int32_t hull, float *outEnd, float *outNormal) {
    if (!start || !end || !outEnd || !outNormal || !g_pTestRayToNavMesh || !g_pGetNearestNav || !NavMesh()) return 0;

    hull = ClampHull(hull);
    std::memcpy(outEnd, start, sizeof(float) * 3);
    std::memset(outNormal, 0, sizeof(float) * 3);

    Vector from(start[0], start[1], start[2]);
    void *startArea = NearestArea(from, hull, 200.0f, &from);
    if (!startArea) return 0;

    NavTraceResult result;
    const bool reached = g_pTestRayToNavMesh(NavMesh(), &from, startArea, reinterpret_cast<const Vector *>(end), hull,
                                             &result, nullptr, nullptr);
    if (result.endPos.x != FLT_MAX) {
        outEnd[0] = result.endPos.x;
        outEnd[1] = result.endPos.y;
        outEnd[2] = result.endPos.z;
    }
    if (!reached && result.hitNormal.x != FLT_MAX) {
        outNormal[0] = result.hitNormal.x;
        outNormal[1] = result.hitNormal.y;
        outNormal[2] = result.hitNormal.z;
    }
    return reached ? 1 : 0;
}

// CNavQuery::FindRandomConnectedPoint: floods the navmesh outwards from origin and picks a random point
// between minDistance and maxDistance that can be walked to. The flood grows with maxDistance, which is
// capped here.
static uint8_t __cdecl NativeNavGetRandomPoint(const float *origin, float minDistance, float maxDistance, int32_t hull, float *out) {
    if (!origin || !out || !g_pFindRandomConnectedPoint || !NavMesh()) return 0;
    if (maxDistance > kMaxRandomPointDistance) maxDistance = kMaxRandomPointDistance;
    if (minDistance < 0.0f) minDistance = 0.0f;
    if (minDistance > maxDistance) return 0;

    Vector result(FLT_MAX, FLT_MAX, FLT_MAX);
    g_pFindRandomConnectedPoint(&result, reinterpret_cast<const Vector *>(origin), nullptr, ClampHull(hull), maxDistance, minDistance);
    if (result.x == FLT_MAX) return 0;
    out[0] = result.x;
    out[1] = result.y;
    out[2] = result.z;
    return 1;
}

// CNavPath::ComputePathPosToPos, the way the nav_test_path debug view runs it: no NPC, a default path cost
// for the hull, and the start and goal areas left for the pathfinder to find. flags: 1 = accept a path that
// stops short of the goal, 2 = run the game's path optimizer (CPathOptimizerNavmesh), which drops the
// waypoints a straight walk can skip. Copies up to maxPoints waypoints (x, y, z each) into outPoints and
// each one's segment type into outTypes, and returns how many the path has; 0 means no path.
static int32_t __cdecl NativeNavFindPath(const float *start, const float *goal, int32_t hull, uint32_t flags,
                                         float *outPoints, int32_t *outTypes, int32_t maxPoints) {
    if (!start || !goal || !outPoints || !outTypes || maxPoints <= 0) return 0;
    if (!g_pNavPathCtor || !g_pPathCostCtor || !g_pComputePathPosToPos || !NavMesh()) return 0;

    hull = ClampHull(hull);
    NavCostQuery query;
    query.cost = PathCost(hull);
    query.allowPartial = (flags & 1) ? 1 : 0;

    alignas(16) uint8_t path[kNavPathSize];
    g_pNavPathCtor(path);

    // The path calls the optimizer itself (when nav_path_optimize is on, its default) and the optimizer
    // removes the skipped segments in place. It must outlive the computation and is not owned by the path.
    alignas(16) uint8_t optimizer[kPathOptimizerSize] = {};
    const bool optimize = (flags & 2) && g_pPathOptimizerVtable;
    if (optimize) {
        *reinterpret_cast<void **>(optimizer) = g_pPathOptimizerVtable;
        *reinterpret_cast<void **>(path + kNavPathOptimizer) = optimizer;
    }

    const uint64_t extra[2] = {0, 0};
    int32_t count = 0;
    if (g_pComputePathPosToPos(path, &query, reinterpret_cast<const Vector *>(start), nullptr,
                               reinterpret_cast<const Vector *>(goal), nullptr, extra)) {
        count = *reinterpret_cast<int32_t *>(path + kNavPathSegmentCount);
        if (count < 0 || count > kNavPathMaxSegments) count = 0;
        const int32_t copied = count < maxPoints ? count : maxPoints;
        for (int32_t i = 0; i < copied; i++) {
            const uint8_t *segment = path + kNavPathSegments + i * kNavPathSegmentSize;
            std::memcpy(outPoints + i * 3, segment + kNavPathSegmentPos, sizeof(float) * 3);
            outTypes[i] = *reinterpret_cast<const int32_t *>(segment + kNavPathSegmentType);
        }
    }

    // Slot 1 of the optimizer and slot 0 of the path are their destructors; 0 = destroy without freeing,
    // since both live on our stack.
    if (optimize) (*reinterpret_cast<VirtualDtorFn **>(optimizer))[1](optimizer, 0);
    (*reinterpret_cast<VirtualDtorFn **>(path))[0](path, 0);
    return count;
}

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveNavStatics() {
    auto &loader = MemoryDataLoader::Get();
    const auto resolve = [&loader](const char *key) { return loader.GetOffset(key).value(); };

    g_pGetNearestNav = reinterpret_cast<GetNearestNavFn>(resolve("CNavMesh::GetNearestNav"));
    g_pNavPathCtor = reinterpret_cast<NavPathCtorFn>(resolve("CNavPath::CNavPath"));
    g_pPathCostCtor = reinterpret_cast<PathCostCtorFn>(resolve("CAI_PathCost::CAI_PathCost"));
    g_pComputePathPosToPos = reinterpret_cast<ComputePathPosToPosFn>(resolve("CNavPath::ComputePathPosToPos"));
    g_pAreaGetZ = reinterpret_cast<AreaGetZFn>(resolve("CNavArea::GetZ"));
    g_pTestRayToNavMesh = reinterpret_cast<TestRayToNavMeshFn>(resolve("NavSearch::TestRayToNavMesh"));
    g_pFindRandomConnectedPoint = reinterpret_cast<FindRandomConnectedPointFn>(resolve("CNavQuery::FindRandomConnectedPoint"));
    g_pPathOptimizerVtable = FindVTable(".?AVCPathOptimizerNavmesh@@");

    // The match starts with mov rax, [rip + disp32] (48 8B 05), loading the CNavMesh global.
    auto globalRef = resolve("CNavMesh::GlobalRef");
    auto disp = *reinterpret_cast<int32_t *>(globalRef + 3);
    g_ppNavMesh = reinterpret_cast<void **>(globalRef + 7 + disp);
}

void deadworks::PopulateNavNatives(NativeCallbacks &cb) {
    cb.NavGetNearestPoint = &NativeNavGetNearestPoint;
    cb.NavFindPath = &NativeNavFindPath;
    cb.NavGetArea = &NativeNavGetArea;
    cb.NavGetGroundHeight = &NativeNavGetGroundHeight;
    cb.NavRaycast = &NativeNavRaycast;
    cb.NavGetRandomPoint = &NativeNavGetRandomPoint;
}
