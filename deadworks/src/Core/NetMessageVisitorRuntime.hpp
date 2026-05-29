#pragma once

#include <cstdint>

class CNetMessage;

namespace deadworks {

enum class NetMessageDirection : int32_t {
    Incoming = 0,
    Outgoing = 1,
};

void SetNetMessageSerializedInterest(NetMessageDirection direction, int32_t msgId, bool enabled);
void SetNetMessageFastInterest(NetMessageDirection direction, int32_t msgId, bool enabled);
void SetUserMessageSerializedInterest(int32_t userMessageType, bool enabled);
void SetUserMessageFastInterest(int32_t userMessageType, bool enabled);
bool HasNetMessageSerializedInterest(NetMessageDirection direction, int32_t msgId);
bool HasNetMessageFastInterest(NetMessageDirection direction, int32_t msgId);
bool HasAnyUserMessageSerializedInterest();
bool HasUserMessageSerializedInterest(int32_t userMessageType);
bool HasUserMessageFastInterest(int32_t userMessageType);

void ProcessFastNetMessage(NetMessageDirection direction, int32_t endpointSlot, int32_t msgId,
                           const CNetMessage *message, uint64_t recipientMask);

void LogNetMessageVisitorStatsIfDue();

} // namespace deadworks
