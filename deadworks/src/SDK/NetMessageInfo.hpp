#pragma once

#include <cstdint>

class CNetMessage;

namespace deadworks {

// What INetworkMessages::FindNetworkMessageById / CNetMessage::GetSerializerPB hand out since the 2026-09-29
// Deadlock build. It is a plain 0x28-byte record, not an INetworkMessageInternal: messages are registered with an
// allocation function instead of an IProtobufBinding (networksystem CNetworkMessages slot 20), and the record keeps
// that function at +8. m_szName is the engine's unscoped name, formatted as "CCitadelUserMsg_ChatMsg [314]".
struct NetMessageInfoDL {
    const char *m_szName;                 // CUtlString
    CNetMessage *(*m_pfnAllocate)();      // factory passed at registration, returns a fresh CNetMessagePB
    const char *m_szGroup;                // CUtlString
    int32_t m_MessageId;
    int32_t m_unk1c;
    uint8_t m_unk20[5];
    uint8_t m_nFlags;                     // (1 << 5) / (1 << 6) from the registration bools, (1 << 7) category set
    uint8_t m_pad26[2];
};
static_assert(sizeof(NetMessageInfoDL) == 0x28, "NetMessageInfoDL must match the engine's 0x28-byte record");

} // namespace deadworks
