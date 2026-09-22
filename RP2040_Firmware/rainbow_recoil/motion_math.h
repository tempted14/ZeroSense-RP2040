#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace ZeroSenseMotion {

// General-mode smoothing limits are defined at reference sensitivity.
// Apply the same sensitivity factor to their velocity and acceleration so
// calibration (including the scoped optic boost) cannot be lost at the cap.
inline float calibratedLimit(float referenceLimit, float sensitivityFactor) {
    return referenceLimit * sensitivityFactor;
}

static constexpr float ReferenceIntervalUs = 8000.0f;

inline float intervalScale(uint32_t intervalUs) {
    return std::clamp(
        static_cast<float>(intervalUs) / ReferenceIntervalUs,
        0.25f,
        4.0f);
}

// Converts a velocity expressed in counts per reference 8 ms into the total
// displacement for one complete shot interval. Unlike smoothing integration,
// this must not clamp long semi-automatic intervals.
inline float shotIntervalScale(uint32_t intervalUs) {
    return static_cast<float>(std::max<uint32_t>(intervalUs, 1U)) /
        ReferenceIntervalUs;
}

inline float approach(float current, float target, float maximumStep) {
    if (current < target) {
        return std::min(current + maximumStep, target);
    }
    if (current > target) {
        return std::max(current - maximumStep, target);
    }
    return current;
}

inline float frictionForInterval(float friction, float scale) {
    return std::pow(std::clamp(friction, 0.0f, 1.0f), scale);
}

}  // namespace ZeroSenseMotion
