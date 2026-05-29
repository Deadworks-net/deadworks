#include "NetMessageVisitorRuntime.hpp"

#include "Deadworks.hpp"

#include <array>
#include <atomic>
#include <chrono>
#include <cstdlib>

#include <networksystem/netmessage.h>
#include <netmessages.pb.h>

namespace deadworks {
namespace {

constexpr int kMaxTrackedMessageId = 4096;

using InterestTable = std::array<std::atomic_uint8_t, kMaxTrackedMessageId>;

InterestTable g_serializedIncoming{};
InterestTable g_serializedOutgoing{};
InterestTable g_fastIncoming{};
InterestTable g_fastOutgoing{};
InterestTable g_serializedUserMessages{};
InterestTable g_fastUserMessages{};

std::atomic_uint32_t g_serializedUserMessageInterestCount{0};
std::atomic_uint32_t g_fastUserMessageInterestCount{0};
std::atomic_uint32_t g_totalInterestCount{0};

std::atomic_uint64_t g_incomingSeen{0};
std::atomic_uint64_t g_outgoingSeen{0};
std::atomic_uint64_t g_serializedIncomingHits{0};
std::atomic_uint64_t g_serializedOutgoingHits{0};
std::atomic_uint64_t g_fastIncomingCallbacks{0};
std::atomic_uint64_t g_fastOutgoingCallbacks{0};
std::atomic_uint64_t g_fastUserMessageCallbacks{0};
std::atomic_uint64_t g_noInterestIncoming{0};
std::atomic_uint64_t g_noInterestOutgoing{0};
std::atomic_uint64_t g_directExceptions{0};

std::chrono::steady_clock::time_point g_lastProbeLog{};

bool IsValidMessageId(int32_t msgId) {
    return msgId >= 0 && msgId < kMaxTrackedMessageId;
}

bool IsValidUserMessageType(int32_t userMessageType) {
    return userMessageType >= 0 && userMessageType < kMaxTrackedMessageId;
}

bool HasAnyUserMessageFastInterest() {
    return g_fastUserMessageInterestCount.load(std::memory_order_relaxed) != 0;
}

bool HasAnyInterest() {
    return g_totalInterestCount.load(std::memory_order_relaxed) != 0;
}

void TrackTotalInterest(uint8_t wasEnabled, bool enabled) {
    if (!wasEnabled && enabled) {
        g_totalInterestCount.fetch_add(1, std::memory_order_relaxed);
    } else if (wasEnabled && !enabled) {
        g_totalInterestCount.fetch_sub(1, std::memory_order_relaxed);
    }
}

void SetMessageInterest(InterestTable &table, int32_t msgId, bool enabled) {
    if (!IsValidMessageId(msgId))
        return;

    auto &entry = table[static_cast<size_t>(msgId)];
    uint8_t wasEnabled = entry.exchange(enabled ? 1 : 0, std::memory_order_relaxed);
    TrackTotalInterest(wasEnabled, enabled);
}

void SetUserMessageInterest(InterestTable &table, std::atomic_uint32_t &count, int32_t userMessageType, bool enabled) {
    if (!IsValidUserMessageType(userMessageType))
        return;

    auto &entry = table[static_cast<size_t>(userMessageType)];
    uint8_t wasEnabled = entry.exchange(enabled ? 1 : 0, std::memory_order_relaxed);
    if (!wasEnabled && enabled) {
        count.fetch_add(1, std::memory_order_relaxed);
    } else if (wasEnabled && !enabled) {
        count.fetch_sub(1, std::memory_order_relaxed);
    }
    TrackTotalInterest(wasEnabled, enabled);
}

InterestTable &TableFor(NetMessageDirection direction, bool serialized) {
    if (serialized)
        return direction == NetMessageDirection::Incoming ? g_serializedIncoming : g_serializedOutgoing;
    return direction == NetMessageDirection::Incoming ? g_fastIncoming : g_fastOutgoing;
}

bool EnvFlag(const char *name) {
    if (const char *value = std::getenv(name)) {
        return value[0] == '1' || value[0] == 't' || value[0] == 'T' || value[0] == 'y' || value[0] == 'Y';
    }
    return false;
}

bool ProbeLogEnabled() {
    static const bool enabled = EnvFlag("DEADWORKS_NETMSG_PROBE_LOG");
    return enabled;
}

int ProbeWindowMs() {
    static const int windowMs = [] {
        if (const char *value = std::getenv("DEADWORKS_NETMSG_PROBE_LOG_WINDOW_MS")) {
            int parsed = std::atoi(value);
            if (parsed > 0)
                return parsed;
        }
        return 5000;
    }();
    return windowMs;
}

struct FastNetMessageNative {
    int32_t userMessageType = -1;
    uint8_t hasUserMessageType = 0;
    int32_t pauseType = 0;
    int32_t pauseGroup = 0;
    uint8_t hasPauseRequest = 0;
    uint8_t paused = 0;
    uint8_t hasPauseState = 0;
};

FastNetMessageNative ExtractFastFields(NetMessageDirection direction, int32_t msgId, const CNetMessage *message) {
    FastNetMessageNative result{};
    if (!message)
        return result;

    __try {
        auto *pb = message->AsMessageLite();
        if (!pb)
            return result;

        if (direction == NetMessageDirection::Incoming && msgId == clc_RequestPause) {
            auto *pause = static_cast<const CCLCMsg_RequestPause *>(pb);
            result.hasPauseRequest = 1;
            result.pauseType = pause->has_pause_type() ? static_cast<int32_t>(pause->pause_type()) : static_cast<int32_t>(RP_PAUSE);
            result.pauseGroup = pause->has_pause_group() ? pause->pause_group() : 0;
            return result;
        }

        if (direction == NetMessageDirection::Outgoing && msgId == svc_SetPause) {
            auto *pause = static_cast<const CSVCMsg_SetPause *>(pb);
            result.hasPauseState = 1;
            result.paused = pause->has_paused() && pause->paused() ? 1 : 0;
            return result;
        }

        if (direction == NetMessageDirection::Outgoing && msgId == svc_UserMessage) {
            auto *user = static_cast<const CSVCMsg_UserMessage *>(pb);
            if (user->has_msg_type()) {
                result.hasUserMessageType = 1;
                result.userMessageType = user->msg_type();
            }
            return result;
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        g_directExceptions.fetch_add(1, std::memory_order_relaxed);
    }

    return result;
}

} // namespace

void SetNetMessageSerializedInterest(NetMessageDirection direction, int32_t msgId, bool enabled) {
    SetMessageInterest(TableFor(direction, true), msgId, enabled);
}

void SetNetMessageFastInterest(NetMessageDirection direction, int32_t msgId, bool enabled) {
    SetMessageInterest(TableFor(direction, false), msgId, enabled);
}

void SetUserMessageSerializedInterest(int32_t userMessageType, bool enabled) {
    SetUserMessageInterest(g_serializedUserMessages, g_serializedUserMessageInterestCount, userMessageType, enabled);
}

void SetUserMessageFastInterest(int32_t userMessageType, bool enabled) {
    SetUserMessageInterest(g_fastUserMessages, g_fastUserMessageInterestCount, userMessageType, enabled);
}

bool HasNetMessageSerializedInterest(NetMessageDirection direction, int32_t msgId) {
    if (!IsValidMessageId(msgId))
        return false;
    return TableFor(direction, true)[static_cast<size_t>(msgId)].load(std::memory_order_relaxed) != 0;
}

bool HasNetMessageFastInterest(NetMessageDirection direction, int32_t msgId) {
    if (!IsValidMessageId(msgId))
        return false;
    return TableFor(direction, false)[static_cast<size_t>(msgId)].load(std::memory_order_relaxed) != 0;
}

bool HasAnyUserMessageSerializedInterest() {
    return g_serializedUserMessageInterestCount.load(std::memory_order_relaxed) != 0;
}

bool HasUserMessageSerializedInterest(int32_t userMessageType) {
    if (!IsValidUserMessageType(userMessageType))
        return false;
    return g_serializedUserMessages[static_cast<size_t>(userMessageType)].load(std::memory_order_relaxed) != 0;
}

bool HasUserMessageFastInterest(int32_t userMessageType) {
    if (!IsValidUserMessageType(userMessageType))
        return false;
    return g_fastUserMessages[static_cast<size_t>(userMessageType)].load(std::memory_order_relaxed) != 0;
}

void ProcessFastNetMessage(NetMessageDirection direction, int32_t endpointSlot, int32_t msgId,
                           const CNetMessage *message, uint64_t recipientMask) {
    const bool probeEnabled = ProbeLogEnabled();
    if (!probeEnabled && !HasAnyInterest())
        return;

    if (probeEnabled) {
        if (direction == NetMessageDirection::Incoming)
            g_incomingSeen.fetch_add(1, std::memory_order_relaxed);
        else
            g_outgoingSeen.fetch_add(1, std::memory_order_relaxed);
    }

    const bool hasMessageSerialized = HasNetMessageSerializedInterest(direction, msgId);
    const bool hasMessageFast = HasNetMessageFastInterest(direction, msgId);

    bool extracted = false;
    bool hasUserMessageSerialized = false;
    bool hasUserMessageFast = false;
    FastNetMessageNative fields{};

    if (direction == NetMessageDirection::Outgoing &&
        (HasAnyUserMessageSerializedInterest() || HasAnyUserMessageFastInterest())) {
        if (msgId == svc_UserMessage) {
            fields = ExtractFastFields(direction, msgId, message);
            extracted = true;
            hasUserMessageSerialized = fields.hasUserMessageType && HasUserMessageSerializedInterest(fields.userMessageType);
            hasUserMessageFast = fields.hasUserMessageType && HasUserMessageFastInterest(fields.userMessageType);
        } else {
            hasUserMessageSerialized = HasUserMessageSerializedInterest(msgId);
            hasUserMessageFast = HasUserMessageFastInterest(msgId);
            if (hasUserMessageSerialized || hasUserMessageFast) {
                fields.hasUserMessageType = 1;
                fields.userMessageType = msgId;
            }
        }
    }

    const bool hasSerialized = hasMessageSerialized || hasUserMessageSerialized;
    const bool hasFast = hasMessageFast || hasUserMessageFast;

    if (!hasSerialized && !hasFast) {
        if (probeEnabled) {
            if (direction == NetMessageDirection::Incoming)
                g_noInterestIncoming.fetch_add(1, std::memory_order_relaxed);
            else
                g_noInterestOutgoing.fetch_add(1, std::memory_order_relaxed);
        }
        return;
    }

    if (probeEnabled && hasSerialized) {
        if (direction == NetMessageDirection::Incoming)
            g_serializedIncomingHits.fetch_add(1, std::memory_order_relaxed);
        else
            g_serializedOutgoingHits.fetch_add(1, std::memory_order_relaxed);
    }

    if (!hasFast || !g_Deadworks.HasFastNetMessageCallback())
        return;

    if (!extracted)
        fields = ExtractFastFields(direction, msgId, message);

    g_Deadworks.OnFastNetMessage(static_cast<int32_t>(direction), endpointSlot, msgId, recipientMask,
                                 fields.userMessageType, fields.hasUserMessageType,
                                 fields.pauseType, fields.pauseGroup, fields.hasPauseRequest,
                                 fields.paused, fields.hasPauseState);

    if (probeEnabled) {
        if (direction == NetMessageDirection::Incoming)
            g_fastIncomingCallbacks.fetch_add(1, std::memory_order_relaxed);
        else
            g_fastOutgoingCallbacks.fetch_add(1, std::memory_order_relaxed);
        if (hasUserMessageFast)
            g_fastUserMessageCallbacks.fetch_add(1, std::memory_order_relaxed);
    }
}

void LogNetMessageVisitorStatsIfDue() {
    if (!ProbeLogEnabled())
        return;

    auto now = std::chrono::steady_clock::now();
    if (g_lastProbeLog.time_since_epoch().count() != 0 &&
        std::chrono::duration_cast<std::chrono::milliseconds>(now - g_lastProbeLog).count() < ProbeWindowMs()) {
        return;
    }
    g_lastProbeLog = now;

    g_Log->Info("[NetMessageVisitor] incomingSeen={} outgoingSeen={} serializedIncomingHits={} serializedOutgoingHits={} fastIncomingCallbacks={} fastOutgoingCallbacks={} fastUserMessageCallbacks={} noInterestIncoming={} noInterestOutgoing={} directExceptions={}",
                g_incomingSeen.load(std::memory_order_relaxed),
                g_outgoingSeen.load(std::memory_order_relaxed),
                g_serializedIncomingHits.load(std::memory_order_relaxed),
                g_serializedOutgoingHits.load(std::memory_order_relaxed),
                g_fastIncomingCallbacks.load(std::memory_order_relaxed),
                g_fastOutgoingCallbacks.load(std::memory_order_relaxed),
                g_fastUserMessageCallbacks.load(std::memory_order_relaxed),
                g_noInterestIncoming.load(std::memory_order_relaxed),
                g_noInterestOutgoing.load(std::memory_order_relaxed),
                g_directExceptions.load(std::memory_order_relaxed));
}

} // namespace deadworks
