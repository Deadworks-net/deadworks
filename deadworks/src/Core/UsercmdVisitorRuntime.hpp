#pragma once

#include <cstdint>

namespace deadworks {
namespace hooks {

enum class UsercmdNativeMode : int32_t {
    DefaultFromEnvironment = -1,
    SerializeManaged = 0,
    Off = 1,
    CountOnly = 2,
    DirectSample = 3,
    FastDispatch = 4,
    MountedPolicy = 5,
};

enum UsercmdMountFlags : uint32_t {
    UsercmdMountNone = 0,
    UsercmdMountFullProtobuf = 1u << 0,
    UsercmdMountFastRead = 1u << 1,
    UsercmdMountButtonTriggers = 1u << 2,
    UsercmdMountAll = UsercmdMountFullProtobuf | UsercmdMountFastRead | UsercmdMountButtonTriggers,
};

enum UsercmdFieldFlags : uint32_t {
    UsercmdFieldNone = 0,
    UsercmdFieldClientTick = 1u << 0,
    UsercmdFieldButtons = 1u << 1,
    UsercmdFieldViewAngles = 1u << 2,
    UsercmdFieldMovement = 1u << 3,
    UsercmdFieldAll = UsercmdFieldClientTick | UsercmdFieldButtons | UsercmdFieldViewAngles | UsercmdFieldMovement,
};

void ProcessUsercmdVisitors(int slot, void *cmds, int numcmds, unsigned char paused, float margin);

void SetUsercmdNativeMode(int32_t mode);
int32_t GetUsercmdNativeMode();

void SetUsercmdMountMask(uint32_t mask);
uint32_t GetUsercmdMountMask();

void SetUsercmdButtonTriggerMask(uint64_t mask);
uint64_t GetUsercmdButtonTriggerMask();

void SetUsercmdFieldMask(uint32_t mask);
uint32_t GetUsercmdFieldMask();

} // namespace hooks
} // namespace deadworks
