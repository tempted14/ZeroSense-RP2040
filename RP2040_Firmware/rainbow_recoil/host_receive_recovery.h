#pragma once

#include <cstdint>
#include <limits>

namespace ZeroSenseHostRecovery {

struct QueueState {
    uint8_t consecutiveFailures = 0;
    bool recoveryPending = false;
    uint32_t lastAttemptAtMs = 0;
};

enum class QueueOutcome : uint8_t {
    Queued,
    Recovered,
    Retry,
    Exhausted
};

inline bool retryDue(const QueueState& state, uint32_t nowMs, uint8_t maximumFailures) {
    if (state.consecutiveFailures == 0) return true;
    const uint32_t backoffMs = state.consecutiveFailures >= maximumFailures ? 100U : 5U;
    return static_cast<uint32_t>(nowMs - state.lastAttemptAtMs) >= backoffMs;
}

inline QueueOutcome recordQueueAttempt(
    QueueState& state,
    bool queued,
    uint8_t maximumFailures,
    uint32_t nowMs = 0) {
    if (queued) {
        const bool recovered = state.recoveryPending;
        state = {};
        return recovered ? QueueOutcome::Recovered : QueueOutcome::Queued;
    }

    state.recoveryPending = true;
    state.lastAttemptAtMs = nowMs;
    if (state.consecutiveFailures < std::numeric_limits<uint8_t>::max()) {
        ++state.consecutiveFailures;
    }
    return state.consecutiveFailures >= maximumFailures
        ? QueueOutcome::Exhausted
        : QueueOutcome::Retry;
}

} // namespace ZeroSenseHostRecovery
