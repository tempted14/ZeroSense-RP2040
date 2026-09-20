#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace ZeroSenseMotion {

static constexpr float ReferenceIntervalUs = 8000.0f;

inline float intervalScale(uint32_t intervalUs) {
    return std::clamp(
        static_cast<float>(intervalUs) / ReferenceIntervalUs,
        0.25f,
        4.0f);
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
