#pragma once

#include <safetyhook.hpp>
#include <cstdint>

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_UsesMatchFlow;
inline safetyhook::InlineHook g_StartPlayersInLanes;

// CCitadelGameRules only runs its matchmade start on its own match map: 21 places in server.dll (6698) compare
// gpGlobals->mapname against "start" and "dl_midtown" inline. Two of them decide the start. On any other map the
// pre-game countdown in base is skipped and nobody rides a zipline. While this is set (from managed code via
// GameRules.MatchStartOnAnyMap) those two see "dl_midtown" instead of the real map.
inline bool g_MatchStartOnAnyMap = false;

// Guessed name. bool(CCitadelGameRules*): whether the matchmade flow applies. Needs the match map and then passes
// on citadel_match_intro_force_enabled. ChangeGameState asks it before PreGameWait (a false skips straight to
// GameInProgress), and so does GetPlayerStartLane.
bool __fastcall Hook_UsesMatchFlow(void *gameRules);

// Guessed name. void(CCitadelGameRules*, bool): run as GameInProgress begins. On the match map it puts every hero on
// its lane's zipline (citadel_start_players_on_zipline) and applies the cinematic intro modifiers; elsewhere it only
// respawns them in base.
void __fastcall Hook_StartPlayersInLanes(void *gameRules, bool bForceIntro);

} // namespace hooks
} // namespace deadworks
