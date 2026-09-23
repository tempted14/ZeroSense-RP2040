#pragma once

namespace ZeroSenseFirstBullet {

inline float verticalForShot(float patternVertical, int shotIndex, float multiplier) {
    return shotIndex == 0 ? patternVertical * multiplier : patternVertical;
}

}
