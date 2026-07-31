#include "NativeHero.hpp"
#include "NativeOffsets.hpp"
#include "Deadworks.hpp"

#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CBaseEntity.hpp"
#include "../SDK/CCitadelPlayerController.hpp"
#include "../SDK/CEntitySystem.hpp"
#include "../SDK/Core.hpp"
#include "../SDK/Util.hpp"

using namespace deadworks;
using namespace deadworks::offsets;

// --- Function pointer types ---

using EmitSoundParamsFn = void(__fastcall *)(void *entity, const char *soundName, int pitch, float volume, float delay);
using PawnResetHeroFn = __int64(__fastcall *)(void *pawn, bool bReset);
using AddResourceFn = void (*)(const char *path, void *manifest);
using GetHeroTableFn = void *(__fastcall *)();
using HeroPrecacheFn = void(__fastcall *)(void *globalSet, const char *heroName, void *resourceCtx);
using GetHeroDataManagerFn = void *(*)();
using HeroNameToIdFn = int *(*)(void *manager, int *outId, const char *heroName);
using GetHeroByIdFn = void *(*)(void *manager, unsigned int heroId);
using CreateHeroPawnFn = void *(*)(void *controller, int teamNum);
using SelectHeroInternalFn = void (*)(void *pawn, void *heroDef);
using TeleportFn = void (*)(CBaseEntity *entity, const Vector *position, const QAngle *angles, const Vector *velocity);

// --- Resolved wrappers ---

// Since the July 2026 patch this takes a CHeroDefinition* rather than a hero
// name - the name->definition wrapper was inlined into ClientConCommand, so we
// resolve the name ourselves via the CHeroDefinitionManager helpers below.
static void SelectHeroInternal(void *pawn, void *heroDef) {
    static const auto fn = reinterpret_cast<SelectHeroInternalFn>(
        MemoryDataLoader::Get().GetOffset("CCitadelPlayerPawn::SelectHeroInternal").value());
    fn(pawn, heroDef);
}

static void *GetHeroDataManager() {
    static const auto anchorAddr =
        MemoryDataLoader::Get().GetOffset("CHeroDefinitionManager::GetManagerAnchor").value();
    static const auto fn = reinterpret_cast<GetHeroDataManagerFn>(
        ResolveE8Call(anchorAddr + kHeroDefMgrAnchor_GetManagerCall));
    return fn();
}

static int HeroNameToId(void *manager, const char *heroName) {
    static const auto fn = reinterpret_cast<HeroNameToIdFn>(
        MemoryDataLoader::Get().GetOffset("CHeroDefinitionManager::HeroNameToId").value());
    int outId = 0;
    fn(manager, &outId, heroName);
    return outId;
}

static void *GetHeroById(void *manager, int heroId) {
    static const auto fn = reinterpret_cast<GetHeroByIdFn>(
        MemoryDataLoader::Get().GetOffset("CHeroDefinitionManager::GetHeroById").value());
    return fn(manager, static_cast<unsigned int>(heroId));
}

// --- Static function pointers ---

static EmitSoundParamsFn g_pEmitSoundParams = nullptr;
static PawnResetHeroFn g_pPawnResetHero = nullptr;
static AddResourceFn g_pAddResource = nullptr;
static GetHeroTableFn g_pGetHeroTable = nullptr;
static HeroPrecacheFn g_pHeroPrecache = nullptr;
static void *g_pHeroPrecacheGlobal = nullptr;

void *deadworks::g_pCurrentManifest = nullptr;
void *deadworks::g_pCurrentResourceCtx = nullptr;

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

static void __cdecl NativeEmitSound(void *entity, const char *soundName, int32_t pitch, float volume, float delay) {
    if (!entity || !soundName || !g_pEmitSoundParams)
        return;
    g_pEmitSoundParams(entity, soundName, pitch, volume, delay);
}

static void __cdecl NativeResetHero(void *pawn, uint8_t bReset) {
    if (!pawn || !g_pPawnResetHero)
        return;
    g_pPawnResetHero(pawn, bReset != 0);
}

static void *__cdecl NativeGetHeroData(const char *heroName) {
    if (!heroName)
        return nullptr;

    void *manager = GetHeroDataManager();
    if (!manager)
        return nullptr;

    int heroId = HeroNameToId(manager, heroName);
    if (heroId <= 0)
        return nullptr;

    // GetHeroById does its own bounds check against the manager's hero count.
    void *data = GetHeroById(manager, heroId);
    g_Log->Debug("GetHeroData({}): id={} ptr={}", heroName, heroId, data ? "found" : "null");
    return data;
}

static void __cdecl NativeChangeTeam(void *controller, int32_t teamNum) {
    if (!controller)
        return;
    auto changeTeamFn = GetVFunc<void (*)(void *, int)>(controller, kVtblChangeTeam);
    changeTeamFn(controller, teamNum);
}

static void __cdecl NativeSelectHero(void *controller, const char *heroName) {
    if (!controller || !heroName)
        return;

    // Resolve the hero definition first, matching the game's own ordering in
    // ClientConCommand - an unknown hero name must not spawn a pawn.
    void *heroDef = NativeGetHeroData(heroName);
    if (!heroDef)
        return;

    auto *pawn = static_cast<CCitadelPlayerController *>(controller)->GetHeroPawn();
    if (!pawn) {
        static const auto createPawn = reinterpret_cast<CreateHeroPawnFn>(
            MemoryDataLoader::Get().GetOffset("CCitadelPlayerController::CreateHeroPawn").value());
        int teamNum = static_cast<CCitadelPlayerController *>(controller)->m_iTeamNum.Get();
        pawn = static_cast<decltype(pawn)>(createPawn(controller, teamNum));
    }
    if (!pawn)
        return;

    SelectHeroInternal(pawn, heroDef);
}

static void __cdecl NativePrecacheResource(const char *path) {
    if (!path || !g_pAddResource || !g_pCurrentManifest)
        return;
    g_pAddResource(path, g_pCurrentManifest);
}

static void __cdecl NativePrecacheHero(const char *heroName) {
    if (!heroName || !g_pHeroPrecache || !g_pHeroPrecacheGlobal || !g_pCurrentResourceCtx)
        return;
    g_pHeroPrecache(g_pHeroPrecacheGlobal, heroName, g_pCurrentResourceCtx);
}

static void __cdecl NativeTeleport(void *entity, const float *position, const float *angles, const float *velocity) {
    if (!entity)
        return;
    auto fn = GetVFunc<TeleportFn>(entity, kVtblTeleport);
    fn(static_cast<CBaseEntity *>(entity),
       position ? reinterpret_cast<const Vector *>(position) : nullptr,
       angles ? reinterpret_cast<const QAngle *>(angles) : nullptr,
       velocity ? reinterpret_cast<const Vector *>(velocity) : nullptr);
}

// ---------------------------------------------------------------------------
// Resolution
// ---------------------------------------------------------------------------

void deadworks::ResolveHeroStatics() {
    g_pPawnResetHero = reinterpret_cast<PawnResetHeroFn>(
        MemoryDataLoader::Get().GetOffset("CCitadelPlayerPawn::ResetHero").value());

    g_pEmitSoundParams = reinterpret_cast<EmitSoundParamsFn>(
        MemoryDataLoader::Get().GetOffset("CBaseEntity::EmitSoundParams").value());
    g_Log->Info("Resolved CBaseEntity::EmitSoundParams: {:p}", reinterpret_cast<void *>(g_pEmitSoundParams));

    g_pAddResource = reinterpret_cast<AddResourceFn>(
        MemoryDataLoader::Get().GetOffset("AddResource").value());
    g_Log->Info("Resolved AddResource: {:p}", reinterpret_cast<void *>(g_pAddResource));
}

void deadworks::ResolveHeroPrecacheFns() {
    auto addr = MemoryDataLoader::Get().GetOffset("CCitadelGameRules::BuildGameSessionManifest").value();
    g_pGetHeroTable = reinterpret_cast<GetHeroTableFn>(ResolveE8Call(addr + kBGSM_GetHeroTableCall));
    g_pHeroPrecacheGlobal = reinterpret_cast<void *>(ResolveLea(addr + kBGSM_PrecacheGlobalLea));
    g_pHeroPrecache = reinterpret_cast<HeroPrecacheFn>(ResolveE8Call(addr + kBGSM_PrecacheCall));
    g_Log->Info("HeroPrecache: table={} precache={} global={}",
        (void *)g_pGetHeroTable, (void *)g_pHeroPrecache, g_pHeroPrecacheGlobal);
}

bool deadworks::IsHeroPrecacheResolved() {
    return g_pGetHeroTable != nullptr;
}

// ---------------------------------------------------------------------------
// Populate
// ---------------------------------------------------------------------------

void deadworks::PopulateHeroNatives(NativeCallbacks &cb) {
    cb.EmitSound = &NativeEmitSound;
    cb.ResetHero = &NativeResetHero;
    cb.GetHeroData = &NativeGetHeroData;
    cb.ChangeTeam = &NativeChangeTeam;
    cb.SelectHero = &NativeSelectHero;
    cb.PrecacheResource = &NativePrecacheResource;
    cb.PrecacheHero = &NativePrecacheHero;
    cb.Teleport = &NativeTeleport;
}
