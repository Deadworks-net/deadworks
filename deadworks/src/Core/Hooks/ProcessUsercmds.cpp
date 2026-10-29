#include "ProcessUsercmds.hpp"

#include "../Deadworks.hpp"
#include "../../SDK/CBaseEntity.hpp"

#include <google/protobuf/message_lite.h>

namespace deadworks {
namespace hooks {

// CUserCmd layout (from IDA reverse engineering):
// 0x00: vtable ptr (CUserCmdBase/CUserCmd)
// 0x08: int (command tick/state)
// 0x10: CCitadelUserCmdPB (MessageLite-derived, inherits through CUserCmdBaseHost)
// 0x70: CInButtonState (vtable, then three uint64 masks: held, changed this command, scroll)
// Total stride: 0xA8 (168 bytes)
static constexpr size_t kCUserCmdStride = 0xA8;
static constexpr size_t kProtobufOffset = 0x10;
static constexpr size_t kButtonStatesOffset = 0x78;

struct ButtonStates {
    uint64_t state[3] = {};
    bool operator==(const ButtonStates &) const = default;
};

static bool ReadVarint(const uint8_t *&at, const uint8_t *end, uint64_t &value) {
    value = 0;
    for (int shift = 0; at < end && shift < 64; shift += 7) {
        const uint8_t byte = *at++;
        value |= static_cast<uint64_t>(byte & 0x7F) << shift;
        if (!(byte & 0x80)) return true;
    }
    return false;
}

// Walks one level of a serialized protobuf message. For a length-delimited field numbered `wanted` it returns
// that field's bytes; varint fields 1-3 are collected into `varints` when given.
static bool ScanMessage(const uint8_t *at, const uint8_t *end, uint32_t wanted, const uint8_t **outData, const uint8_t **outEnd,
                        ButtonStates *varints) {
    bool found = false;
    while (at < end) {
        uint64_t key, value;
        if (!ReadVarint(at, end, key)) return false;
        const uint32_t field = static_cast<uint32_t>(key >> 3);
        switch (key & 7) {
        case 0:
            if (!ReadVarint(at, end, value)) return false;
            if (varints && field >= 1 && field <= 3) varints->state[field - 1] = value;
            break;
        case 1: at += 8; break;
        case 5: at += 4; break;
        case 2:
            if (!ReadVarint(at, end, value) || value > static_cast<uint64_t>(end - at)) return false;
            if (field == wanted && outData) {
                *outData = at;
                *outEnd = at + value;
                found = true;
            }
            at += value;
            break;
        default: return false;
        }
    }
    return found || varints;
}

// CCitadelUserCmdPB.base (1) -> CBaseUserCmdPB.buttons_pb (3) -> buttonstate1..3, read straight off the wire:
// the core has no generated classes for these messages. Missing fields read as zero, as protobuf defines.
static ButtonStates ReadButtons(const uint8_t *data, int length) {
    ButtonStates buttons;
    const uint8_t *base, *baseEnd, *pb, *pbEnd;
    if (ScanMessage(data, data + length, 1, &base, &baseEnd, nullptr) && ScanMessage(base, baseEnd, 3, &pb, &pbEnd, nullptr))
        ScanMessage(pb, pbEnd, 0, nullptr, nullptr, &buttons);
    return buttons;
}

void *__fastcall Hook_ProcessUsercmds(void *pController, void *cmds, int numcmds, unsigned char paused, float margin) {
    auto *entity = static_cast<CBaseEntity *>(reinterpret_cast<CEntityInstance *>(pController));
    int slot = entity->GetRefEHandle().GetEntryIndex() - 1;

    if (slot >= 0 && slot < 64 && numcmds > 0) {
        // Build a buffer with all serialized usercmds: [len1][bytes1][len2][bytes2]...
        static thread_local std::vector<uint8_t> batchBuf;
        batchBuf.clear();

        // Track which native cmd indices had valid protos (for deserialization back)
        static thread_local std::vector<int> validCmdIndices;
        validCmdIndices.clear();

        // The buttons each usercmd arrived with, to tell whether a plugin changed them.
        static thread_local std::vector<ButtonStates> originalButtons;
        originalButtons.clear();

        for (int i = 0; i < numcmds; i++) {
            auto *cmdBase = reinterpret_cast<char *>(cmds) + (i * kCUserCmdStride);
            auto *pb = reinterpret_cast<google::protobuf::MessageLite *>(cmdBase + kProtobufOffset);

            // A bot in simple bot mode sends a blank usercmd, which serializes to nothing. It is passed on
            // as a zero-length entry so plugins can fill it in; that is how a bot is driven with input.
            int size = static_cast<int>(pb->ByteSizeLong());
            if (size < 0)
                continue;

            // Append length prefix (4 bytes, little-endian)
            size_t offset = batchBuf.size();
            batchBuf.resize(offset + 4 + size);
            memcpy(batchBuf.data() + offset, &size, 4);

            if (size > 0 && !pb->SerializeToArray(batchBuf.data() + offset + 4, size)) {
                batchBuf.resize(offset); // rollback on failure
                continue;
            }

            validCmdIndices.push_back(i);
            originalButtons.push_back(ReadButtons(batchBuf.data() + offset + 4, size));
        }

        if (!validCmdIndices.empty()) {
            static thread_local uint8_t outBuf[65536];
            int outLen = 0;

            g_Deadworks.OnPre_ProcessUsercmds(slot, batchBuf.data(),
                                               static_cast<int>(batchBuf.size()),
                                               static_cast<int>(validCmdIndices.size()),
                                               paused != 0, margin,
                                               outBuf, &outLen);

            // If managed code returned modified bytes, deserialize back into native protos
            if (outLen > 0) {
                int offset = 0;
                for (size_t idx = 0; idx < validCmdIndices.size() && offset + 4 <= outLen; idx++) {
                    int len;
                    memcpy(&len, outBuf + offset, 4);
                    offset += 4;

                    if (len < 0 || offset + len > outLen)
                        break;

                    auto *cmdBase = reinterpret_cast<char *>(cmds) + (validCmdIndices[idx] * kCUserCmdStride);
                    auto *pb = reinterpret_cast<google::protobuf::MessageLite *>(cmdBase + kProtobufOffset);
                    pb->ParseFromArray(outBuf + offset, len);

                    // Movement and abilities read the usercmd's own CInButtonState, which the engine fills
                    // from buttons_pb when the command arrives off the network (or, for a bot, when its AI
                    // builds it). A plugin's change to buttons_pb only counts once it is copied there too.
                    const ButtonStates buttons = ReadButtons(outBuf + offset, len);
                    if (buttons != originalButtons[idx])
                        memcpy(cmdBase + kButtonStatesOffset, buttons.state, sizeof(buttons.state));

                    offset += len;
                }
            }
        }
    }

    return g_ProcessUsercmds.thiscall<void *>(pController, cmds, numcmds, paused, margin);
}

} // namespace hooks
} // namespace deadworks
