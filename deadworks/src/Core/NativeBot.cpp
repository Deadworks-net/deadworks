#include "NativeBot.hpp"

#include <cmath>

#include <mathlib/vector.h>

#include "NativeOffsets.hpp"
#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CCitadelPlayerPawn.hpp"

using namespace deadworks;
using namespace deadworks::offsets;

// A bot hero is three objects: the pawn, a CCitadelPlayerBot hanging off it (m_pBot), and a brain entity
// (npc_player_bot_brain, a CAI_CitadelNPC) that owns the navigator and schedules. Each pawn tick,
// CCitadelPlayerBot::PhysicsSimulate either lets the brain think and send a usercmd (a "bot match"), or,
// in "simple bot mode", sends a blank usercmd and never asks the brain. Every lobby-less server is in simple
// mode unless the bot-match flag is set.

// --- Function pointer types ---

using BrainUpdateMovementFn = void(__fastcall *)(void *brain);
using BrainGetDesiredMoveDirFn = void *(__fastcall *)(void *brain, Vector *out);
using CreateBotByHeroNameFn = void *(__fastcall *)(void *gameRules, const char *heroName, int32_t team, const Vector *position);
using AssignBotLaneFn = void(__fastcall *)(void *gameRules, int32_t team, void *pawn);
using BotSetLaneFn = void(__fastcall *)(void *bot, int32_t lane);

static BrainUpdateMovementFn g_pBrainUpdateMovement = nullptr;
static BrainGetDesiredMoveDirFn g_pBrainGetDesiredMoveDir = nullptr;
static CreateBotByHeroNameFn g_pCreateBotByHeroName = nullptr;
static AssignBotLaneFn g_pAssignBotLane = nullptr;
static BotSetLaneFn g_pBotSetLane = nullptr;
static int32_t g_botMatchFlagOffset = 0; // on CCitadelGameRules; not in the schema, and it moves between builds

// What NativeBotGetMoveInput hands to managed code. Must match BotMoveInput in CCitadelPlayerBotNPCBrain.cs.
struct BotMoveInput {
    float forward;
    float side;
    uint64_t heldButtons;
    uint64_t pressedButtons;
    float pitch;
    float yaw;
};
static_assert(sizeof(BotMoveInput) == 32);

// ---------------------------------------------------------------------------
// Native implementations
// ---------------------------------------------------------------------------

// The brain entity driving a bot hero, or null for a hero with no bot AI attached.
static void *__cdecl NativeBotGetBrain(void *pawn) {
    if (!pawn) return nullptr;
    auto *bot = static_cast<uint8_t *>(static_cast<CCitadelPlayerPawn *>(pawn)->m_pBot.Get());
    return bot ? *reinterpret_cast<void **>(bot + kBotBrain) : nullptr;
}

// The hero a brain drives.
static void *__cdecl NativeBotGetPawn(void *brain) {
    return brain ? *reinterpret_cast<void **>(static_cast<uint8_t *>(brain) + kBrainPawn) : nullptr;
}

// Turns what the brain's navigator wants into movement input, with the game's own UpdateMovement: the move
// direction projected onto the view angles as forward/side, plus the jump it presses when the next waypoint is
// above step height. In a bot match the brain does this itself every tick; in simple mode nothing does, so
// a plugin calls this from its usercmd hook to walk a bot along a ForceNpcGo path with the stock AI off.
// viewAngles (pitch, yaw) is where the bot looks; null faces the way it is moving. Returns 0 when the brain
// has nowhere to go (no movement task running, dead pawn, citadel_bot_brain_disable_movement).
static uint8_t __cdecl NativeBotGetMoveInput(void *brainEntity, const float *viewAngles, void *outInput) {
    auto *brain = static_cast<uint8_t *>(brainEntity);
    auto *out = static_cast<BotMoveInput *>(outInput);
    if (!brain || !out || !g_pBrainUpdateMovement || !g_pBrainGetDesiredMoveDir) return 0;
    // UpdateMovement calls through the pawn without checking it.
    if (!*reinterpret_cast<void **>(brain + kBrainPawn)) return 0;

    auto *view = reinterpret_cast<float *>(brain + kBrainViewAngles);
    if (viewAngles) {
        view[0] = viewAngles[0];
        view[1] = viewAngles[1];
    } else {
        Vector direction(0, 0, 0);
        g_pBrainGetDesiredMoveDir(brain, &direction);
        if (direction.x != 0.0f || direction.y != 0.0f) {
            view[0] = 0.0f;
            view[1] = std::atan2(direction.y, direction.x) * 57.29578f;
        }
    }
    view[2] = 0.0f;

    // Left over from the last call; UpdateMovement only writes them when it has somewhere to go, and it
    // refuses to run while one of the held bits is set.
    *reinterpret_cast<float *>(brain + kBrainMoveForward) = 0.0f;
    *reinterpret_cast<float *>(brain + kBrainMoveSide) = 0.0f;
    *reinterpret_cast<uint64_t *>(brain + kBrainHeldButtons) = 0;
    *reinterpret_cast<uint64_t *>(brain + kBrainPressedButtons) = 0;

    g_pBrainUpdateMovement(brain);

    out->forward = *reinterpret_cast<float *>(brain + kBrainMoveForward);
    out->side = *reinterpret_cast<float *>(brain + kBrainMoveSide);
    out->heldButtons = *reinterpret_cast<uint64_t *>(brain + kBrainHeldButtons);
    out->pressedButtons = *reinterpret_cast<uint64_t *>(brain + kBrainPressedButtons);
    out->pitch = view[0];
    out->yaw = view[1];
    return out->forward != 0.0f || out->side != 0.0f || out->heldButtons || out->pressedButtons;
}

static uint8_t *BotOf(void *pawn) {
    return pawn ? static_cast<uint8_t *>(static_cast<CCitadelPlayerPawn *>(pawn)->m_pBot.Get()) : nullptr;
}

// Adds a bot hero the way citadel_create_unit does, so the bot gets its hero loaded and its AI attached by the
// game itself. heroName is a hero_* name or "none" for a random hero bots may play. With assignLane the game
// also picks it a lane, as it does for practice bots. Returns the pawn, or null (the game logs why).
static void *__cdecl NativeBotCreate(void *gameRules, const char *heroName, int32_t team, const float *position, uint8_t assignLane) {
    if (!gameRules || !heroName || !position || !g_pCreateBotByHeroName) return nullptr;
    const Vector pos(position[0], position[1], position[2]);
    void *pawn = g_pCreateBotByHeroName(gameRules, heroName, team, &pos);
    if (pawn && assignLane && g_pAssignBotLane && BotOf(pawn))
        g_pAssignBotLane(gameRules, team, pawn);
    return pawn;
}

// Sends a bot hero down a lane. Returns 0 for a pawn with no bot AI.
static uint8_t __cdecl NativeBotSetLane(void *pawn, int32_t lane) {
    auto *bot = BotOf(pawn);
    if (!bot || !g_pBotSetLane) return 0;
    g_pBotSetLane(bot, lane);
    return 1;
}

// The lane a bot hero was sent down, or 0 for none / no bot AI.
static int32_t __cdecl NativeBotGetLane(void *pawn) {
    auto *bot = BotOf(pawn);
    return bot ? *reinterpret_cast<int32_t *>(bot + kBotAssignedLane) : 0;
}

// The CBotDifficultyDefinition (bot_difficulty.vdata entry) a bot hero plays with, or null.
static void *__cdecl NativeBotGetDifficulty(void *pawn) {
    auto *bot = BotOf(pawn);
    return bot ? *reinterpret_cast<void **>(bot + kBotDifficulty) : nullptr;
}

// Where CCitadelGameRules keeps its "this is a bot match" byte, or 0 if it could not be resolved. Bots only
// leave simple mode (think, path, fight, buy) while it or citadel_solo_bot_match is set.
static int32_t __cdecl NativeBotGetMatchFlagOffset() {
    return g_botMatchFlagOffset;
}

// ---------------------------------------------------------------------------
// Resolution & populate
// ---------------------------------------------------------------------------

void deadworks::ResolveBotStatics() {
    auto &loader = MemoryDataLoader::Get();
    const auto resolve = [&loader](const char *key) { return loader.GetOffset(key).value(); };

    g_pBrainUpdateMovement = reinterpret_cast<BrainUpdateMovementFn>(resolve("CCitadelPlayerBotNPCBrain::UpdateMovement"));
    g_pBrainGetDesiredMoveDir = reinterpret_cast<BrainGetDesiredMoveDirFn>(resolve("CCitadelPlayerBotNPCBrain::GetDesiredMoveDir"));
    g_pCreateBotByHeroName = reinterpret_cast<CreateBotByHeroNameFn>(resolve("CCitadelGameRules::CreateBotByHeroName"));
    g_pAssignBotLane = reinterpret_cast<AssignBotLaneFn>(resolve("CCitadelGameRules::AssignBotLane"));
    g_pBotSetLane = reinterpret_cast<BotSetLaneFn>(resolve("CCitadelPlayerBot::SetLane"));

    // IsBotMatch: mov rax, [convar]; cmp byte [rax+58h], 0; jnz; cmp byte [rcx+disp32], 0. The disp32 is
    // the flag's offset on the game rules (0x458 in 6722, 0x460 in 6762).
    auto isBotMatch = resolve("CCitadelGameRules::IsBotMatch");
    const auto offset = *reinterpret_cast<int32_t *>(isBotMatch + kIsBotMatch_FlagDisp);
    if (offset > 0 && offset < kMaxGameRulesSize) g_botMatchFlagOffset = offset;
}

void deadworks::PopulateBotNatives(NativeCallbacks &cb) {
    cb.BotGetBrain = &NativeBotGetBrain;
    cb.BotGetPawn = &NativeBotGetPawn;
    cb.BotGetMoveInput = &NativeBotGetMoveInput;
    cb.BotGetMatchFlagOffset = &NativeBotGetMatchFlagOffset;
    cb.BotCreate = &NativeBotCreate;
    cb.BotSetLane = &NativeBotSetLane;
    cb.BotGetLane = &NativeBotGetLane;
    cb.BotGetDifficulty = &NativeBotGetDifficulty;
}
