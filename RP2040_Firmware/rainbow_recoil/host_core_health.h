#pragma once

#include <cstdint>

namespace ZeroSenseHostHealth {

inline bool stalled(uint32_t lastCompletedAtMs, uint32_t nowMs,
                    uint32_t timeoutMs) {
    return lastCompletedAtMs != 0 &&
        static_cast<uint32_t>(nowMs - lastCompletedAtMs) > timeoutMs;
}

inline bool restartDue(uint32_t lastCompletedAtMs, uint32_t nowMs) {
    // A slow frame must never restart the board. Only a host task that has
    // made no progress for several seconds warrants a full USB-controller
    // reset; restarting only core 1 can strand PIO IRQ/DMA state.
    return stalled(lastCompletedAtMs, nowMs, 3000);
}

} // namespace ZeroSenseHostHealth
