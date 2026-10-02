#pragma once

#include <safetyhook.hpp>

class CBasePlayerController;
class CBasePlayerPawn;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_CBasePlayerController_SetPawn;
void __fastcall Hook_CBasePlayerController_SetPawn(CBasePlayerController *thisptr, CBasePlayerPawn *pPawn, bool bRetainOldPawnTeam, bool bCopyMovementState, bool bAllowTeamMismatch, bool bPreserveMovementState);

} // namespace hooks
} // namespace deadworks
