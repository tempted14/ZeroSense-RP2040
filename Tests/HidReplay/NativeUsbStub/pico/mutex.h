#pragma once
#include <cstdint>
struct mutex_t { bool busy = false; unsigned tries = 0, exits = 0; };
inline bool mutex_try_enter(mutex_t* mutex, uint32_t*) {
    ++mutex->tries;
    if (mutex->busy) return false;
    mutex->busy = true;
    return true;
}
inline void mutex_exit(mutex_t* mutex) { ++mutex->exits; mutex->busy = false; }
