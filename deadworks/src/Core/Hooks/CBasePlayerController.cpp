#include "CBasePlayerController.hpp"

#include <cstddef>

#include <iserver.h>
#include <serversideclient.h>
#include <interfaces/interfaces.h>

#include "../Deadworks.hpp"
#include "../../SDK/CBasePlayerController.hpp"

namespace deadworks {
namespace hooks {

// The fields ForceFullUpdate writes, checked against the live engine2.
static_assert(offsetof(CServerSideClientBase, m_nDeltaTick) == 0x15C);
static_assert(offsetof(CServerSideClientBase, m_nHltvReplayDeltaTick) == 0x160);
static_assert(offsetof(CServerSideClientBase, m_nForceWaitForTick) == 0x9DC);

// Makes the engine send the client an uncompressed snapshot next. The sourcesdk's CServerSideClientBase::ForceFullUpdate()
// only clears m_nDeltaTick, which does nothing once a reliable delta has gone out: the engine deltas from
// max(m_nDeltaTick, m_nHltvReplayDeltaTick), and the latter is really "last reliable delta tick" (set on every reliable
// delta while sv_disable_reliable_delta_retransmit is on). m_nForceWaitForTick is cleared too, or a full update that is
// still waiting for its ack would hold snapshots back and then overwrite m_nDeltaTick when that ack arrives.
static void ForceFullUpdate(CServerSideClientBase *client) {
    client->m_nDeltaTick = -1;
    client->m_nHltvReplayDeltaTick = -1;
    client->m_nForceWaitForTick = -1;
}

// A pawn change mid-connection freezes the client for good (0 HP, blank portrait, can't move or look,
// "Prediction time ... is less than sim time" spam) whenever the snapshot carrying the new m_hPawn goes out unreliably
// (<= sv_max_unreliable_delta_size) and the next snapshot is built before the client's ack arrives: that snapshot is
// delta'd from before the change and resends m_hPawn. client.dll's m_hPawn change callback has no old != new check, so
// the resend makes it call ForceFullUpdate("pawn swapped"); the client then drops every delta and acks -1, and the
// engine's UpdateAcknowledgedFramecount treats a -1 ack as "no change" and never sends the update. Matchmaking never
// swaps a connected player's pawn, but changeteam from team select does (and so can plugins), and on maps where joining
// a team reveals little (no vis, sparse layouts) the join snapshot is small enough to lose that race nearly every time.
// Sending the full update from the same frame as the swap means the new pawn never arrives as a delta at all.
void __fastcall Hook_CBasePlayerController_SetPawn(CBasePlayerController *thisptr, CBasePlayerPawn *pPawn, bool bRetainOldPawnTeam, bool bCopyMovementState, bool bAllowTeamMismatch, bool bPreserveMovementState) {
    const int oldPawn = thisptr->m_hPawn.Get().ToInt();

    g_CBasePlayerController_SetPawn.call<void>(thisptr, pPawn, bRetainOldPawnTeam, bCopyMovementState, bAllowTeamMismatch, bPreserveMovementState);

    const CEntityHandle newPawn = thisptr->m_hPawn.Get();
    if (!newPawn.IsValid() || newPawn.ToInt() == oldPawn || !g_pNetworkServerService)
        return;

    auto *server = g_pNetworkServerService->GetIGameServer();
    if (!server)
        return;

    auto *client = server->GetClientBySlot(CPlayerSlot(thisptr->GetEntityIndex().Get() - 1));
    if (!client || client->IsFakeClient() || client->IsHLTV() || !client->IsInGame())
        return;

    ForceFullUpdate(client);
    g_Log->Debug("Pawn changed for slot {}; forcing a full update", client->GetPlayerSlot().Get());
}

} // namespace hooks
} // namespace deadworks
