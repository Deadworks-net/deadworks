#pragma once

#include <safetyhook.hpp>
#include <mathlib/vector.h>
#include <cstdint>

namespace deadworks {

// ---------------------------------------------------------------------------
// Trace structs (matching Source 2 Rubikon physics engine)
// ---------------------------------------------------------------------------

enum class RayType_t : uint8_t {
    LINE = 0,
    SPHERE,
    HULL,
    CAPSULE,
    MESH,
};

#pragma pack(push, 1)
struct Ray_t {
    union {
        struct { Vector startOffset; float radius; } line;
        struct { Vector center; float radius; } sphere;
        struct { Vector mins; Vector maxs; } hull;
        struct { Vector center0; Vector center1; float radius; } capsule;
        uint8_t _data[40];
    };
    RayType_t type;
    uint8_t _pad[7];

    void InitLine(const Vector &offset = Vector(0, 0, 0)) {
        memset(this, 0, sizeof(*this));
        line.startOffset = offset;
        line.radius = 0.0f;
        type = RayType_t::LINE;
    }
};
static_assert(sizeof(Ray_t) == 48);
#pragma pack(pop)

enum class CollisionGroup_t : uint8_t {
    ALWAYS = 0,
};

enum class RnQueryObjectSet : uint8_t {
    STATIC = 0x01,
    KEYFRAMED = 0x02,
    DYNAMIC = 0x04,
    LOCATABLE = 0x08,
    ALL = 0x0F,
};

#pragma pack(push, 8)
struct RnQueryShapeAttr_t {
    uint64_t interactsWith;
    uint64_t interactsExclude;
    uint64_t interactsAs;
    uint32_t entityIdsToIgnore[2];
    uint32_t ownerIdsToIgnore[2];
    uint16_t hierarchyIds[2];
    uint16_t includedDetailLayers;
    uint8_t targetDetailLayer;
    RnQueryObjectSet objectSetMask;
    CollisionGroup_t collisionGroup;
    uint8_t data; // bitfield: HitSolid=1, HitTrigger, ShouldIgnoreDisabledPairs, Unknown, etc.

    RnQueryShapeAttr_t() {
        memset(this, 0, sizeof(*this));
        entityIdsToIgnore[0] = 0xFFFFFFFF;
        entityIdsToIgnore[1] = 0xFFFFFFFF;
        ownerIdsToIgnore[0] = 0xFFFFFFFF;
        ownerIdsToIgnore[1] = 0xFFFFFFFF;
        objectSetMask = RnQueryObjectSet::ALL;
        collisionGroup = CollisionGroup_t::ALWAYS;
        data = 0x49; // HitSolid | ShouldIgnoreDisabledPairs | Unknown
    }
};
#pragma pack(pop)

struct alignas(8) CTraceFilter {
    void *vtable;
    RnQueryShapeAttr_t queryShapeAttributes;
    uint8_t iterateEntities;

    CTraceFilter() {
        memset(this, 0, sizeof(*this));
        new (&queryShapeAttributes) RnQueryShapeAttr_t();
    }
};
static_assert(sizeof(CTraceFilter) == 72);

struct alignas(16) CGameTrace {
    void *surfaceProperties;   // 0x00
    void *pEntity;             // 0x08
    void *hitBox;              // 0x10
    void *body;                // 0x18
    void *shape;               // 0x20
    uint64_t contents;         // 0x28
    uint8_t bodyTransform[32]; // 0x30 - CTransform (quaternion + position)
    uint8_t shapeAttributes[40]; // 0x50 - RnCollisionAttr_t
    Vector startPos;           // 0x78
    Vector endPos;             // 0x84
    Vector hitNormal;          // 0x90
    Vector hitPoint;           // 0x9C
    float hitOffset;           // 0xA8
    float fraction;            // 0xAC
    int32_t triangle;          // 0xB0
    int32_t unk_B4;            // 0xB4 - new in 6711, copied together with triangle from the hit; -1 when unset
    int16_t hitboxBoneIndex;   // 0xB8
    RayType_t rayType;         // 0xBA
    bool startInSolid;         // 0xBB
    bool exactHitPoint;        // 0xBC
    uint8_t _pad[3];           // 0xBD

    CGameTrace() {
        memset(this, 0, sizeof(*this));
        // Identity quaternion for body transform (w = 1.0 at offset 0x0C within transform)
        *reinterpret_cast<float *>(bodyTransform + 0x0C) = 1.0f;
        fraction = 1.0f;
        triangle = -1;
        unk_B4 = -1;
        hitboxBoneIndex = -1;
    }

    bool DidHit() const { return fraction < 1.0f || startInSolid; }
};
static_assert(sizeof(CGameTrace) == 0xC0);
static_assert(offsetof(CGameTrace, fraction) == 0xAC);
static_assert(offsetof(CGameTrace, hitboxBoneIndex) == 0xB8);
static_assert(offsetof(CGameTrace, startInSolid) == 0xBB);

// ---------------------------------------------------------------------------
// Hook declarations
// ---------------------------------------------------------------------------

namespace hooks {

inline safetyhook::InlineHook g_TraceShape;
void __fastcall Hook_TraceShape(void *pPhysQuery, Ray_t *ray, Vector *start, Vector *end, CTraceFilter *filter, CGameTrace *traceOutput);

} // namespace hooks

// Physics query interface pointer, captured by TraceShape hook
inline void *g_pPhysicsQuery = nullptr;

} // namespace deadworks
