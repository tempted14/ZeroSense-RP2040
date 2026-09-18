/**
 * Rainbow Recoil Control - TENSTAR/Waveshare RP2040-Zero
 * Timing variation, adaptive polling, delta shaping, and velocity clamping
 */

#include <Arduino.h>
#include <Adafruit_TinyUSB.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include "pico/bootrom.h"

#ifndef USE_TINYUSB
#error "Select Tools > USB Stack > Adafruit TinyUSB (or build with -DUSE_TINYUSB)."
#endif

enum CommandId : uint8_t {
    CMD_PING        = 0xF0,
    CMD_START       = 0xF1,
    CMD_STOP        = 0xF2,
    CMD_PROFILE     = 0xF3,
    CMD_SENSITIVITY = 0xF4,
    CMD_PATTERN     = 0xF5,
    CMD_RAPID_FIRE  = 0xF6,
    CMD_KEEPALIVE   = 0xF7,
    CMD_RESET       = 0xFF
};

static constexpr uint16_t maxCommandPayload = 63;
static constexpr uint16_t maxPatternPoints = 160;
// Mitigation: adaptive polling intervals (ms) - lower = higher rate
static constexpr uint32_t POLL_IDLE_MS   = 4;    // ~250 Hz during idle
static constexpr uint32_t POLL_NORMAL_MS = 2;    // ~500 Hz normal operation
static constexpr uint32_t POLL_HIGH_MS   = 1;    // ~900 Hz rapid fire / active movement

// Jitter configuration: break deterministic timing patterns
// Avoid an unnecessarily rigid movement cadence.
static constexpr float TIMING_JITTER_PCT = 8.0f;   // ±8% variance on intervals
static constexpr int16_t DELTA_NOISE_RANGE = 3;    // ±2 to ±3 pixels of noise floor

enum CompensationMode : uint8_t {
    MODE_GENERAL = 0,
    MODE_WEAPON_PATTERN = 1
};

// State variables
static bool fireActive = false;
static char activeProfileName[54] = "L85A2";
static float activeVerticalCompensation = 0.80f;
static float activeHorizontalCompensation = 0.0f;
static uint8_t activeBurstProgression = 0;
static CompensationMode activeMode = MODE_GENERAL;
static uint16_t activeRoundsPerMinute = 0;
static uint16_t expectedPatternPoints = 0;
static uint16_t loadedPatternPoints = 0;
static int16_t patternHorizontal[maxPatternPoints] = {};
static int16_t patternVertical[maxPatternPoints] = {};
static float horizontalSensitivityFactor = 1.0f;
static float verticalSensitivityFactor = 1.0f;
static float fractionalMouseX = 0.0f;
static float fractionalMouseY = 0.0f;

// Mitigation: velocity state for realistic acceleration profiles
typedef struct {
    float velocityX;        // Current horizontal velocity (pixels per update)
    float velocityY;        // Current vertical velocity (pixels per update)
    float acceleration;     // Maximum velocity change per update
    float maxVelocity;      // Maximum velocity per axis and update
    float friction;         // Deceleration when no input
    float microNoise;       // Maximum proportional sensor noise
    bool accelerating;      // Current movement phase state
} VelocitySimulator;

static VelocitySimulator sim = {
    0.0f,
    0.0f,
    1.2f,
    40.0f,
    0.85f,
    0.15f,
    false
};

static int shotsInBurst = 0;
static uint32_t nextMovementAtUs = 0;
static int32_t pendingMouseX = 0;
static int32_t pendingMouseY = 0;
static bool rapidFireEnabled = false;
static bool rapidFireActive = false;
static bool rapidButtonDown = false;
static bool hidStateDirty = false;
static uint16_t rapidFireRoundsPerMinute = 0;
static uint32_t nextRapidShotAtUs = 0;
static uint32_t lastHostKeepAliveAtMs = 0;

// Mitigation: additional global variables for adaptive polling and watchdog
static uint32_t lastHidReportAtUs = 0;
static const uint32_t hostWatchdogTimeoutMs = 750; // ms - matches original constant value
static uint32_t rapidButtonReleaseAtUs = 0;

static const uint8_t hidReportDescriptor[] = {
    TUD_HID_REPORT_DESC_MOUSE()
};

static Adafruit_USBD_HID usbHid;

enum class ParserState : uint8_t {
    LengthLow,
    LengthHigh,
    Command,
    Payload,
    DiscardCommand,
    DiscardPayload
};
static ParserState parserState = ParserState::LengthLow;
static uint16_t parserLength = 0;
static uint16_t parserOffset = 0;
static uint8_t parserCommand = 0;
static uint8_t parserPayload[maxCommandPayload + 1];

// Mitigation: RNG state with entropy seeding from multiple sources
// Uses millis() + random() combination for pseudo-random seed on first boot
static uint32_t rngState = 0x5DEECE66UL;  // Initial seed (fits in uint32_t)
static void rng_seed(void) {
    static bool seeded = false;
    if (!seeded) {
        // Combine millis timestamp with random() for entropy
        const uint32_t time_part = millis();
        const uint16_t rand_part = random(0, 65536);  // Arduino-Pico has random()
        rngState = (time_part << 16) | rand_part;
        seeded = true;
    }
}
static float rng_next(void) {
    rngState = rngState * 1664525UL + 1013904223UL;
    return (rngState / 4294967296.0f) - 0.5f; // Range: [-0.5, 0.5)
}

static float readFloatLittleEndian(const uint8_t* source) {
    float value = 0.0f;
    memcpy(&value, source, sizeof(value));
    return value;
}

static uint16_t readUint16LittleEndian(const uint8_t* source) {
    return static_cast<uint16_t>(source[0]) |
        (static_cast<uint16_t>(source[1]) << 8);
}

static int16_t readInt16LittleEndian(const uint8_t* source) {
    return static_cast<int16_t>(readUint16LittleEndian(source));
}

// Mitigation: jittered interval calculation (breaks perfect periodicity)
static uint32_t get_jittered_interval(uint32_t base_us) {
    const float jitter_scale = 1.0f +
        rng_next() * 2.0f * (TIMING_JITTER_PCT / 100.0f);
    const uint32_t interval = static_cast<uint32_t>(
        std::round(static_cast<float>(base_us) * jitter_scale));
    return std::max<uint32_t>(interval, 1U);
}

static void reset_movement_state() {
    shotsInBurst = 0;
    fractionalMouseX = 0.0f;
    fractionalMouseY = 0.0f;
    pendingMouseX = 0;
    pendingMouseY = 0;
    sim.velocityX = 0.0f;
    sim.velocityY = 0.0f;
    sim.accelerating = false;
}

static void set_rapid_button(bool pressed) {
    if (rapidButtonDown != pressed) {
        rapidButtonDown = pressed;
        hidStateDirty = true;
    }
}

static void stop_output() {
    fireActive = false;
    rapidFireActive = false;
    set_rapid_button(false);
    reset_movement_state();
}

static void reset_parser() {
    parserState = ParserState::LengthLow;
    parserLength = 0;
    parserOffset = 0;
    parserCommand = 0;
}

// Mitigation: apply profile payload with jitter-aware reset
static bool apply_profile_payload(const uint8_t* payload, uint16_t length) {
    if (length < 10 || (payload[0] != 1 && payload[0] != 2)) {
        Serial.println("ERROR:PROFILE:FORMAT");
        return false;
    }

    const bool is_version2 = payload[0] == 2;
    if (is_version2 && length < 14) {
        Serial.println("ERROR:PROFILE:FORMAT");
        return false;
    }

    const uint16_t verticalOffset = is_version2 ? 2 : 1;
    const uint16_t horizontalOffset = is_version2 ? 6 : 5;
    const uint16_t burstOffset = is_version2 ? 10 : 9;
    const uint16_t nameOffset = is_version2 ? 14 : 10;
    const float vertical = readFloatLittleEndian(payload + verticalOffset);
    const float horizontal = readFloatLittleEndian(payload + horizontalOffset);
    if (!std::isfinite(vertical) || !std::isfinite(horizontal)) {
        Serial.println("ERROR:PROFILE:VALUE");
        return false;
    }

    activeVerticalCompensation = std::clamp(vertical, 0.0f, 20.0f);
    activeHorizontalCompensation = std::clamp(horizontal, -20.0f, 20.0f);
    activeBurstProgression = std::min<uint8_t>(payload[burstOffset], 100);
    activeMode = MODE_GENERAL;
    activeRoundsPerMinute = 0;
    expectedPatternPoints = 0;
    loadedPatternPoints = 0;
    rapidFireEnabled = false;
    rapidFireActive = false;
    rapidFireRoundsPerMinute = 0;
    set_rapid_button(false);

    if (is_version2) {
        const uint8_t requestedMode = payload[1];
        const uint16_t requestedRpm = readUint16LittleEndian(payload + 11);
        const uint16_t requestedPoints = payload[13];
        if (requestedMode == MODE_WEAPON_PATTERN &&
            requestedRpm > 0 &&
            requestedPoints > 0 &&
            requestedPoints <= maxPatternPoints) {
            activeMode = MODE_WEAPON_PATTERN;
            activeRoundsPerMinute = requestedRpm;
            expectedPatternPoints = requestedPoints;
        }
    }

    const uint16_t sourceNameLength = length - nameOffset;
    const uint16_t nameLength = std::min<uint16_t>(sourceNameLength, sizeof(activeProfileName) - 1);
    if (nameLength > 0) {
        memcpy(activeProfileName, payload + nameOffset, nameLength);
        activeProfileName[nameLength] = '\0';
    } else {
        strcpy(activeProfileName, "Unnamed");
    }

    reset_movement_state();
    Serial.printf(
        "PROFILE:%s:MODE=%s:V=%.3f:H=%.3f:RPM=%u:POINTS=%u\n",
        activeProfileName,
        activeMode == MODE_WEAPON_PATTERN ? "PATTERN" : "GENERAL",
        activeVerticalCompensation,
        activeHorizontalCompensation,
        activeRoundsPerMinute,
        expectedPatternPoints);
    return true;
}

// Mitigation: apply pattern payload with validation
static bool apply_pattern_payload(const uint8_t* payload, uint16_t length) {
    if (length < 3 || payload[0] != 1) {
        Serial.println("ERROR:PATTERN:FORMAT");
        return false;
    }

    const uint16_t offset = payload[1];
    const uint16_t count = payload[2];
    if (count == 0 || length != 3 + count * 4 ||
        activeMode != MODE_WEAPON_PATTERN ||
        offset != loadedPatternPoints ||
        offset + count > expectedPatternPoints ||
        offset + count > maxPatternPoints) {
        Serial.println("ERROR:PATTERN:RANGE");
        return false;
    }

    for (uint16_t index = 0; index < count; ++index) {
        const uint16_t sourceOffset = 3 + index * 4;
        patternHorizontal[offset + index] = readInt16LittleEndian(payload + sourceOffset);
        patternVertical[offset + index] = readInt16LittleEndian(payload + sourceOffset + 2);
    }
    loadedPatternPoints = offset + count;
    if (loadedPatternPoints == expectedPatternPoints) {
        Serial.printf("PATTERN:READY:%u\n", loadedPatternPoints);
    }
    return true;
}

// Mitigation: sensitivity with velocity-aware clamping (avoid extreme values)
static void apply_sensitivity_payload(const uint8_t* payload, uint16_t length) {
    if (length == sizeof(float) * 2) {
        const float horizontal = readFloatLittleEndian(payload);
        const float vertical = readFloatLittleEndian(payload + sizeof(float));
        if (std::isfinite(horizontal) && std::isfinite(vertical)) {
            // Clamp to realistic DPI ranges (avoid extreme values that trigger heuristics)
            horizontalSensitivityFactor = std::clamp(horizontal, 0.3f, 6.0f);
            verticalSensitivityFactor = std::clamp(vertical, 0.3f, 6.0f);
            Serial.printf("SENSITIVITY:H=%.3f:V=%.3f\n", horizontalSensitivityFactor, verticalSensitivityFactor);
            return;
        }
    }

    if (length == sizeof(float)) {
        const float value = readFloatLittleEndian(payload);
        if (std::isfinite(value)) {
            horizontalSensitivityFactor = std::clamp(value, 0.3f, 6.0f);
            verticalSensitivityFactor = horizontalSensitivityFactor;
            Serial.printf("SENSITIVITY:H=%.3f:V=%.3f\n", value, value);
            return;
        }
    }

    Serial.println("ERROR:SENSITIVITY:FORMAT");
}

static void apply_rapid_fire_payload(const uint8_t* payload, uint16_t length) {
    if (length != 3 || payload[0] > 1) {
        Serial.println("ERROR:RAPID_FIRE:FORMAT");
        return;
    }

    const bool enabled = payload[0] == 1;
    const uint16_t roundsPerMinute = readUint16LittleEndian(payload + 1);
    if (enabled && (roundsPerMinute < 60 || roundsPerMinute > 1200)) {
        Serial.println("ERROR:RAPID_FIRE:RATE");
        return;
    }

    rapidFireEnabled = enabled;
    rapidFireActive = false;
    rapidFireRoundsPerMinute = enabled ? roundsPerMinute : 0;
    set_rapid_button(false);
    reset_movement_state();
    Serial.printf("RAPID_FIRE:%s:RPM=%u\n", enabled ? "ON" : "OFF", rapidFireRoundsPerMinute);
}

static void process_command(uint8_t command, const uint8_t* payload, uint16_t length) {
    switch (command) {
        case CMD_PING:
            Serial.println("PONG:RAINBOW-RECOIL:3");
            break;

        case CMD_START:
            if (activeMode == MODE_WEAPON_PATTERN &&
                loadedPatternPoints != expectedPatternPoints) {
                fireActive = false;
                Serial.println("ERROR:PATTERN:INCOMPLETE");
                break;
            }
            fireActive = true;
            rapidFireActive = rapidFireEnabled;
            lastHostKeepAliveAtMs = millis();

            rng_seed();

            set_rapid_button(false);
            reset_movement_state();
            nextMovementAtUs = micros();
            nextRapidShotAtUs = nextMovementAtUs;
            Serial.printf("START:%s\n", activeProfileName);
            break;

        case CMD_STOP:
            stop_output();
            Serial.println("STOP");
            break;

        case CMD_PROFILE:
            apply_profile_payload(payload, length);
            break;

        case CMD_SENSITIVITY:
            apply_sensitivity_payload(payload, length);
            break;

        case CMD_PATTERN:
            apply_pattern_payload(payload, length);
            break;

        case CMD_RAPID_FIRE:
            apply_rapid_fire_payload(payload, length);
            break;

        case CMD_KEEPALIVE:
            if (length != 0) {
                Serial.println("ERROR:KEEPALIVE:FORMAT");
            } else if (fireActive) {
                lastHostKeepAliveAtMs = millis();
            }
            break;

        case CMD_RESET:
            Serial.println("BOOTSEL");
            Serial.flush();
            delay(20);
            reset_usb_boot(0, 0);
            break;

        default:
            Serial.printf("ERROR:COMMAND:%02X\n", command);
            break;
    }
}

static void service_serial() {
    while (Serial.available() > 0) {
        const int value = Serial.read();
        if (value < 0) { return; }
        const uint8_t byte = static_cast<uint8_t>(value);

        switch (parserState) {
            case ParserState::LengthLow:
                parserLength = byte;
                parserState = ParserState::LengthHigh;
                break;

            case ParserState::LengthHigh:
                parserLength |= static_cast<uint16_t>(byte) << 8;
                if (parserLength > maxCommandPayload) {
                    parserOffset = 0;
                    parserState = ParserState::DiscardCommand;
                } else {
                    parserState = ParserState::Command;
                }
                break;

            case ParserState::Command:
                parserCommand = byte;
                parserOffset = 0;
                if (parserLength == 0) {
                    process_command(parserCommand, parserPayload, 0);
                    reset_parser();
                } else {
                    parserState = ParserState::Payload;
                }
                break;

            case ParserState::Payload:
                parserPayload[parserOffset++] = byte;
                if (parserOffset == parserLength) {
                    parserPayload[parserOffset] = 0;
                    process_command(parserCommand, parserPayload, parserLength);
                    reset_parser();
                }
                break;

            case ParserState::DiscardCommand:
                parserOffset = 0;
                parserState = parserLength == 0 ? ParserState::LengthLow : ParserState::DiscardPayload;
                break;

            case ParserState::DiscardPayload:
                if (++parserOffset >= parserLength) {
                    reset_parser();
                    Serial.println("ERROR:FRAME:TOO_LARGE");
                }
                break;
        }
    }
}

// Mitigation: report delta with micro-noise injection (simulates sensor jitter)
static int8_t report_delta_with_noise(int32_t value) {
    if (value == 0) {
        return 0;
    }

    const int32_t direction = value > 0 ? 1 : -1;
    const int32_t maximumMagnitude = std::min<int32_t>(std::abs(value), 100);
    const float noise = rng_next() * 2.0f * DELTA_NOISE_RANGE;
    const int32_t baseMagnitude = std::min<int32_t>(std::abs(value), 100);
    const int32_t noisyMagnitude = static_cast<int32_t>(
        std::round(static_cast<float>(baseMagnitude) + noise));

    // A report must always drain the queue in its original direction and may
    // never consume more movement than is pending.
    const int32_t magnitude = std::clamp<int32_t>(noisyMagnitude, 1, maximumMagnitude);
    return static_cast<int8_t>(direction * magnitude);
}

// Mitigation: queue movement with velocity simulation (smooth acceleration profiles)
static void queue_mouse_movement_with_simulation(int32_t raw_dx, int32_t raw_dy) {
    const float targetX = std::clamp(
        (raw_dx / 256.0f) * horizontalSensitivityFactor,
        -sim.maxVelocity,
        sim.maxVelocity);
    const float targetY = std::clamp(
        (raw_dy / 256.0f) * verticalSensitivityFactor,
        -sim.maxVelocity,
        sim.maxVelocity);

    const auto approach = [](float current, float target, float maximumStep) {
        if (current < target) {
            return std::min(current + maximumStep, target);
        }
        if (current > target) {
            return std::max(current - maximumStep, target);
        }
        return current;
    };

    sim.velocityX = targetX == 0.0f
        ? sim.velocityX * sim.friction
        : approach(sim.velocityX, targetX, sim.acceleration);
    sim.velocityY = targetY == 0.0f
        ? sim.velocityY * sim.friction
        : approach(sim.velocityY, targetY, sim.acceleration);
    sim.accelerating = std::abs(sim.velocityX) >= 0.01f ||
        std::abs(sim.velocityY) >= 0.01f;

    // Scale the noise with actual movement so zero-input axes cannot drift.
    const float outputX = sim.velocityX == 0.0f
        ? 0.0f
        : sim.velocityX * (1.0f + rng_next() * 2.0f * sim.microNoise);
    const float outputY = sim.velocityY == 0.0f
        ? 0.0f
        : sim.velocityY * (1.0f + rng_next() * 2.0f * sim.microNoise);

    fractionalMouseX += outputX * 0.5f; // Damped response for smoother feel
    fractionalMouseY += outputY * 0.5f;

    const int32_t queuedX = static_cast<int32_t>(fractionalMouseX);
    const int32_t queuedY = static_cast<int32_t>(fractionalMouseY);
    fractionalMouseX -= static_cast<float>(queuedX);
    fractionalMouseY -= static_cast<float>(queuedY);
    pendingMouseX += queuedX;
    pendingMouseY += queuedY;
}

static void service_hid() {
    if (pendingMouseX == 0 && pendingMouseY == 0 && !hidStateDirty) {
        return;
    }

    if (TinyUSBDevice.suspended()) {
        TinyUSBDevice.remoteWakeup();
    }

    // Mitigation: adaptive polling rate based on activity state
    uint32_t poll_interval_us = POLL_NORMAL_MS * 1000UL;
    const bool is_idle = !fireActive && !rapidFireActive;
    const bool is_high_activity = rapidFireActive || sim.accelerating;

    if (is_idle) {
        poll_interval_us = POLL_IDLE_MS * 1000UL; // Reduce packet rate during inactivity
    } else if (is_high_activity) {
        poll_interval_us = POLL_HIGH_MS * 1000UL;
    }

    const uint32_t now = micros();
    if (static_cast<uint32_t>(now - lastHidReportAtUs) < poll_interval_us ||
        !TinyUSBDevice.mounted() || !usbHid.ready()) {
        return;
    }

    const int8_t dx = report_delta_with_noise(pendingMouseX);
    const int8_t dy = report_delta_with_noise(pendingMouseY);
    const uint8_t buttons = rapidButtonDown ? MOUSE_BUTTON_LEFT : 0;

    if (usbHid.mouseReport(0, buttons, dx, dy, 0, 0)) {
        pendingMouseX -= dx;
        pendingMouseY -= dy;
        hidStateDirty = false;
        lastHidReportAtUs = now;
    }
}

// Mitigation: movement generation with jittered intervals and velocity simulation
static void generate_movement() {
    float vertical;
    float horizontal;

    if (activeMode == MODE_WEAPON_PATTERN) {
        if (shotsInBurst >= loadedPatternPoints) {
            stop_output();
            return;
        }
        // Pattern values are stored as Q8.8 fixed-point (int16 / 256.0f)
        horizontal = static_cast<float>(patternHorizontal[shotsInBurst]) / 256.0f * horizontalSensitivityFactor;
        vertical = static_cast<float>(patternVertical[shotsInBurst]) / 256.0f * verticalSensitivityFactor;
    } else {
        vertical = activeVerticalCompensation * verticalSensitivityFactor;
        horizontal = activeHorizontalCompensation * horizontalSensitivityFactor;
    }

    // Burst progression reduction for diminishing recoil over shot sequence
    if (activeMode == MODE_GENERAL && shotsInBurst > 0 && activeBurstProgression > 0) {
        const float reduction = 1.0f - (static_cast<float>(shotsInBurst) * activeBurstProgression / 100.0f);
        vertical *= std::max(0.5f, reduction);
        horizontal *= std::max(0.5f, reduction);
    }

    // Apply velocity simulation for smoother, more natural-looking movement
    queue_mouse_movement_with_simulation(static_cast<int32_t>(horizontal * 100.0f), static_cast<int32_t>(vertical * 100.0f));

    ++shotsInBurst;
}

// Mitigation: service movement with jittered intervals and velocity-aware timing
static void service_movement() {
    if (!fireActive || rapidFireActive) {
        return;
    }

    const uint32_t now = micros();
    if (static_cast<int32_t>(now - nextMovementAtUs) >= 0) {
        generate_movement();
        if (!fireActive) {
            return;
        }

        const uint32_t base_interval = activeMode == MODE_WEAPON_PATTERN && activeRoundsPerMinute > 0
            ? static_cast<uint32_t>(60000000.0f / activeRoundsPerMinute)
            : 8000UL;
        nextMovementAtUs = now + get_jittered_interval(base_interval);
    }
}

// Mitigation: rapid fire with jittered shot timing and velocity simulation
static void service_rapid_fire() {
    if (!fireActive || !rapidFireActive || rapidFireRoundsPerMinute == 0) {
        return;
    }

    const uint32_t now = micros();

    // Rapid button release handling
    if (rapidButtonDown && static_cast<int32_t>(now - rapidButtonReleaseAtUs) >= 0) {
        set_rapid_button(false);
        sim.velocityX *= sim.friction * 0.5f;
        sim.velocityY *= sim.friction * 0.5f;
    }

    // Schedule next shot with jittered interval (breaks perfect periodicity)
    if (!rapidButtonDown && static_cast<int32_t>(now - nextRapidShotAtUs) >= 0) {
        set_rapid_button(true);
        generate_movement();
        if (!fireActive) {
            return;
        }
        rapidButtonReleaseAtUs = now + 8000;

        const uint32_t base_interval = static_cast<uint32_t>(60000000.0f / rapidFireRoundsPerMinute);
        const uint32_t shot_interval = get_jittered_interval(base_interval);
        nextRapidShotAtUs += shot_interval;

        if (static_cast<int32_t>(now - nextRapidShotAtUs) >= 0) {
            nextRapidShotAtUs = now + shot_interval;
        }
    }
}

// Mitigation: host watchdog with extended timeout and adaptive behavior
static void service_host_watchdog() {
    if (fireActive && millis() - lastHostKeepAliveAtMs > hostWatchdogTimeoutMs) {
        stop_output();
        Serial.println("STOP:WATCHDOG");
    }
}

void setup() {
    TinyUSBDevice.setManufacturerDescriptor("RP2040");
    TinyUSBDevice.setProductDescriptor("RP2040 USB Mouse");

    if (!TinyUSBDevice.isInitialized()) {
        TinyUSBDevice.begin(0);
    }

    Serial.begin(115200);

    usbHid.setBootProtocol(HID_ITF_PROTOCOL_MOUSE);
    // The endpoint permits 1 ms reports; service_hid applies the 1/2/4 ms pacing.
    usbHid.setPollInterval(POLL_HIGH_MS);
    usbHid.setReportDescriptor(hidReportDescriptor, sizeof(hidReportDescriptor));

    usbHid.setStringDescriptor("USB Mouse");
    usbHid.begin();

    if (TinyUSBDevice.mounted()) {
        TinyUSBDevice.detach();
        delay(10);
        TinyUSBDevice.attach();
    }

    Serial.println("READY:RP2040-ZERO:CDC+HID");
}

void loop() {
#ifdef TINYUSB_NEED_POLLING_TASK
    TinyUSBDevice.task();
#endif
    service_serial();
    service_host_watchdog();
    service_rapid_fire();
    service_movement();
    service_hid();
    // Send a zero-delta heartbeat while active but stationary. Never synthesize
    // movement or alter a pressed rapid-fire button state in this path.
    if (pendingMouseX == 0 && pendingMouseY == 0 && !hidStateDirty &&
        fireActive && !rapidButtonDown) {
        const uint32_t now = millis();
        static uint32_t last_idle_report = 0;
        if (now - last_idle_report >= 20U &&
            TinyUSBDevice.mounted() && usbHid.ready()) {
            if (usbHid.mouseReport(0, 0, 0, 0, 0, 0)) {
                hidStateDirty = false;
                last_idle_report = now;
                lastHidReportAtUs = micros();
            }
        }
    }
    delay(1);
}
