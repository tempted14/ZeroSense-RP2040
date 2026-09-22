#pragma once

#include <cstdint>

namespace ZeroSenseHostHealth {

inline bool stalled(uint32_t lastCompletedAtMs, uint32_t nowMs,
                    uint32_t timeoutMs) {
    return lastCompletedAtMs != 0 &&
        static_cast<uint32_t>(nowMs - lastCompletedAtMs) > timeoutMs;
}

} // namespace ZeroSenseHostHealth
