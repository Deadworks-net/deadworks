#include "NativeCollision.hpp"

#include "../Memory/MemoryDataLoader.hpp"

using namespace deadworks;

// Every setter here is the game's own CCollisionProperty helper. Each one snapshots
// m_collisionAttribute, changes it, networks only the fields that changed, then calls
// CBaseEntity::CollisionRulesChanged (vfunc 184) on the owner, which rebuilds the derived
// attribute fields (entity/owner/hierarchy ids, group, the PhysicsProp layer) and pushes the
// result onto the physics body. A schema write skips both the networking and the push, so
// clients keep predicting the old masks and the physics body never sees the change.

// --- Function pointer types ---

using ChangeCollisionLayersFn = void(__fastcall *)(void *collision, uint64_t layers);
using SetCollisionByteFn = void(__fastcall *)(void *collision, uint8_t value);
using SetCollisionEnabledFn = bool(__fastcall *)(void *collision);
using SetEntityCollisionsFn = void(__fastcall *)(void *entity, void *other);

// Indexed by the mask the managed side passes: 0 = InteractsAs, 1 = InteractsWith, 2 = InteractsExclude.
static ChangeCollisionLayersFn g_pAddLayers[3] = {};
static ChangeCollisionLayersFn g_pRemoveLayers[3] = {};
static SetCollisionByteFn g_pSetCollisionGroup = nullptr;
static SetCollisionByteFn g_pSetSolid = nullptr;
static SetCollisionByteFn g_pSetSolidFlags = nullptr;
static SetCollisionEnabledFn g_pEnableCollision = nullptr;
static SetCollisionEnabledFn g_pDisableCollision = nullptr;
static SetEntityCollisionsFn g_pPhysDisableEntityCollisions = nullptr;
static SetEntityCollisionsFn g_pPhysEnableEntityCollisions = nullptr;

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

static void __cdecl NativeAddCollisionLayers(void *collision, uint8_t mask, uint64_t layers) {
    if (!collision || mask > 2 || !g_pAddLayers[mask]) return;
    g_pAddLayers[mask](collision, layers);
}

static void __cdecl NativeRemoveCollisionLayers(void *collision, uint8_t mask, uint64_t layers) {
    if (!collision || mask > 2 || !g_pRemoveLayers[mask]) return;
    g_pRemoveLayers[mask](collision, layers);
}

// Also rewrites the Debris and TouchAll InteractsAs bits to match the new group.
static void __cdecl NativeSetCollisionGroup(void *collision, uint8_t group) {
    if (!collision || !g_pSetCollisionGroup) return;
    g_pSetCollisionGroup(collision, group);
}

// Only stores m_nSolidType. CModelState::DoSetupPhysics builds the physics shape from it the next
// time the model changes (setting the same model again returns early), so this alone doesn't
// rebuild an existing body.
static void __cdecl NativeSetSolid(void *collision, uint8_t solidType) {
    if (!collision || !g_pSetSolid) return;
    g_pSetSolid(collision, solidType);
}

static void __cdecl NativeSetSolidFlags(void *collision, uint8_t flags) {
    if (!collision || !g_pSetSolidFlags) return;
    g_pSetSolidFlags(collision, flags);
}

static void __cdecl NativeSetCollisionEnabled(void *collision, uint8_t enabled) {
    const auto fn = enabled ? g_pEnableCollision : g_pDisableCollision;
    if (!collision || !fn) return;
    fn(collision);
}

// PhysDisableEntityCollisions / PhysEnableEntityCollisions, what logic_collision_pair calls. They
// add the pair of entity handles to (or remove it from) the physics world's disabled-pair set, which
// vphysics2 checks before creating a contact between two bodies and, for queries that set
// ShouldIgnoreDisabledPairs, against the query's ignored entities. Both warn and do nothing when the
// entities are in different scene worlds.
static void __cdecl NativeSetEntityCollisionsWith(void *entity, void *other, uint8_t enabled) {
    const auto fn = enabled ? g_pPhysEnableEntityCollisions : g_pPhysDisableEntityCollisions;
    if (!entity || !other || !fn) return;
    fn(entity, other);
}

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveCollisionStatics() {
    auto &loader = MemoryDataLoader::Get();
    const auto resolve = [&loader](const char *key) { return loader.GetOffset(key).value(); };

    g_pAddLayers[0] = reinterpret_cast<ChangeCollisionLayersFn>(resolve("CCollisionProperty::AddInteractsAs"));
    g_pAddLayers[1] = reinterpret_cast<ChangeCollisionLayersFn>(resolve("CCollisionProperty::AddInteractsWith"));
    g_pAddLayers[2] = reinterpret_cast<ChangeCollisionLayersFn>(resolve("CCollisionProperty::AddInteractsExclude"));
    g_pRemoveLayers[0] = reinterpret_cast<ChangeCollisionLayersFn>(resolve("CCollisionProperty::RemoveInteractsAs"));
    g_pRemoveLayers[1] = reinterpret_cast<ChangeCollisionLayersFn>(resolve("CCollisionProperty::RemoveInteractsWith"));
    g_pRemoveLayers[2] = reinterpret_cast<ChangeCollisionLayersFn>(resolve("CCollisionProperty::RemoveInteractsExclude"));
    g_pSetCollisionGroup = reinterpret_cast<SetCollisionByteFn>(resolve("CCollisionProperty::SetCollisionGroup"));
    g_pSetSolid = reinterpret_cast<SetCollisionByteFn>(resolve("CCollisionProperty::SetSolid"));
    g_pSetSolidFlags = reinterpret_cast<SetCollisionByteFn>(resolve("CCollisionProperty::SetSolidFlags"));
    g_pEnableCollision = reinterpret_cast<SetCollisionEnabledFn>(resolve("CCollisionProperty::EnableCollision"));
    g_pDisableCollision = reinterpret_cast<SetCollisionEnabledFn>(resolve("CCollisionProperty::DisableCollision"));
    g_pPhysDisableEntityCollisions = reinterpret_cast<SetEntityCollisionsFn>(resolve("PhysDisableEntityCollisions"));
    g_pPhysEnableEntityCollisions = reinterpret_cast<SetEntityCollisionsFn>(resolve("PhysEnableEntityCollisions"));
}

void deadworks::PopulateCollisionNatives(NativeCallbacks &cb) {
    cb.AddCollisionLayers = &NativeAddCollisionLayers;
    cb.RemoveCollisionLayers = &NativeRemoveCollisionLayers;
    cb.SetCollisionGroup = &NativeSetCollisionGroup;
    cb.SetSolid = &NativeSetSolid;
    cb.SetSolidFlags = &NativeSetSolidFlags;
    cb.SetCollisionEnabled = &NativeSetCollisionEnabled;
    cb.SetEntityCollisionsWith = &NativeSetEntityCollisionsWith;
}
