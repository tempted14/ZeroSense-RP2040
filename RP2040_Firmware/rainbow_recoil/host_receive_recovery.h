#pragma once

#include <cstdint>
#include <limits>

namespace ZeroSenseHostRecovery {

struct QueueState {
    uint8_t consecutiveFailures = 0;
    bool recoveryPending = false;
};

enum class QueueOutcome : uint8_t {
    Queued,
    Recovered,
    Retry,
    Exhausted
};

inline QueueOutcome recordQueueAttempt(
    QueueState& state,
    bool queued,
    uint8_t maximumFailures) {
    if (queued) {
        const bool recovered = state.recoveryPending;
        state = {};
        return recovered ? QueueOutcome::Recovered : QueueOutcome::Queued;
    }

    state.recoveryPending = true;
    if (state.consecutiveFailures < std::numeric_limits<uint8_t>::max()) {
        ++state.consecutiveFailures;
    }
    return state.consecutiveFailures >= maximumFailures
        ? QueueOutcome::Exhausted
        : QueueOutcome::Retry;
}

} // namespace ZeroSenseHostRecovery
