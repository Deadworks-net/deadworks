#include "MatchMapOverride.hpp"

#include <cstddef>

#include <eiface.h>
#include <edict.h>
#include <interfaces/interfaces.h>

namespace deadworks {
namespace hooks {

// server.dll reads the map name at gpGlobals + 0x60.
static_assert(offsetof(CGlobalVars, mapname) == 0x60);

namespace {

// Shows the hooked function "dl_midtown" as the map for as long as it runs.
class MatchMapScope {
public:
    MatchMapScope() {
        if (!g_MatchStartOnAnyMap || !g_pEngineServer)
            return;
        m_globals = g_pEngineServer->GetServerGlobals();
        if (!m_globals)
            return;
        m_saved = m_globals->mapname;
        m_globals->mapname = MAKE_STRING(MatchMap);
    }

    ~MatchMapScope() {
        if (m_globals)
            m_globals->mapname = m_saved;
    }

    MatchMapScope(const MatchMapScope &) = delete;
    MatchMapScope &operator=(const MatchMapScope &) = delete;

private:
    static constexpr const char *MatchMap = "dl_midtown";

    CGlobalVars *m_globals = nullptr;
    string_t m_saved{};
};

} // namespace

bool __fastcall Hook_UsesMatchFlow(void *gameRules) {
    MatchMapScope scope;
    return g_UsesMatchFlow.call<bool>(gameRules);
}

void __fastcall Hook_StartPlayersInLanes(void *gameRules, bool bForceIntro) {
    MatchMapScope scope;
    g_StartPlayersInLanes.call<void>(gameRules, bForceIntro);
}

} // namespace hooks
} // namespace deadworks
