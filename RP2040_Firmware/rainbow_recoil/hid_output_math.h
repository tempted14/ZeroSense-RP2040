#pragma once

#include <algorithm>
#include <cstdint>
#include <limits>

namespace ZeroSenseHidOutput {

// Both board descriptors declare signed 8-bit relative X/Y with the HID
// logical range [-127, 127]. A clipped report leaves its remainder queued.
inline int8_t reportDelta(int64_t value) {
    return static_cast<int8_t>(std::clamp<int64_t>(value, -127, 127));
}

// An extreme General-mode setting can produce input faster than the 1 ms
// endpoint can drain it. Preserve the sign instead of wrapping the queue.
inline int32_t addPending(int32_t current, int32_t incoming) {
    return static_cast<int32_t>(std::clamp<int64_t>(
        static_cast<int64_t>(current) + incoming,
        std::numeric_limits<int32_t>::min(),
        std::numeric_limits<int32_t>::max()));
}

}  // namespace ZeroSenseHidOutput
