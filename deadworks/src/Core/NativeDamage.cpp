#include "NativeDamage.hpp"
#include "NativeOffsets.hpp"
#include "Deadworks.hpp"

#include "Hooks/CBaseEntity.hpp"
#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/Schema/Schema.hpp"

using namespace deadworks;
using namespace deadworks::offsets;

static int GetCTakeDamageInfoSize() {
    static int size = schema::GetClassSize("CTakeDamageInfo");
    return size ? size : 0x100; // fallback to known size if schema lookup fails
}

// --- Function pointer types ---

using CTakeDamageInfoCtorFn = void *(__fastcall *)(void *info, void *inflictor, void *attacker, void *ability, float damage, int damageType, int customDamage);
using CTakeDamageInfoDtorFn = void(__fastcall *)(void *info);

static CTakeDamageInfoCtorFn g_pCTakeDamageInfoCtor = nullptr;
static CTakeDamageInfoDtorFn g_pCTakeDamageInfoDtor = nullptr;

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

static void *__cdecl NativeCreateDamageInfo(void *inflictor, void *attacker, void *ability, float damage, int32_t damageType) {
    if (!g_pCTakeDamageInfoCtor) return nullptr;
    auto size = GetCTakeDamageInfoSize();
    auto *info = static_cast<uint8_t *>(_aligned_malloc(size, 16));
    if (!info) return nullptr;
    std::memset(info, 0, size);
    g_pCTakeDamageInfoCtor(info, inflictor, attacker ? attacker : inflictor, ability, damage, damageType, 0);
    return info;
}

static void __cdecl NativeDestroyDamageInfo(void *info) {
    if (!info) return;
    if (g_pCTakeDamageInfoDtor) g_pCTakeDamageInfoDtor(info);
    _aligned_free(info);
}

static void __cdecl NativeTakeDamage(void *victim, void *info) {
    if (!victim || !info) return;
    // Direct TakeDamageOld route - skips the ApplyDamage wrapper (see NativeApplyDamage).
    hooks::g_CBaseEntity_TakeDamageOld.thiscall<void>(victim, info, nullptr);
}

static void __cdecl NativeApplyDamage(void *victim, void *info) {
    if (!victim || !info) return;
    // Route through CBaseEntity::ApplyDamage, the engine's canonical
    // damage entry - NOT straight to TakeDamageOld. ApplyDamage runs the attribute /
    // resistance modification, does bunch of other stuff, then calls TakeDamageOld (whose inline hook still fires,
    // so plugin OnTakeDamage dispatch is preserved).
    static const auto target = MemoryDataLoader::Get().GetOffset("CBaseEntity::ApplyDamage");
    if (!target) return;
    reinterpret_cast<void (*)(void *, void *, void *)>(target.value())(victim, info, nullptr);
}

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveDamageStatics() {
    g_pCTakeDamageInfoCtor = reinterpret_cast<CTakeDamageInfoCtorFn>(
        MemoryDataLoader::Get().GetOffset("CTakeDamageInfo::Ctor").value());
    g_pCTakeDamageInfoDtor = reinterpret_cast<CTakeDamageInfoDtorFn>(
        MemoryDataLoader::Get().GetOffset("CTakeDamageInfo::Dtor").value());
}

void deadworks::PopulateDamageNatives(NativeCallbacks &cb) {
    cb.CreateDamageInfo = &NativeCreateDamageInfo;
    cb.DestroyDamageInfo = &NativeDestroyDamageInfo;
    cb.TakeDamage = &NativeTakeDamage;
    cb.ApplyDamage = &NativeApplyDamage;
}
