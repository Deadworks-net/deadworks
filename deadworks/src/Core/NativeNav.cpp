#include "NativeNav.hpp"

#include <mathlib/vector.h>

#include "../Memory/MemoryDataLoader.hpp"

using namespace deadworks;

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

static GetNearestNavFn g_pGetNearestNav = nullptr;
static void **g_ppNavMesh = nullptr;

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

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveNavStatics() {
    auto &loader = MemoryDataLoader::Get();

    g_pGetNearestNav = reinterpret_cast<GetNearestNavFn>(loader.GetOffset("CNavMesh::GetNearestNav").value());

    // The match starts with mov rax, [rip + disp32] (48 8B 05), loading the CNavMesh global.
    auto globalRef = loader.GetOffset("CNavMesh::GlobalRef").value();
    auto disp = *reinterpret_cast<int32_t *>(globalRef + 3);
    g_ppNavMesh = reinterpret_cast<void **>(globalRef + 7 + disp);
}

void deadworks::PopulateNavNatives(NativeCallbacks &cb) {
    cb.NavGetNearestPoint = &NativeNavGetNearestPoint;
}
