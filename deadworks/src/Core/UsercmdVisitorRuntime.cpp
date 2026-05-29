#include "UsercmdVisitorRuntime.hpp"

#include "Deadworks.hpp"
#include "ManagedCallbacks.hpp"

#include <google/protobuf/message_lite.h>

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <string_view>

namespace deadworks {
namespace hooks {

namespace {

// CUserCmd layout observed from Deadlock dedicated-server ProcessUsercmds input:
// 0x00: vtable ptr (CUserCmdBase/CUserCmd)
// 0x08: command tick/state
// 0x10: embedded CCitadelUserCmdPB (MessageLite-derived through CUserCmdBaseHost)
// Total stride: 0xA8 (168 bytes)
constexpr size_t kCUserCmdStride = 0xA8;
constexpr size_t kProtobufOffset = 0x10;

// Direct-read offsets in the generated protobuf object layout used by the server build.
// Reads are has-bit and pointer guarded, wrapped in SEH, and treated as read-only telemetry.
constexpr size_t kProtoHasBitsOffset = 0x10;
constexpr size_t kCitadelBasePtrOffset = 0x38;
constexpr uint32_t kCitadelHasBaseMask = 0x00000001u;
constexpr size_t kBaseButtonsPtrOffset = 0x38;
constexpr size_t kBaseViewAnglesPtrOffset = 0x40;
constexpr size_t kBaseClientTickOffset = 0x54;
constexpr size_t kBaseForwardMoveOffset = 0x58;
constexpr size_t kBaseLeftMoveOffset = 0x5C;
constexpr uint32_t kBaseHasButtonsMask = 0x00000002u;
constexpr uint32_t kBaseHasViewAnglesMask = 0x00000004u;
constexpr size_t kButtonsState1Offset = 0x18;
constexpr size_t kQAnglePitchOffset = 0x18;
constexpr size_t kQAngleYawOffset = 0x1C;
constexpr size_t kQAngleRollOffset = 0x20;

static_assert(sizeof(FastUsercmdNative) == 48, "FastUsercmdNative ABI must match managed FastUsercmd");

static bool TextEquals(std::string_view left, std::string_view right) {
    return left == right;
}

static bool EnvEnabled(const char *name) {
    const char *value = std::getenv(name);
    if (!value || !*value)
        return false;
    return std::strcmp(value, "0") != 0 && _stricmp(value, "false") != 0 && _stricmp(value, "off") != 0;
}

static uint64_t EnvUint64(const char *name, uint64_t fallback, int base = 10) {
    const char *value = std::getenv(name);
    if (!value || !*value)
        return fallback;
    char *end = nullptr;
    auto parsed = std::strtoull(value, &end, base);
    return end == value ? fallback : parsed;
}

static UsercmdNativeMode ParseUsercmdMode(const char *value) {
    if (!value || !*value)
        return UsercmdNativeMode::MountedPolicy;

    std::string_view mode(value);
    if (TextEquals(mode, "off") || TextEquals(mode, "disabled") || TextEquals(mode, "0"))
        return UsercmdNativeMode::Off;
    if (TextEquals(mode, "count") || TextEquals(mode, "counter") || TextEquals(mode, "count-only") || TextEquals(mode, "1"))
        return UsercmdNativeMode::CountOnly;
    if (TextEquals(mode, "direct") || TextEquals(mode, "sample") || TextEquals(mode, "direct-sample") || TextEquals(mode, "2") || TextEquals(mode, "3"))
        return UsercmdNativeMode::DirectSample;
    if (TextEquals(mode, "fast") || TextEquals(mode, "typed") || TextEquals(mode, "fast-read") || TextEquals(mode, "direct-dispatch") || TextEquals(mode, "4"))
        return UsercmdNativeMode::FastDispatch;
    if (TextEquals(mode, "mounted") || TextEquals(mode, "policy") || TextEquals(mode, "auto") || TextEquals(mode, "5"))
        return UsercmdNativeMode::MountedPolicy;
    if (TextEquals(mode, "serialize") || TextEquals(mode, "protobuf") || TextEquals(mode, "managed") || TextEquals(mode, "default-deadworks"))
        return UsercmdNativeMode::SerializeManaged;

    return UsercmdNativeMode::MountedPolicy;
}

static uint32_t ParseMountMaskEnv() {
    const char *value = std::getenv("DEADWORKS_USERCMD_MOUNT_MASK");
    if (!value || !*value)
        value = std::getenv("DEADWORKS_USERCMD_MOUNTS");
    if (!value || !*value)
        return UsercmdMountNone;

    std::string_view text(value);
    if (TextEquals(text, "all") || TextEquals(text, "ALL"))
        return UsercmdMountAll;

    uint32_t mask = UsercmdMountNone;
    if (text.find("full") != std::string_view::npos || text.find("protobuf") != std::string_view::npos)
        mask |= UsercmdMountFullProtobuf;
    if (text.find("fast") != std::string_view::npos || text.find("typed") != std::string_view::npos)
        mask |= UsercmdMountFastRead;
    if (text.find("trigger") != std::string_view::npos || text.find("button") != std::string_view::npos)
        mask |= UsercmdMountButtonTriggers;
    if (mask != UsercmdMountNone)
        return mask;

    return static_cast<uint32_t>(std::strtoul(value, nullptr, 0)) & UsercmdMountAll;
}

static uint64_t ParseButtonTriggerMaskEnv() {
    const char *value = std::getenv("DEADWORKS_USERCMD_BUTTON_TRIGGER_MASK");
    if (!value || !*value)
        return 0;
    if (_stricmp(value, "all") == 0)
        return UINT64_MAX;
    return static_cast<uint64_t>(std::strtoull(value, nullptr, 0));
}

static uint32_t ParseFieldMaskEnv() {
    const char *value = std::getenv("DEADWORKS_USERCMD_FIELD_MASK");
    if (!value || !*value)
        value = std::getenv("DEADWORKS_USERCMD_FIELDS");
    if (!value || !*value)
        return UsercmdFieldAll;

    std::string_view text(value);
    if (TextEquals(text, "all") || TextEquals(text, "ALL"))
        return UsercmdFieldAll;

    uint32_t mask = UsercmdFieldNone;
    if (text.find("tick") != std::string_view::npos)
        mask |= UsercmdFieldClientTick;
    if (text.find("button") != std::string_view::npos)
        mask |= UsercmdFieldButtons;
    if (text.find("view") != std::string_view::npos || text.find("angle") != std::string_view::npos)
        mask |= UsercmdFieldViewAngles;
    if (text.find("move") != std::string_view::npos || text.find("movement") != std::string_view::npos)
        mask |= UsercmdFieldMovement;
    if (mask != UsercmdFieldNone)
        return mask;

    return static_cast<uint32_t>(std::strtoul(value, nullptr, 0)) & UsercmdFieldAll;
}

static bool ShouldLog() {
    return EnvEnabled("DEADWORKS_USERCMD_PROBE_LOG") || EnvEnabled("DEADWORKS_USERCMD_PROBE_LOGGING");
}

static uint64_t LogWindowMs() {
    return EnvUint64("DEADWORKS_USERCMD_PROBE_LOG_WINDOW_MS", EnvUint64("DEADWORKS_USERCMD_PROBE_LOG_MS", 5000));
}

static std::atomic<int32_t> g_ModeOverride{static_cast<int32_t>(UsercmdNativeMode::DefaultFromEnvironment)};
static std::atomic<uint32_t> g_MountMask{ParseMountMaskEnv()};
static std::atomic<uint64_t> g_ButtonTriggerMask{ParseButtonTriggerMaskEnv()};
static std::atomic<uint32_t> g_FieldMask{ParseFieldMaskEnv()};
static uint64_t g_LastButtonsBySlot[64]{};
static LARGE_INTEGER g_PerfFrequency{};

struct UsercmdCounters {
    uint64_t batches = 0;
    uint64_t cmds = 0;
    uint64_t serializedCmds = 0;
    uint64_t serializedBytes = 0;
    uint64_t serializeTicks = 0;
    uint64_t managedTicks = 0;
    uint64_t directCmds = 0;
    uint64_t directBase = 0;
    uint64_t directButtons = 0;
    uint64_t directAngles = 0;
    uint64_t directExceptions = 0;
    uint64_t directTicks = 0;
    uint64_t directReadOps = 0;
    uint64_t serializeOps = 0;
    uint64_t parseOps = 0;
    uint64_t managedCallbacks = 0;
    uint64_t triggerCallbacks = 0;
    int32_t lastClientTick = 0;
    uint64_t lastButtons = 0;
    float lastPitch = 0.0f;
    float lastYaw = 0.0f;
    float lastForward = 0.0f;
    float lastLeft = 0.0f;
    uint64_t lastLogMs = 0;
};

static UsercmdCounters g_Counters[64]{};

static uint64_t QpcNow() {
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return static_cast<uint64_t>(value.QuadPart);
}

static uint64_t QpcToMicros(uint64_t ticks) {
    if (!g_PerfFrequency.QuadPart)
        QueryPerformanceFrequency(&g_PerfFrequency);
    return static_cast<uint64_t>((ticks * 1000000ULL) / static_cast<uint64_t>(g_PerfFrequency.QuadPart));
}

struct DirectReadResult {
    int directCmds = 0;
    int base = 0;
    int buttons = 0;
    int angles = 0;
    int exceptions = 0;
    uint64_t ticks = 0;
    uint64_t readOps = 0;
    int32_t lastClientTick = 0;
    uint64_t lastButtons = 0;
    float lastPitch = 0.0f;
    float lastYaw = 0.0f;
    float lastForward = 0.0f;
    float lastLeft = 0.0f;
};

static UsercmdNativeMode EffectiveMode() {
    int32_t overrideMode = g_ModeOverride.load(std::memory_order_relaxed);
    if (overrideMode >= 0)
        return static_cast<UsercmdNativeMode>(overrideMode);

    static UsercmdNativeMode mode = [] {
        if (const char *disabled = std::getenv("DEADWORKS_DISABLE_USERCMDS"); disabled && std::string_view(disabled) == "1")
            return UsercmdNativeMode::Off;
        return ParseUsercmdMode(std::getenv("DEADWORKS_USERCMD_NATIVE_MODE"));
    }();
    return mode;
}

static DirectReadResult ExtractFastFields(void *cmds, int numcmds, uint32_t requestedFields, std::vector<FastUsercmdNative> *out) {
    DirectReadResult result{};
    requestedFields &= UsercmdFieldAll;
    if (requestedFields == UsercmdFieldNone)
        requestedFields = UsercmdFieldAll;
    if (out) {
        out->clear();
        out->reserve(static_cast<size_t>(numcmds));
    }

    uint64_t start = QpcNow();
    for (int i = 0; i < numcmds; i++) {
        FastUsercmdNative item{};
        item.commandIndex = i;

        __try {
            auto *cmdBase = reinterpret_cast<uint8_t *>(cmds) + (i * kCUserCmdStride);
            auto *citadel = cmdBase + kProtobufOffset;
            uint32_t citadelHas = *reinterpret_cast<uint32_t *>(citadel + kProtoHasBitsOffset);
            result.readOps++;
            result.directCmds++;

            if (citadelHas & kCitadelHasBaseMask) {
                auto *base = *reinterpret_cast<uint8_t **>(citadel + kCitadelBasePtrOffset);
                result.readOps++;
                if (base) {
                    item.hasBase = 1;
                    result.base++;

                    uint32_t baseHas = *reinterpret_cast<uint32_t *>(base + kProtoHasBitsOffset);
                    result.readOps++;

                    if (requestedFields & UsercmdFieldClientTick) {
                        item.clientTick = *reinterpret_cast<int32_t *>(base + kBaseClientTickOffset);
                        result.readOps++;
                    }

                    if (requestedFields & UsercmdFieldMovement) {
                        item.forwardMove = *reinterpret_cast<float *>(base + kBaseForwardMoveOffset);
                        item.leftMove = *reinterpret_cast<float *>(base + kBaseLeftMoveOffset);
                        result.readOps += 2;
                    }

                    if ((requestedFields & UsercmdFieldButtons) && (baseHas & kBaseHasButtonsMask)) {
                        auto *buttons = *reinterpret_cast<uint8_t **>(base + kBaseButtonsPtrOffset);
                        result.readOps++;
                        if (buttons) {
                            item.hasButtons = 1;
                            item.buttons = *reinterpret_cast<uint64_t *>(buttons + kButtonsState1Offset);
                            result.readOps++;
                            result.buttons++;
                        }
                    }

                    if ((requestedFields & UsercmdFieldViewAngles) && (baseHas & kBaseHasViewAnglesMask)) {
                        auto *angles = *reinterpret_cast<uint8_t **>(base + kBaseViewAnglesPtrOffset);
                        result.readOps++;
                        if (angles) {
                            item.hasViewAngles = 1;
                            item.pitch = *reinterpret_cast<float *>(angles + kQAnglePitchOffset);
                            item.yaw = *reinterpret_cast<float *>(angles + kQAngleYawOffset);
                            item.roll = *reinterpret_cast<float *>(angles + kQAngleRollOffset);
                            result.readOps += 3;
                            result.angles++;
                        }
                    }
                }
            }

            result.lastClientTick = item.clientTick;
            result.lastButtons = item.buttons;
            result.lastPitch = item.pitch;
            result.lastYaw = item.yaw;
            result.lastForward = item.forwardMove;
            result.lastLeft = item.leftMove;
            if (out)
                out->push_back(item);
        } __except (EXCEPTION_EXECUTE_HANDLER) {
            result.exceptions++;
        }
    }

    result.ticks = QpcNow() - start;
    return result;
}

struct FullDispatchResult {
    int serializedCmds = 0;
    int serializedBytes = 0;
    uint64_t serializeTicks = 0;
    uint64_t managedTicks = 0;
    uint64_t serializeOps = 0;
    uint64_t parseOps = 0;
    uint64_t managedCallbacks = 0;
};

static FullDispatchResult DispatchFullProtobuf(int slot, void *cmds, int numcmds, unsigned char paused, float margin) {
    FullDispatchResult result{};
    uint64_t serializeStart = QpcNow();

    static thread_local std::vector<uint8_t> batchBuf;
    batchBuf.clear();

    static thread_local std::vector<int> validCmdIndices;
    validCmdIndices.clear();

    for (int i = 0; i < numcmds; i++) {
        auto *cmdBase = reinterpret_cast<char *>(cmds) + (i * kCUserCmdStride);
        auto *pb = reinterpret_cast<google::protobuf::MessageLite *>(cmdBase + kProtobufOffset);

        result.serializeOps++;
        int size = static_cast<int>(pb->ByteSizeLong());
        if (size <= 0)
            continue;

        size_t offset = batchBuf.size();
        batchBuf.resize(offset + 4 + size);
        std::memcpy(batchBuf.data() + offset, &size, 4);

        result.serializeOps++;
        if (!pb->SerializeToArray(batchBuf.data() + offset + 4, size)) {
            batchBuf.resize(offset);
            continue;
        }

        validCmdIndices.push_back(i);
    }

    result.serializeTicks = QpcNow() - serializeStart;
    result.serializedCmds = static_cast<int>(validCmdIndices.size());
    result.serializedBytes = static_cast<int>(batchBuf.size());

    if (!validCmdIndices.empty()) {
        static thread_local uint8_t outBuf[65536];
        int outLen = 0;

        uint64_t managedStart = QpcNow();
        g_Deadworks.OnPre_ProcessUsercmds(slot, batchBuf.data(),
                                           static_cast<int>(batchBuf.size()),
                                           static_cast<int>(validCmdIndices.size()),
                                           paused != 0, margin,
                                           outBuf, &outLen);
        result.managedTicks = QpcNow() - managedStart;
        result.managedCallbacks++;

        if (outLen > 0) {
            int offset = 0;
            for (size_t idx = 0; idx < validCmdIndices.size() && offset + 4 <= outLen; idx++) {
                int len = 0;
                std::memcpy(&len, outBuf + offset, 4);
                offset += 4;

                if (len <= 0 || offset + len > outLen)
                    break;

                auto *cmdBase = reinterpret_cast<char *>(cmds) + (validCmdIndices[idx] * kCUserCmdStride);
                auto *pb = reinterpret_cast<google::protobuf::MessageLite *>(cmdBase + kProtobufOffset);
                pb->ParseFromArray(outBuf + offset, len);
                result.parseOps++;

                offset += len;
            }
        }
    }

    return result;
}

static uint64_t DispatchButtonTriggers(int slot, const std::vector<FastUsercmdNative> &fastCmds, unsigned char paused, float margin) {
    uint64_t triggerMask = GetUsercmdButtonTriggerMask();
    if (slot < 0 || slot >= 64 || triggerMask == 0 || fastCmds.empty())
        return 0;

    uint64_t callbacks = 0;
    uint64_t lastButtons = g_LastButtonsBySlot[slot];
    for (const auto &cmd : fastCmds) {
        if (!cmd.hasButtons)
            continue;

        uint64_t pressed = cmd.buttons & ~lastButtons;
        uint64_t triggered = pressed & triggerMask;
        lastButtons = cmd.buttons;

        if (!triggered)
            continue;

        g_Deadworks.OnUsercmdTrigger(slot, &cmd, pressed, triggered, paused != 0, margin);
        callbacks++;
    }

    g_LastButtonsBySlot[slot] = lastButtons;
    return callbacks;
}

static void Accumulate(int slot, int numcmds, const FullDispatchResult &full,
                       const DirectReadResult *direct, uint64_t managedTicksOverride = 0,
                       uint64_t managedCallbacksOverride = 0, uint64_t triggerCallbacks = 0) {
    if (slot < 0 || slot >= 64)
        return;

    auto &c = g_Counters[slot];
    c.batches++;
    c.cmds += static_cast<uint64_t>(numcmds > 0 ? numcmds : 0);
    c.serializedCmds += static_cast<uint64_t>(full.serializedCmds > 0 ? full.serializedCmds : 0);
    c.serializedBytes += static_cast<uint64_t>(full.serializedBytes > 0 ? full.serializedBytes : 0);
    c.serializeTicks += full.serializeTicks;
    c.managedTicks += full.managedTicks + managedTicksOverride;
    c.serializeOps += full.serializeOps;
    c.parseOps += full.parseOps;
    c.managedCallbacks += full.managedCallbacks + managedCallbacksOverride;
    c.triggerCallbacks += triggerCallbacks;

    if (direct) {
        c.directCmds += static_cast<uint64_t>(direct->directCmds);
        c.directBase += static_cast<uint64_t>(direct->base);
        c.directButtons += static_cast<uint64_t>(direct->buttons);
        c.directAngles += static_cast<uint64_t>(direct->angles);
        c.directExceptions += static_cast<uint64_t>(direct->exceptions);
        c.directTicks += direct->ticks;
        c.directReadOps += direct->readOps;
        c.lastClientTick = direct->lastClientTick;
        c.lastButtons = direct->lastButtons;
        c.lastPitch = direct->lastPitch;
        c.lastYaw = direct->lastYaw;
        c.lastForward = direct->lastForward;
        c.lastLeft = direct->lastLeft;
    }

    if (!ShouldLog())
        return;

    uint64_t now = GetTickCount64();
    uint64_t windowMs = LogWindowMs();
    if (windowMs > 0 && now - c.lastLogMs >= windowMs) {
        c.lastLogMs = now;
        g_Log->Info("[UsercmdVisitor] slot={} batches={} cmds={} serializedCmds={} serializedBytes={} serializeUs={} managedUs={} mode={} mountMask=0x{:X} directCmds={} directBase={} directButtons={} directAngles={} directExceptions={} directUs={} directReadOps={} serializeOps={} parseOps={} managedCallbacks={} triggerCallbacks={} lastClientTick={} lastButtons=0x{:X} lastPitch={} lastYaw={} lastForward={} lastLeft={}",
                    slot, c.batches, c.cmds, c.serializedCmds, c.serializedBytes,
                    QpcToMicros(c.serializeTicks), QpcToMicros(c.managedTicks), static_cast<int>(EffectiveMode()), GetUsercmdMountMask(),
                    c.directCmds, c.directBase, c.directButtons, c.directAngles, c.directExceptions,
                    QpcToMicros(c.directTicks), c.directReadOps, c.serializeOps, c.parseOps,
                    c.managedCallbacks, c.triggerCallbacks,
                    c.lastClientTick, c.lastButtons, c.lastPitch, c.lastYaw, c.lastForward, c.lastLeft);
    }
}

} // namespace

void SetUsercmdNativeMode(int32_t mode) {
    if (mode < static_cast<int32_t>(UsercmdNativeMode::DefaultFromEnvironment) ||
        mode > static_cast<int32_t>(UsercmdNativeMode::MountedPolicy)) {
        mode = static_cast<int32_t>(UsercmdNativeMode::DefaultFromEnvironment);
    }
    g_ModeOverride.store(mode, std::memory_order_relaxed);
}

int32_t GetUsercmdNativeMode() {
    return static_cast<int32_t>(EffectiveMode());
}

void SetUsercmdMountMask(uint32_t mask) {
    g_MountMask.store(mask & UsercmdMountAll, std::memory_order_relaxed);
}

uint32_t GetUsercmdMountMask() {
    return g_MountMask.load(std::memory_order_relaxed);
}

void SetUsercmdButtonTriggerMask(uint64_t mask) {
    g_ButtonTriggerMask.store(mask, std::memory_order_relaxed);
}

uint64_t GetUsercmdButtonTriggerMask() {
    return g_ButtonTriggerMask.load(std::memory_order_relaxed);
}

void SetUsercmdFieldMask(uint32_t mask) {
    g_FieldMask.store(mask & UsercmdFieldAll, std::memory_order_relaxed);
}

uint32_t GetUsercmdFieldMask() {
    auto mask = g_FieldMask.load(std::memory_order_relaxed) & UsercmdFieldAll;
    return mask == UsercmdFieldNone ? UsercmdFieldAll : mask;
}

void ProcessUsercmdVisitors(int slot, void *cmds, int numcmds, unsigned char paused, float margin) {
    if (slot < 0 || slot >= 64 || numcmds <= 0)
        return;

    auto mode = EffectiveMode();
    FullDispatchResult emptyFull{};

    if (mode == UsercmdNativeMode::Off || mode == UsercmdNativeMode::CountOnly) {
        Accumulate(slot, numcmds, emptyFull, nullptr);
        return;
    }

    if (mode == UsercmdNativeMode::DirectSample) {
        auto direct = ExtractFastFields(cmds, numcmds, GetUsercmdFieldMask(), nullptr);
        Accumulate(slot, numcmds, emptyFull, &direct);
        return;
    }

    if (mode == UsercmdNativeMode::FastDispatch) {
        static thread_local std::vector<FastUsercmdNative> fastCmds;
        auto direct = ExtractFastFields(cmds, numcmds, GetUsercmdFieldMask(), &fastCmds);
        uint64_t managedTicks = 0;
        uint64_t managedCallbacks = 0;
        if (!fastCmds.empty()) {
            uint64_t managedStart = QpcNow();
            g_Deadworks.OnFast_ProcessUsercmds(slot, fastCmds.data(), static_cast<int>(fastCmds.size()), paused != 0, margin);
            managedTicks = QpcNow() - managedStart;
            managedCallbacks = 1;
        }
        Accumulate(slot, numcmds, emptyFull, &direct, managedTicks, managedCallbacks);
        return;
    }

    if (mode == UsercmdNativeMode::MountedPolicy) {
        uint32_t mountMask = GetUsercmdMountMask();
        if (mountMask == UsercmdMountNone) {
            Accumulate(slot, numcmds, emptyFull, nullptr);
            return;
        }

        DirectReadResult direct{};
        const DirectReadResult *directPtr = nullptr;
        uint64_t managedTicks = 0;
        uint64_t managedCallbacks = 0;
        uint64_t triggerCallbacks = 0;
        static thread_local std::vector<FastUsercmdNative> fastCmds;

        const bool needDirect = (mountMask & UsercmdMountFastRead) != 0 ||
                                ((mountMask & UsercmdMountButtonTriggers) != 0 && GetUsercmdButtonTriggerMask() != 0);
        if (needDirect) {
            uint32_t requestedFields = GetUsercmdFieldMask();
            if ((mountMask & UsercmdMountButtonTriggers) && GetUsercmdButtonTriggerMask() != 0)
                requestedFields |= UsercmdFieldButtons;
            direct = ExtractFastFields(cmds, numcmds, requestedFields, &fastCmds);
            directPtr = &direct;

            if ((mountMask & UsercmdMountButtonTriggers) && GetUsercmdButtonTriggerMask() != 0)
                triggerCallbacks = DispatchButtonTriggers(slot, fastCmds, paused, margin);

            if ((mountMask & UsercmdMountFastRead) && !fastCmds.empty()) {
                uint64_t managedStart = QpcNow();
                g_Deadworks.OnFast_ProcessUsercmds(slot, fastCmds.data(), static_cast<int>(fastCmds.size()), paused != 0, margin);
                managedTicks += QpcNow() - managedStart;
                managedCallbacks++;
            }
        }

        FullDispatchResult full{};
        if (mountMask & UsercmdMountFullProtobuf)
            full = DispatchFullProtobuf(slot, cmds, numcmds, paused, margin);

        Accumulate(slot, numcmds, full, directPtr, managedTicks, managedCallbacks, triggerCallbacks);
        return;
    }

    auto full = DispatchFullProtobuf(slot, cmds, numcmds, paused, margin);
    Accumulate(slot, numcmds, full, nullptr);
}

} // namespace hooks
} // namespace deadworks
