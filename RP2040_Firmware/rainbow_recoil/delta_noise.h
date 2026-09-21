#pragma once

#include <algorithm>
#include <cstdint>

namespace ZeroSenseDeltaNoise {

// Noise is tracked as an offset balance. A later report always repays the
// balance before a new random offset is introduced, preventing random-walk
// drift while retaining small per-report variation.
struct AxisState {
    int16_t balance = 0;
};

inline bool hasBalance(const AxisState& state) {
    return state.balance != 0;
}

inline int8_t preferredDelta(const AxisState& state, int8_t randomDelta) {
    if (state.balance > 0) {
        return static_cast<int8_t>(-std::min<int16_t>(state.balance, 3));
    }
    if (state.balance < 0) {
        return static_cast<int8_t>(std::min<int16_t>(-state.balance, 3));
    }
    return randomDelta;
}

inline void recordAppliedDelta(AxisState& state, int8_t appliedDelta) {
    state.balance = static_cast<int16_t>(state.balance + appliedDelta);
}

}  // namespace ZeroSenseDeltaNoise
