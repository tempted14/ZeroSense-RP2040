#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace ZeroSenseRapidFire {

static constexpr uint32_t ButtonHoldUs = 8000;
static constexpr float IntervalVariance = 0.08f;

// RNG input is centered in [-0.5, 0.5]. At 960 RPM this gives 57.5-67.5 ms
// between click starts, or approximately 14.8-17.4 CPS. This is independent
// of General-mode movement variance and never alters automatic patterns.
inline uint32_t variedInterval(uint32_t nominalUs, float centeredRandom) {
    const float variation = std::clamp(centeredRandom, -0.5f, 0.5f) *
        2.0f * IntervalVariance;
    return std::max<uint32_t>(ButtonHoldUs * 2,
        static_cast<uint32_t>(std::round(static_cast<float>(nominalUs) * (1.0f + variation))));
}

enum class Action { None, Press, Release };

struct Deadlines { uint32_t releaseAt; uint32_t nextPressAt; };

inline Deadlines afterPressSubmitted(uint32_t now, uint32_t intervalUs) {
    return {now + ButtonHoldUs, now + intervalUs};
}

inline Action nextAction(bool buttonDown, bool transitionPending,
    uint32_t now, uint32_t releaseAt, uint32_t nextPressAt) {
    // Each down/up transition must reach the HID endpoint before its opposite
    // is generated. A busy endpoint must not collapse a release and new press.
    if (transitionPending) return Action::None;
    if (buttonDown) {
        return static_cast<int32_t>(now - releaseAt) >= 0 ? Action::Release : Action::None;
    }
    return static_cast<int32_t>(now - nextPressAt) >= 0 ? Action::Press : Action::None;
}

} // namespace ZeroSenseRapidFire
