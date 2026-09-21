#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace ZeroSenseCorrection {

constexpr int64_t Q16 = 65536;
constexpr uint32_t FrameIntervalUs = 1000;
constexpr uint16_t MaximumFrames = 1000;

struct State {
    int64_t remainingXQ16 = 0;
    int64_t remainingYQ16 = 0;
    int64_t fractionXQ16 = 0;
    int64_t fractionYQ16 = 0;
    uint16_t frames = 0;
    uint32_t nextFrameAtUs = 0;
    uint32_t delayedFrames = 0;
    uint32_t maximumLatenessUs = 0;
};

struct ServiceResult {
    int32_t queuedX = 0;
    int32_t queuedY = 0;
    uint8_t servicedFrames = 0;
};

inline bool deadlineReached(uint32_t now, uint32_t deadline) {
    return static_cast<int32_t>(now - deadline) >= 0;
}

inline uint16_t framesForInterval(uint32_t intervalUs) {
    return static_cast<uint16_t>(std::min<uint32_t>(
        std::max<uint32_t>((intervalUs + FrameIntervalUs - 1U) / FrameIntervalUs, 1U),
        MaximumFrames));
}

inline void resetOutput(State& state) {
    state.remainingXQ16 = 0;
    state.remainingYQ16 = 0;
    state.fractionXQ16 = 0;
    state.fractionYQ16 = 0;
    state.frames = 0;
    state.nextFrameAtUs = 0;
}

inline void schedule(
    State& state,
    float scaledDx,
    float scaledDy,
    uint32_t shotIntervalUs,
    uint32_t now) {
    state.remainingXQ16 += static_cast<int64_t>(std::llround(scaledDx * Q16));
    state.remainingYQ16 += static_cast<int64_t>(std::llround(scaledDy * Q16));
    state.frames = std::max(state.frames, framesForInterval(shotIntervalUs));
    if (state.nextFrameAtUs == 0) {
        state.nextFrameAtUs = now;
    }
}

inline ServiceResult service(State& state, uint32_t now, uint8_t maximumFrames = 4) {
    ServiceResult result;
    while (state.frames > 0 && deadlineReached(now, state.nextFrameAtUs) &&
        result.servicedFrames < maximumFrames) {
        const uint32_t lateness = now - state.nextFrameAtUs;
        if (lateness >= FrameIntervalUs) {
            ++state.delayedFrames;
            state.maximumLatenessUs = std::max(state.maximumLatenessUs, lateness);
        }

        const int64_t stepX = state.remainingXQ16 / state.frames;
        const int64_t stepY = state.remainingYQ16 / state.frames;
        state.remainingXQ16 -= stepX;
        state.remainingYQ16 -= stepY;
        --state.frames;

        state.fractionXQ16 += stepX;
        state.fractionYQ16 += stepY;
        const int64_t queuedX = state.fractionXQ16 / Q16;
        const int64_t queuedY = state.fractionYQ16 / Q16;
        state.fractionXQ16 -= queuedX * Q16;
        state.fractionYQ16 -= queuedY * Q16;
        result.queuedX += static_cast<int32_t>(std::clamp<int64_t>(
            queuedX,
            INT32_MIN,
            INT32_MAX));
        result.queuedY += static_cast<int32_t>(std::clamp<int64_t>(
            queuedY,
            INT32_MIN,
            INT32_MAX));
        state.nextFrameAtUs += FrameIntervalUs;
        ++result.servicedFrames;
    }

    if (state.frames == 0) {
        state.nextFrameAtUs = 0;
    }
    return result;
}

inline int32_t roundedFraction(int64_t value) {
    return static_cast<int32_t>(value >= 0
        ? (value + Q16 / 2) / Q16
        : (value - Q16 / 2) / Q16);
}

}  // namespace ZeroSenseCorrection
