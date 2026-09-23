/**
 * ZeroSense firmware for TENSTAR/Waveshare RP2040-Zero and
 * Waveshare RP2350-USB-C. The RP2350 build also proxies a mouse connected to
 * the board's female PIO-USB port and adds generated movement to that input.
 */

#include <Arduino.h>
#include <Adafruit_TinyUSB.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <cstdarg>
#include <cstdio>
#include "protocol_output.h"
#include "pico/bootrom.h"
#include "correction_scheduler.h"
#include "delta_noise.h"
#include "motion_math.h"

#ifdef ZEROSENSE_RP2350_USB_C
#include "pio_usb.h"
#include "hid_report_decoder.h"
#include "host_receive_recovery.h"
#include "host_core_health.h"
#endif

static ZeroSenseProtocol::OutputQueue<4096> protocolOutput;

static void protocol_printf(const char* format, ...) {
    if (!tud_cdc_connected()) {
        protocolOutput.clear();
        return;
    }
    char line[768];
    va_list args;
    va_start(args, format);
    const int length = vsnprintf(line, sizeof(line), format, args);
    va_end(args);
    if (length < 0 || static_cast<size_t>(length) >= sizeof(line)) {
        ++protocolOutput.droppedMessages;
        return;
    }
    protocolOutput.enqueue(line, static_cast<size_t>(length));
}

static void protocol_println(const char* line) {
    protocol_printf("%s\n", line);
}

static void service_protocol_output() {
    if (!tud_cdc_connected()) {
        protocolOutput.clear();
        return;
    }
    protocolOutput.drain(tud_cdc_write_available(), 128,
        [](const uint8_t* data, size_t length) -> size_t {
            return tud_cdc_write(data, static_cast<uint32_t>(length));
        });
    tud_cdc_write_flush();
}

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
    CMD_ARM_LEASE   = 0xF8,
    CMD_CONFIG_BEGIN = 0xF9,
    CMD_CONFIG_COMMIT = 0xFA,
    CMD_CONFIG_ABORT = 0xFB,
    CMD_STATUS      = 0xFC,
    CMD_GENERAL_SETTINGS = 0xFD,
    CMD_RESET       = 0xFF
};

static constexpr uint16_t maxCommandPayload = 63;
static constexpr uint16_t maxPatternPoints = 160;
static constexpr uint16_t maxPatternRoundsPerMinute = 2000;
// Q8.8 pattern points and the upstream relative-mouse report both top out at
// roughly one signed byte per axis. Keep the profile wire contract aligned
// with that real device limit instead of imposing the old tuning-only 20-unit
// ceiling.
static constexpr float minCompensation = -127.0f;
static constexpr float maxCompensation = 127.0f;
static constexpr float minVerticalCompensation = 0.0f;
static constexpr float minSensitivityFactor = 0.05f;
static constexpr float maxSensitivityFactor = 16.0f;
// Adaptive standalone polling intervals in microseconds.
static constexpr uint32_t POLL_IDLE_US   = 4000; // 250 Hz during idle
static constexpr uint32_t POLL_NORMAL_US = 2000; // 500 Hz normal operation
// A 1 ms endpoint is the closest USB full-speed interval. Firmware targets
// 1 kHz; normal host/controller overhead yields roughly 900 Hz in practice.
static constexpr uint32_t POLL_HIGH_US   = 1000;

// General-mode cadence variation is opt-in. Pattern and rapid-fire timing use
// exact RPM intervals so their configured shot sequence does not drift.
static bool generalTimingJitterEnabled = false;
static constexpr float TIMING_JITTER_PCT = 8.0f;   // ±8% when explicitly enabled

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
    bool accelerating;      // Current movement phase state
} VelocitySimulator;

static VelocitySimulator sim = {
    0.0f,
    0.0f,
    1.2f,
    40.0f,
    0.85f,
    false
};

static int shotsInBurst = 0;
static uint32_t nextMovementAtUs = 0;
static uint32_t lastMovementIntegrationAtUs = 0;
static uint32_t movementIntervalRemainder = 0;
static int32_t pendingMouseX = 0;
static int32_t pendingMouseY = 0;
static int32_t pendingPhysicalMouseX = 0;
static int32_t pendingPhysicalMouseY = 0;
static int32_t pendingPhysicalWheel = 0;
static int32_t pendingPhysicalPan = 0;
static ZeroSenseCorrection::State correctionScheduler;
static bool rapidFireEnabled = false;
static bool rapidFireActive = false;
static bool rapidButtonDown = false;
static bool hidStateDirty = false;
static uint16_t rapidFireRoundsPerMinute = 0;
static uint32_t nextRapidShotAtUs = 0;
static uint32_t rapidIntervalRemainder = 0;
static uint32_t lastHostKeepAliveAtMs = 0;

static constexpr uint8_t configurationSchemaVersion = 3;
static constexpr uint32_t configurationTransactionTimeoutMs = 10000;
typedef struct {
    char profileName[54];
    float verticalCompensation;
    float horizontalCompensation;
    uint8_t burstProgression;
    CompensationMode mode;
    uint16_t roundsPerMinute;
    uint16_t expectedPoints;
    uint16_t loadedPoints;
    int16_t horizontal[maxPatternPoints];
    int16_t vertical[maxPatternPoints];
    float horizontalSensitivity;
    float verticalSensitivity;
    bool rapidEnabled;
    uint16_t rapidRoundsPerMinute;
    bool generalTimingJitterEnabled;
    bool deltaNoiseEnabled;
} ConfigurationSnapshot;

static ConfigurationSnapshot previousConfiguration;
static bool configurationTransactionActive = false;
static uint32_t configurationTransactionId = 0;
static uint32_t configurationExpectedHash = 0;
static uint32_t configurationTransactionTouchedAtMs = 0;

// Mitigation: additional global variables for adaptive polling and watchdog
static uint32_t lastHidReportAtUs = 0;
static uint32_t lastHighActivityReportAtUs = 0;
static uint32_t hidReportsSent = 0;
static uint32_t hidBusyDeferrals = 0;
static uint32_t maximumQueuedDelta = 0;
static uint32_t maximumActiveReportGapUs = 0;
static uint32_t upstreamDisconnectStops = 0;
static uint32_t generalIntervalClampCount = 0;
static uint32_t currentHidReportIntervalUs = POLL_IDLE_US;
static bool deltaNoiseEnabled = false;
static ZeroSenseDeltaNoise::AxisState deltaNoiseX;
static ZeroSenseDeltaNoise::AxisState deltaNoiseY;
static const uint32_t hostWatchdogTimeoutMs = 750; // ms - matches original constant value
static uint32_t rapidButtonReleaseAtUs = 0;

#ifdef ZEROSENSE_RP2350_USB_C
// The Windows app grants a short foreground/armed lease, but the RP2350 uses
// the raw downstream mouse state as the only firing trigger. This keeps
// synthesized rapid-fire releases out of the activation feedback path.
static bool rp2350ArmLeaseEnabled = false;
static bool rp2350TriggerLatched = false;
static uint32_t lastRp2350ArmLeaseAtMs = 0;
static constexpr uint32_t rp2350ArmLeaseTimeoutMs = 750;
#endif

#ifdef ZEROSENSE_RP2350_USB_C
// The proxy descriptor keeps the standard relative-mouse packet layout but
// exposes all eight button bits instead of turning the upper three into
// padding. X/Y/wheel/pan remain signed 8-bit values and larger deltas are
// drained over consecutive 1 ms reports without being discarded.
static const uint8_t hidReportDescriptor[] = {
    HID_USAGE_PAGE(HID_USAGE_PAGE_DESKTOP),
    HID_USAGE(HID_USAGE_DESKTOP_MOUSE),
    HID_COLLECTION(HID_COLLECTION_APPLICATION),
        HID_USAGE(HID_USAGE_DESKTOP_POINTER),
        HID_COLLECTION(HID_COLLECTION_PHYSICAL),
            HID_USAGE_PAGE(HID_USAGE_PAGE_BUTTON),
            HID_USAGE_MIN(1),
            HID_USAGE_MAX(8),
            HID_LOGICAL_MIN(0),
            HID_LOGICAL_MAX(1),
            HID_REPORT_COUNT(8),
            HID_REPORT_SIZE(1),
            HID_INPUT(HID_DATA | HID_VARIABLE | HID_ABSOLUTE),
            HID_USAGE_PAGE(HID_USAGE_PAGE_DESKTOP),
            HID_USAGE(HID_USAGE_DESKTOP_X),
            HID_USAGE(HID_USAGE_DESKTOP_Y),
            HID_LOGICAL_MIN(0x81),
            HID_LOGICAL_MAX(0x7f),
            HID_REPORT_COUNT(2),
            HID_REPORT_SIZE(8),
            HID_INPUT(HID_DATA | HID_VARIABLE | HID_RELATIVE),
            HID_USAGE(HID_USAGE_DESKTOP_WHEEL),
            HID_LOGICAL_MIN(0x81),
            HID_LOGICAL_MAX(0x7f),
            HID_REPORT_COUNT(1),
            HID_REPORT_SIZE(8),
            HID_INPUT(HID_DATA | HID_VARIABLE | HID_RELATIVE),
            HID_USAGE_PAGE(HID_USAGE_PAGE_CONSUMER),
            HID_USAGE_N(HID_USAGE_CONSUMER_AC_PAN, 2),
            HID_LOGICAL_MIN(0x81),
            HID_LOGICAL_MAX(0x7f),
            HID_REPORT_COUNT(1),
            HID_REPORT_SIZE(8),
            HID_INPUT(HID_DATA | HID_VARIABLE | HID_RELATIVE),
        HID_COLLECTION_END,
    HID_COLLECTION_END
};
#else
static const uint8_t hidReportDescriptor[] = {
    TUD_HID_REPORT_DESC_MOUSE()
};
#endif

static Adafruit_USBD_HID usbHid;

#ifdef ZEROSENSE_RP2350_USB_C
static constexpr uint8_t hostMouseDpPin = 12;
static constexpr uint8_t maxHostMouseInterfaces = 4;
static constexpr uint8_t maxHostReceiveQueueFailures = 8;
static constexpr int32_t maxHostAccumulator = 32767;

struct HostMouseInterface {
    bool used;
    bool connected;
    bool bootProtocolPending;
    uint8_t deviceAddress;
    uint8_t instance;
    uint8_t buttons;
    ZeroSenseHostRecovery::QueueState receiveQueue;
    uint16_t vid;
    uint16_t pid;
    ZeroSenseHid::Decoder decoder;
};

enum class MouseProxyEvent : uint8_t {
    None,
    Connected,
    Disconnected,
    Unsupported,
    HostError
};

static Adafruit_USBH_Host usbHost;
static HostMouseInterface hostMouseInterfaces[maxHostMouseInterfaces] = {};
static bool hostStackStarted = false;

static std::atomic<int32_t> hostMouseX{0};
static std::atomic<int32_t> hostMouseY{0};
static std::atomic<int32_t> hostMouseWheel{0};
static std::atomic<int32_t> hostMousePan{0};
static std::atomic<uint8_t> hostMouseButtons{0};
static std::atomic<bool> hostMouseConnected{false};
static std::atomic<uint16_t> hostMouseVid{0};
static std::atomic<uint16_t> hostMousePid{0};
static std::atomic<MouseProxyEvent> mouseProxyEvent{MouseProxyEvent::None};
static std::atomic<bool> hostMouseFaultPending{false};
static std::atomic<uint32_t> hostReportsReceived{0};
static std::atomic<uint32_t> hostDecodeErrors{0};
static std::atomic<uint32_t> hostEmptyReports{0};
static std::atomic<uint32_t> hostAccumulatorSaturations{0};
static std::atomic<uint32_t> hostReceiveRecoveries{0};
static std::atomic<uint32_t> hostReceiveQueueFailures{0};
static std::atomic<uint32_t> hostMouseUnmounts{0};
static std::atomic<uint32_t> hostTaskLastAtMs{0};
static std::atomic<bool> hostCoreStalled{false};

// Parse the standard HID short-item format so report-ID, 16-bit movement, and
// ordinary gaming-mouse descriptors work without assuming a fixed byte layout.
static bool parse_host_mouse_descriptor(
    HostMouseInterface& mouseInterface,
    const uint8_t* descriptor,
    uint16_t descriptorLength,
    bool& descriptorHadMouseApplication) {
    return ZeroSenseHid::parseDescriptor(
        mouseInterface.decoder,
        descriptor,
        descriptorLength,
        descriptorHadMouseApplication);
}

static void configure_boot_mouse_layout(HostMouseInterface& mouseInterface) {
    ZeroSenseHid::configureBootMouse(mouseInterface.decoder);
}

static void add_host_accumulator(std::atomic<int32_t>& accumulator, int32_t value) {
    int32_t current = accumulator.load(std::memory_order_relaxed);
    while (true) {
        const int64_t unbounded = static_cast<int64_t>(current) + value;
        const int32_t next = static_cast<int32_t>(std::clamp<int64_t>(
            unbounded,
            -maxHostAccumulator,
            maxHostAccumulator));
        if (accumulator.compare_exchange_weak(
                current,
                next,
                std::memory_order_release,
                std::memory_order_relaxed)) {
            if (unbounded != next) {
                hostAccumulatorSaturations.fetch_add(1, std::memory_order_relaxed);
            }
            return;
        }
    }
}

static HostMouseInterface* find_host_mouse_interface(
    uint8_t deviceAddress,
    uint8_t instance) {
    for (auto& mouseInterface : hostMouseInterfaces) {
        if (mouseInterface.used &&
            mouseInterface.deviceAddress == deviceAddress &&
            mouseInterface.instance == instance) {
            return &mouseInterface;
        }
    }
    return nullptr;
}

static HostMouseInterface* allocate_host_mouse_interface(
    uint8_t deviceAddress,
    uint8_t instance) {
    HostMouseInterface* existing = find_host_mouse_interface(deviceAddress, instance);
    if (existing != nullptr) {
        return existing;
    }
    for (auto& mouseInterface : hostMouseInterfaces) {
        if (!mouseInterface.used) {
            memset(&mouseInterface, 0, sizeof(mouseInterface));
            mouseInterface.used = true;
            mouseInterface.deviceAddress = deviceAddress;
            mouseInterface.instance = instance;
            return &mouseInterface;
        }
    }
    return nullptr;
}

static void publish_aggregate_host_mouse_state() {
    uint8_t buttons = 0;
    bool connected = false;
    uint16_t vid = 0;
    uint16_t pid = 0;
    for (const auto& mouseInterface : hostMouseInterfaces) {
        if (!mouseInterface.used || !mouseInterface.connected) {
            continue;
        }
        buttons |= mouseInterface.buttons;
        if (!connected) {
            vid = mouseInterface.vid;
            pid = mouseInterface.pid;
        }
        connected = true;
    }
    hostMouseButtons.store(buttons, std::memory_order_release);
    hostMouseVid.store(vid, std::memory_order_release);
    hostMousePid.store(pid, std::memory_order_release);
    hostMouseConnected.store(connected, std::memory_order_release);
}

static bool decode_host_mouse_report(
    HostMouseInterface& mouseInterface,
    const uint8_t* report,
    uint16_t length) {
    ZeroSenseHid::DecodedReport sharedDecoded = {};
    if (!ZeroSenseHid::decodeReport(
            mouseInterface.decoder,
            report,
            length,
            sharedDecoded)) {
        return false;
    }
    mouseInterface.buttons = static_cast<uint8_t>(
        (mouseInterface.buttons & ~sharedDecoded.reportedButtonMask) |
        (sharedDecoded.buttons & sharedDecoded.reportedButtonMask));
    publish_aggregate_host_mouse_state();
    add_host_accumulator(hostMouseX, sharedDecoded.deltaX);
    add_host_accumulator(hostMouseY, sharedDecoded.deltaY);
    add_host_accumulator(hostMouseWheel, sharedDecoded.wheel);
    add_host_accumulator(hostMousePan, sharedDecoded.pan);
    return true;
}

static void clear_host_mouse_state() {
    hostMouseX.store(0, std::memory_order_release);
    hostMouseY.store(0, std::memory_order_release);
    hostMouseWheel.store(0, std::memory_order_release);
    hostMousePan.store(0, std::memory_order_release);
    hostMouseButtons.store(0, std::memory_order_release);
    hostMouseConnected.store(false, std::memory_order_release);
    memset(hostMouseInterfaces, 0, sizeof(hostMouseInterfaces));
    hostMouseFaultPending.store(true, std::memory_order_release);
}

static void clear_host_mouse_interface(HostMouseInterface& mouseInterface) {
    const bool wasConnected = mouseInterface.connected;
    memset(&mouseInterface, 0, sizeof(mouseInterface));
    publish_aggregate_host_mouse_state();
    if (wasConnected) {
        hostMouseFaultPending.store(true, std::memory_order_release);
    }
}

static void connect_host_mouse_interface(HostMouseInterface& mouseInterface) {
    uint16_t vid = 0;
    uint16_t pid = 0;
    tuh_vid_pid_get(mouseInterface.deviceAddress, &vid, &pid);
    mouseInterface.vid = vid;
    mouseInterface.pid = pid;
    mouseInterface.connected = true;
    mouseInterface.bootProtocolPending = false;
    // Do not clear a queued fault from another interface here. Core 0 must
    // observe every disconnect and revoke the arm lease before re-arming.
    publish_aggregate_host_mouse_state();
    mouseProxyEvent.store(MouseProxyEvent::Connected, std::memory_order_release);
}

// A completed interrupt transfer must always be followed by another queued
// receive. PIO-USB can briefly reject that queue request while it finishes an
// endpoint transition; retaining the mounted interface and retrying from the
// bounded host loop avoids the "COM still connected, mouse frozen" state.
static ZeroSenseHostRecovery::QueueOutcome queue_host_mouse_report(
    HostMouseInterface& mouseInterface) {
    if (!mouseInterface.used || mouseInterface.bootProtocolPending) {
        return ZeroSenseHostRecovery::QueueOutcome::Exhausted;
    }
    const bool queued = tuh_hid_receive_report(
        mouseInterface.deviceAddress,
        mouseInterface.instance);
    const auto outcome = ZeroSenseHostRecovery::recordQueueAttempt(
        mouseInterface.receiveQueue,
        queued,
        maxHostReceiveQueueFailures,
        millis());
    if (!queued) {
        hostReceiveQueueFailures.fetch_add(1, std::memory_order_relaxed);
    }
    if (outcome == ZeroSenseHostRecovery::QueueOutcome::Recovered) {
        hostReceiveRecoveries.fetch_add(1, std::memory_order_relaxed);
    }
    return outcome;
}

static void service_host_mouse_receive_recovery() {
    for (auto& mouseInterface : hostMouseInterfaces) {
        if (!mouseInterface.used || mouseInterface.bootProtocolPending ||
            !ZeroSenseHostRecovery::retryDue(
                mouseInterface.receiveQueue, millis(), maxHostReceiveQueueFailures)) {
            continue;
        }

        // A busy endpoint already has the next report queued. An unexpectedly
        // ready endpoint has no outstanding transfer and must be re-armed.
        // Do not re-check tuh_mounted() here: TinyUSB invokes the HID mount
        // callback before the device-wide mounted flag is finalized. The
        // authoritative unmount callback clears this interface when needed.
        if (!tuh_hid_receive_ready(
                mouseInterface.deviceAddress,
                mouseInterface.instance)) {
            continue;
        }
        mouseInterface.receiveQueue.recoveryPending = true;
        const auto outcome = queue_host_mouse_report(mouseInterface);
        if (outcome == ZeroSenseHostRecovery::QueueOutcome::Queued ||
            outcome == ZeroSenseHostRecovery::QueueOutcome::Recovered ||
            outcome == ZeroSenseHostRecovery::QueueOutcome::Retry) {
            continue;
        }
        // Retain the descriptor and address until the authoritative unmount.
        // Release stale buttons and revoke arming once, but keep retrying with
        // backoff. Only an actual decoded report marks this interface healthy.
        if (mouseInterface.connected) {
            mouseInterface.connected = false;
            mouseInterface.buttons = 0;
            publish_aggregate_host_mouse_state();
            hostMouseFaultPending.store(true, std::memory_order_release);
            mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
        }
    }
}

extern "C" {
void tuh_hid_mount_cb(
    uint8_t deviceAddress,
    uint8_t instance,
    const uint8_t* reportDescriptor,
    uint16_t descriptorLength) {
    HostMouseInterface* mouseInterface = allocate_host_mouse_interface(
        deviceAddress,
        instance);
    if (mouseInterface == nullptr) {
        hostMouseFaultPending.store(true, std::memory_order_release);
        mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
        return;
    }

    const uint8_t protocol = tuh_hid_interface_protocol(deviceAddress, instance);
    bool descriptorHadMouseApplication = false;
    const bool parsed = parse_host_mouse_descriptor(
        *mouseInterface,
        reportDescriptor,
        descriptorLength,
        descriptorHadMouseApplication);
    if (!parsed) {
        if (protocol == HID_ITF_PROTOCOL_MOUSE &&
            tuh_hid_set_protocol(deviceAddress, instance, HID_PROTOCOL_BOOT)) {
            mouseInterface->bootProtocolPending = true;
            return;
        }
        if (protocol == HID_ITF_PROTOCOL_MOUSE || descriptorHadMouseApplication) {
            hostMouseFaultPending.store(true, std::memory_order_release);
            mouseProxyEvent.store(MouseProxyEvent::Unsupported, std::memory_order_release);
        }
        memset(mouseInterface, 0, sizeof(*mouseInterface));
        return;
    }

    connect_host_mouse_interface(*mouseInterface);
    queue_host_mouse_report(*mouseInterface);
}

void tuh_hid_set_protocol_complete_cb(
    uint8_t deviceAddress,
    uint8_t instance,
    uint8_t protocol) {
    HostMouseInterface* mouseInterface = find_host_mouse_interface(
        deviceAddress,
        instance);
    if (mouseInterface == nullptr || protocol != HID_PROTOCOL_BOOT ||
        !mouseInterface->bootProtocolPending) {
        return;
    }

    configure_boot_mouse_layout(*mouseInterface);
    connect_host_mouse_interface(*mouseInterface);
    queue_host_mouse_report(*mouseInterface);
}

void tuh_hid_umount_cb(uint8_t deviceAddress, uint8_t instance) {
    HostMouseInterface* mouseInterface = find_host_mouse_interface(
        deviceAddress,
        instance);
    if (mouseInterface != nullptr) {
        hostMouseUnmounts.fetch_add(1, std::memory_order_relaxed);
        clear_host_mouse_interface(*mouseInterface);
        mouseProxyEvent.store(
            hostMouseConnected.load(std::memory_order_acquire)
                ? MouseProxyEvent::HostError
                : MouseProxyEvent::Disconnected,
            std::memory_order_release);
    }
}

void tuh_hid_report_received_cb(
    uint8_t deviceAddress,
    uint8_t instance,
    const uint8_t* report,
    uint16_t length) {
    HostMouseInterface* mouseInterface = find_host_mouse_interface(
        deviceAddress,
        instance);
    if (mouseInterface != nullptr && !mouseInterface->bootProtocolPending) {
        hostReportsReceived.fetch_add(1, std::memory_order_relaxed);
        // TinyUSB HID also invokes this callback for failed transfers with
        // zero bytes. Keep the legacy error total, but distinguish those
        // callbacks from non-empty reports rejected by the HID decoder.
        if (length == 0) {
            hostEmptyReports.fetch_add(1, std::memory_order_relaxed);
        }
        if (!decode_host_mouse_report(*mouseInterface, report, length)) {
            hostDecodeErrors.fetch_add(1, std::memory_order_relaxed);
        } else {
            hostTaskLastAtMs.store(millis(), std::memory_order_release);
            const bool wasStalled =
                hostCoreStalled.exchange(false, std::memory_order_acq_rel);
            if (!mouseInterface->connected) {
                connect_host_mouse_interface(*mouseInterface);
            } else if (wasStalled) {
                mouseProxyEvent.store(MouseProxyEvent::Connected, std::memory_order_release);
            }
        }
        queue_host_mouse_report(*mouseInterface);
    }
}
} // extern "C"
#endif

enum class ParserState : uint8_t {
    MagicFirst,
    MagicSecond,
    Version,
    SequenceLow,
    SequenceHigh,
    Command,
    Length,
    Payload,
    CrcLow,
    CrcHigh
};
static constexpr uint8_t frameMagicFirst = 0xA5;
static constexpr uint8_t frameMagicSecond = 0x5A;
static constexpr uint8_t frameVersion = 1;
static constexpr uint32_t parserTimeoutMs = 250;
static ParserState parserState = ParserState::MagicFirst;
static uint8_t parserLength = 0;
static uint8_t parserOffset = 0;
static uint8_t parserCommand = 0;
static uint16_t parserSequence = 0;
static uint16_t parserCrc = 0xFFFF;
static uint16_t parserReceivedCrc = 0;
static uint32_t parserFrameStartedAtMs = 0;
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

static uint32_t readUint32LittleEndian(const uint8_t* source) {
    return static_cast<uint32_t>(source[0]) |
        (static_cast<uint32_t>(source[1]) << 8) |
        (static_cast<uint32_t>(source[2]) << 16) |
        (static_cast<uint32_t>(source[3]) << 24);
}

static int16_t readInt16LittleEndian(const uint8_t* source) {
    return static_cast<int16_t>(readUint16LittleEndian(source));
}

// General mode may vary its fixed 8 ms update cadence slightly. This is not
// used for configured weapon patterns or rapid-fire shot scheduling.
static uint32_t get_jittered_interval(uint32_t base_us) {
    if (!generalTimingJitterEnabled) {
        return base_us;
    }
    const float jitter_scale = 1.0f +
        rng_next() * 2.0f * (TIMING_JITTER_PCT / 100.0f);
    const uint32_t interval = static_cast<uint32_t>(
        std::round(static_cast<float>(base_us) * jitter_scale));
    return std::max<uint32_t>(interval, 1U);
}

// Carry the fractional microseconds instead of always rounding an RPM period
// down. Across exactly `roundsPerMinute` intervals, the returned values sum to
// exactly 60,000,000 us, so phase-locked scheduling has no systematic drift.
static uint32_t next_rpm_interval(
    uint16_t roundsPerMinute,
    uint32_t& remainderAccumulator) {
    if (roundsPerMinute == 0) {
        return 1;
    }

    const uint32_t base = 60000000UL / roundsPerMinute;
    remainderAccumulator += 60000000UL % roundsPerMinute;
    if (remainderAccumulator >= roundsPerMinute) {
        remainderAccumulator -= roundsPerMinute;
        return base + 1U;
    }
    return base;
}

static void reset_movement_state() {
    shotsInBurst = 0;
    movementIntervalRemainder = 0;
    rapidIntervalRemainder = 0;
    fractionalMouseX = 0.0f;
    fractionalMouseY = 0.0f;
    pendingMouseX = 0;
    pendingMouseY = 0;
    // STOP, disconnect, and configuration changes must not emit a deferred
    // noise repayment after generated movement has been revoked.
    deltaNoiseX = {};
    deltaNoiseY = {};
    ZeroSenseCorrection::resetOutput(correctionScheduler);
    lastMovementIntegrationAtUs = 0;
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

static int32_t round_q16_to_integer(int64_t value) {
    return ZeroSenseCorrection::roundedFraction(value);
}

// A normally completed pattern is different from an emergency STOP. Preserve
// every whole-count delta already queued for HID, and quantize the final
// sub-count residue once, instead of clearing the last scheduled correction.
static void complete_pattern_output() {
    if (!fireActive) {
        return;
    }

    pendingMouseX += round_q16_to_integer(correctionScheduler.fractionXQ16);
    pendingMouseY += round_q16_to_integer(correctionScheduler.fractionYQ16);
    ZeroSenseCorrection::resetOutput(correctionScheduler);
    sim.velocityX = 0.0f;
    sim.velocityY = 0.0f;
    sim.accelerating = false;
    fireActive = false;
    rapidFireActive = false;
    set_rapid_button(false);
    protocol_println("STOP:PATTERN_COMPLETE");
}

static void capture_configuration(ConfigurationSnapshot& snapshot) {
    memcpy(snapshot.profileName, activeProfileName, sizeof(activeProfileName));
    snapshot.verticalCompensation = activeVerticalCompensation;
    snapshot.horizontalCompensation = activeHorizontalCompensation;
    snapshot.burstProgression = activeBurstProgression;
    snapshot.mode = activeMode;
    snapshot.roundsPerMinute = activeRoundsPerMinute;
    snapshot.expectedPoints = expectedPatternPoints;
    snapshot.loadedPoints = loadedPatternPoints;
    memcpy(snapshot.horizontal, patternHorizontal, sizeof(patternHorizontal));
    memcpy(snapshot.vertical, patternVertical, sizeof(patternVertical));
    snapshot.horizontalSensitivity = horizontalSensitivityFactor;
    snapshot.verticalSensitivity = verticalSensitivityFactor;
    snapshot.rapidEnabled = rapidFireEnabled;
    snapshot.rapidRoundsPerMinute = rapidFireRoundsPerMinute;
    snapshot.generalTimingJitterEnabled = generalTimingJitterEnabled;
    snapshot.deltaNoiseEnabled = deltaNoiseEnabled;
}

static void restore_configuration(const ConfigurationSnapshot& snapshot) {
    stop_output();
    memcpy(activeProfileName, snapshot.profileName, sizeof(activeProfileName));
    activeProfileName[sizeof(activeProfileName) - 1] = '\0';
    activeVerticalCompensation = snapshot.verticalCompensation;
    activeHorizontalCompensation = snapshot.horizontalCompensation;
    activeBurstProgression = snapshot.burstProgression;
    activeMode = snapshot.mode;
    activeRoundsPerMinute = snapshot.roundsPerMinute;
    expectedPatternPoints = snapshot.expectedPoints;
    loadedPatternPoints = snapshot.loadedPoints;
    memcpy(patternHorizontal, snapshot.horizontal, sizeof(patternHorizontal));
    memcpy(patternVertical, snapshot.vertical, sizeof(patternVertical));
    horizontalSensitivityFactor = snapshot.horizontalSensitivity;
    verticalSensitivityFactor = snapshot.verticalSensitivity;
    rapidFireEnabled = snapshot.rapidEnabled;
    rapidFireRoundsPerMinute = snapshot.rapidRoundsPerMinute;
    generalTimingJitterEnabled = snapshot.generalTimingJitterEnabled;
    deltaNoiseEnabled = snapshot.deltaNoiseEnabled;
}

static void hash_byte(uint32_t& hash, uint8_t value) {
    hash ^= value;
    hash *= 16777619UL;
}

static void hash_uint16(uint32_t& hash, uint16_t value) {
    hash_byte(hash, static_cast<uint8_t>(value));
    hash_byte(hash, static_cast<uint8_t>(value >> 8));
}

static void hash_int16(uint32_t& hash, int16_t value) {
    hash_uint16(hash, static_cast<uint16_t>(value));
}

static void hash_float(uint32_t& hash, float value) {
    uint32_t bits = 0;
    static_assert(sizeof(bits) == sizeof(value), "32-bit floats are required");
    memcpy(&bits, &value, sizeof(bits));
    hash_byte(hash, static_cast<uint8_t>(bits));
    hash_byte(hash, static_cast<uint8_t>(bits >> 8));
    hash_byte(hash, static_cast<uint8_t>(bits >> 16));
    hash_byte(hash, static_cast<uint8_t>(bits >> 24));
}

static uint32_t current_configuration_hash() {
    uint32_t hash = 2166136261UL;
    hash_byte(hash, configurationSchemaVersion);
    hash_byte(hash, static_cast<uint8_t>(activeMode));
    hash_float(hash, activeVerticalCompensation);
    hash_float(hash, activeHorizontalCompensation);
    hash_byte(hash, activeBurstProgression);
    hash_uint16(hash, activeMode == MODE_WEAPON_PATTERN ? activeRoundsPerMinute : 0);
    const uint8_t pointCount = activeMode == MODE_WEAPON_PATTERN
        ? static_cast<uint8_t>(loadedPatternPoints)
        : 0;
    hash_byte(hash, pointCount);
    const uint8_t nameLength = static_cast<uint8_t>(strnlen(
        activeProfileName,
        sizeof(activeProfileName) - 1));
    hash_byte(hash, nameLength);
    for (uint8_t index = 0; index < nameLength; ++index) {
        hash_byte(hash, static_cast<uint8_t>(activeProfileName[index]));
    }
    hash_float(hash, horizontalSensitivityFactor);
    hash_float(hash, verticalSensitivityFactor);
    hash_byte(hash, rapidFireEnabled ? 1 : 0);
    hash_uint16(hash, rapidFireEnabled ? rapidFireRoundsPerMinute : 0);
    hash_byte(hash, generalTimingJitterEnabled ? 1 : 0);
    hash_byte(hash, deltaNoiseEnabled ? 1 : 0);
    for (uint8_t index = 0; index < pointCount; ++index) {
        hash_int16(hash, patternHorizontal[index]);
        hash_int16(hash, patternVertical[index]);
    }
    return hash;
}

static void rollback_configuration_transaction() {
    if (!configurationTransactionActive) {
        return;
    }
    restore_configuration(previousConfiguration);
    configurationTransactionActive = false;
    configurationTransactionId = 0;
    configurationExpectedHash = 0;
}

static bool start_output() {
    if (activeMode == MODE_WEAPON_PATTERN &&
        loadedPatternPoints != expectedPatternPoints) {
        fireActive = false;
        protocol_println("ERROR:PATTERN:INCOMPLETE");
        return false;
    }

    fireActive = true;
    rapidFireActive = rapidFireEnabled;
    lastHostKeepAliveAtMs = millis();
    rng_seed();
    set_rapid_button(false);
    reset_movement_state();
    nextMovementAtUs = micros();
    // The first General-mode update represents one complete reference frame.
    // Subsequent updates integrate the actual elapsed time between executions.
    lastMovementIntegrationAtUs = nextMovementAtUs - 8000UL;
    nextRapidShotAtUs = nextMovementAtUs;
    protocol_printf("START:%s\n", activeProfileName);
    return true;
}

static void reset_parser() {
    parserState = ParserState::MagicFirst;
    parserLength = 0;
    parserOffset = 0;
    parserCommand = 0;
    parserSequence = 0;
    parserCrc = 0xFFFF;
    parserReceivedCrc = 0;
    parserFrameStartedAtMs = 0;
}

static void reset_parser_preserving_magic(uint8_t currentByte) {
    reset_parser();
    if (currentByte == frameMagicFirst) {
        parserFrameStartedAtMs = millis();
        parserState = ParserState::MagicSecond;
    }
}

static void parser_crc_byte(uint8_t value) {
    parserCrc ^= static_cast<uint16_t>(value) << 8;
    for (uint8_t bit = 0; bit < 8; ++bit) {
        parserCrc = (parserCrc & 0x8000U) != 0
            ? static_cast<uint16_t>((parserCrc << 1) ^ 0x1021U)
            : static_cast<uint16_t>(parserCrc << 1);
    }
}

static void print_hardware_identity() {
    protocol_println("BUILD:HOST-BASELINE-GUARDS-X2-20260923");
#ifdef ZEROSENSE_RP2350_USB_C
    protocol_println("DEVICE:RP2350-USB-C:MOUSE-PROXY");
    if (hostCoreStalled.load(std::memory_order_acquire)) {
        protocol_println("MOUSE:HOST_ERROR");
    } else if (hostMouseConnected.load(std::memory_order_acquire)) {
        protocol_printf(
            "MOUSE:CONNECTED:VID=%04X:PID=%04X\n",
            hostMouseVid.load(std::memory_order_acquire),
            hostMousePid.load(std::memory_order_acquire));
    } else {
        protocol_println("MOUSE:DISCONNECTED");
    }
#else
    protocol_println("DEVICE:RP2040-ZERO:CDC+HID");
#endif
}

// Validate and apply a complete host profile.
static bool apply_profile_payload(const uint8_t* payload, uint16_t length) {
    if (length < 10 || (payload[0] != 1 && payload[0] != 2)) {
        protocol_println("ERROR:PROFILE:FORMAT");
        return false;
    }

    const bool is_version2 = payload[0] == 2;
    if (is_version2 && length < 14) {
        protocol_println("ERROR:PROFILE:FORMAT");
        return false;
    }

    const uint16_t verticalOffset = is_version2 ? 2 : 1;
    const uint16_t horizontalOffset = is_version2 ? 6 : 5;
    const uint16_t burstOffset = is_version2 ? 10 : 9;
    const uint16_t nameOffset = is_version2 ? 14 : 10;
    const float vertical = readFloatLittleEndian(payload + verticalOffset);
    const float horizontal = readFloatLittleEndian(payload + horizontalOffset);
    if (!std::isfinite(vertical) || !std::isfinite(horizontal)) {
        protocol_println("ERROR:PROFILE:VALUE");
        return false;
    }
    if (vertical < minVerticalCompensation || vertical > maxCompensation ||
        horizontal < minCompensation || horizontal > maxCompensation) {
        protocol_println("ERROR:PROFILE:RANGE");
        return false;
    }
    if (payload[burstOffset] > 100) {
        protocol_println("ERROR:PROFILE:BURST_RANGE");
        return false;
    }

    uint8_t requestedMode = MODE_GENERAL;
    uint16_t requestedRpm = 0;
    uint16_t requestedPoints = 0;
    if (is_version2) {
        requestedMode = payload[1];
        requestedRpm = readUint16LittleEndian(payload + 11);
        requestedPoints = payload[13];
        if (requestedMode > MODE_WEAPON_PATTERN ||
            (requestedMode == MODE_WEAPON_PATTERN &&
             (requestedRpm == 0 || requestedPoints == 0 ||
              requestedRpm > maxPatternRoundsPerMinute ||
              requestedPoints > maxPatternPoints)) ||
            (requestedMode == MODE_GENERAL &&
             (requestedRpm != 0 || requestedPoints != 0))) {
            protocol_println("ERROR:PROFILE:MODE_RANGE");
            return false;
        }
    }

    const uint16_t sourceNameLength = length - nameOffset;
    for (uint16_t index = 0; index < sourceNameLength; ++index) {
        const uint8_t value = payload[nameOffset + index];
        if (value < 0x20 || value == 0x7F) {
            protocol_println("ERROR:PROFILE:NAME");
            return false;
        }
    }

    activeVerticalCompensation = vertical;
    activeHorizontalCompensation = horizontal;
    activeBurstProgression = payload[burstOffset];
    activeMode = MODE_GENERAL;
    activeRoundsPerMinute = 0;
    expectedPatternPoints = 0;
    loadedPatternPoints = 0;
    rapidFireEnabled = false;
    rapidFireActive = false;
    rapidFireRoundsPerMinute = 0;
    set_rapid_button(false);

    if (is_version2 && requestedMode == MODE_WEAPON_PATTERN) {
        activeMode = MODE_WEAPON_PATTERN;
        activeRoundsPerMinute = requestedRpm;
        expectedPatternPoints = requestedPoints;
    }

    const uint16_t nameLength = std::min<uint16_t>(sourceNameLength, sizeof(activeProfileName) - 1);
    if (nameLength > 0) {
        memcpy(activeProfileName, payload + nameOffset, nameLength);
        activeProfileName[nameLength] = '\0';
    } else {
        strcpy(activeProfileName, "Unnamed");
    }

    reset_movement_state();
    protocol_printf(
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
        protocol_println("ERROR:PATTERN:FORMAT");
        return false;
    }

    const uint16_t offset = payload[1];
    const uint16_t count = payload[2];
    if (count == 0 || length != 3 + count * 4 ||
        activeMode != MODE_WEAPON_PATTERN ||
        offset != loadedPatternPoints ||
        offset + count > expectedPatternPoints ||
        offset + count > maxPatternPoints) {
        protocol_println("ERROR:PATTERN:RANGE");
        return false;
    }

    static constexpr int16_t minimumHorizontalQ8_8 = -127 * 256;
    static constexpr int16_t maximumPatternQ8_8 = 127 * 256;
    for (uint16_t index = 0; index < count; ++index) {
        const uint16_t sourceOffset = 3 + index * 4;
        const int16_t horizontal = readInt16LittleEndian(payload + sourceOffset);
        const int16_t vertical = readInt16LittleEndian(payload + sourceOffset + 2);
        if (horizontal < minimumHorizontalQ8_8 || horizontal > maximumPatternQ8_8 ||
            vertical < 0 || vertical > maximumPatternQ8_8) {
            protocol_println("ERROR:PATTERN:VALUE");
            return false;
        }
    }
    for (uint16_t index = 0; index < count; ++index) {
        const uint16_t sourceOffset = 3 + index * 4;
        patternHorizontal[offset + index] = readInt16LittleEndian(payload + sourceOffset);
        patternVertical[offset + index] = readInt16LittleEndian(payload + sourceOffset + 2);
    }
    loadedPatternPoints = offset + count;
    if (loadedPatternPoints == expectedPatternPoints) {
        protocol_printf("PATTERN:READY:%u\n", loadedPatternPoints);
    }
    return true;
}

// Apply exact host-calibrated sensitivity inside the shared finite safety range.
static void apply_sensitivity_payload(const uint8_t* payload, uint16_t length) {
    if (length == sizeof(float) * 2) {
        const float horizontal = readFloatLittleEndian(payload);
        const float vertical = readFloatLittleEndian(payload + sizeof(float));
        if (std::isfinite(horizontal) && std::isfinite(vertical) &&
            horizontal >= minSensitivityFactor && horizontal <= maxSensitivityFactor &&
            vertical >= minSensitivityFactor && vertical <= maxSensitivityFactor) {
            horizontalSensitivityFactor = horizontal;
            verticalSensitivityFactor = vertical;
            protocol_printf("SENSITIVITY:H=%.3f:V=%.3f\n", horizontalSensitivityFactor, verticalSensitivityFactor);
            return;
        }
    }

    if (length == sizeof(float)) {
        const float value = readFloatLittleEndian(payload);
        if (std::isfinite(value) && value >= minSensitivityFactor &&
            value <= maxSensitivityFactor) {
            horizontalSensitivityFactor = value;
            verticalSensitivityFactor = horizontalSensitivityFactor;
            protocol_printf(
                "SENSITIVITY:H=%.3f:V=%.3f\n",
                horizontalSensitivityFactor,
                verticalSensitivityFactor);
            return;
        }
    }

    protocol_println("ERROR:SENSITIVITY:FORMAT");
}

static void apply_rapid_fire_payload(const uint8_t* payload, uint16_t length) {
    if (length != 3 || payload[0] > 1) {
        protocol_println("ERROR:RAPID_FIRE:FORMAT");
        return;
    }

    const bool enabled = payload[0] == 1;
    const uint16_t roundsPerMinute = readUint16LittleEndian(payload + 1);
    if (enabled && (roundsPerMinute < 60 || roundsPerMinute > 1200)) {
        protocol_println("ERROR:RAPID_FIRE:RATE");
        return;
    }
    if (enabled && activeMode == MODE_WEAPON_PATTERN) {
        protocol_println("ERROR:RAPID_FIRE:MODE");
        return;
    }

    rapidFireEnabled = enabled;
    rapidFireActive = false;
    rapidFireRoundsPerMinute = enabled ? roundsPerMinute : 0;
    set_rapid_button(false);
    reset_movement_state();
    protocol_printf("RAPID_FIRE:%s:RPM=%u\n", enabled ? "ON" : "OFF", rapidFireRoundsPerMinute);
}

static void apply_general_settings_payload(const uint8_t* payload, uint16_t length) {
    if (length != 2 || payload[0] > 1 || payload[1] > 1) {
        protocol_println("ERROR:GENERAL_SETTINGS:FORMAT");
        return;
    }

    generalTimingJitterEnabled = payload[0] == 1;
    deltaNoiseEnabled = payload[1] == 1;
    protocol_printf(
        "GENERAL_SETTINGS:TIMING_VARIANCE=%s:DELTA_NOISE=%s\n",
        generalTimingJitterEnabled ? "ON" : "OFF",
        deltaNoiseEnabled ? "ON" : "OFF");
}

static void process_command(uint8_t command, const uint8_t* payload, uint16_t length) {
    switch (command) {
        case CMD_PING:
            protocol_println("PONG:RAINBOW-RECOIL:4");
            print_hardware_identity();
            break;

        case CMD_START:
            if (configurationTransactionActive) {
                protocol_println("ERROR:START:CONFIGURATION_ACTIVE");
                break;
            }
#ifdef ZEROSENSE_RP2350_USB_C
            // RP2350 firing is gated locally from the raw downstream mouse.
            // Accepting host START here would reintroduce the synthesized-HID
            // feedback loop that local activation is designed to remove.
            protocol_println("ERROR:START:LOCAL_TRIGGER_REQUIRED");
#else
            start_output();
#endif
            break;

        case CMD_STOP:
            stop_output();
            protocol_println("STOP");
            break;

        case CMD_PROFILE:
            if (!configurationTransactionActive) {
                protocol_println("ERROR:CONFIG:TRANSACTION_REQUIRED");
                break;
            }
            configurationTransactionTouchedAtMs = millis();
            apply_profile_payload(payload, length);
            break;

        case CMD_SENSITIVITY:
            if (!configurationTransactionActive) {
                protocol_println("ERROR:CONFIG:TRANSACTION_REQUIRED");
                break;
            }
            configurationTransactionTouchedAtMs = millis();
            apply_sensitivity_payload(payload, length);
            break;

        case CMD_PATTERN:
            if (!configurationTransactionActive) {
                protocol_println("ERROR:CONFIG:TRANSACTION_REQUIRED");
                break;
            }
            configurationTransactionTouchedAtMs = millis();
            apply_pattern_payload(payload, length);
            break;

        case CMD_RAPID_FIRE:
            if (!configurationTransactionActive) {
                protocol_println("ERROR:CONFIG:TRANSACTION_REQUIRED");
                break;
            }
            configurationTransactionTouchedAtMs = millis();
            apply_rapid_fire_payload(payload, length);
            break;

        case CMD_GENERAL_SETTINGS:
            if (!configurationTransactionActive) {
                protocol_println("ERROR:CONFIG:TRANSACTION_REQUIRED");
                break;
            }
            configurationTransactionTouchedAtMs = millis();
            apply_general_settings_payload(payload, length);
            break;

        case CMD_KEEPALIVE:
            if (length != 0) {
                protocol_println("ERROR:KEEPALIVE:FORMAT");
            } else if (fireActive) {
                lastHostKeepAliveAtMs = millis();
            }
            break;

        case CMD_ARM_LEASE:
#ifdef ZEROSENSE_RP2350_USB_C
            if (length != 1 || payload[0] > 1) {
                protocol_println("ERROR:ARM_LEASE:FORMAT");
            } else if (configurationTransactionActive && payload[0] != 0) {
                protocol_println("ERROR:ARM_LEASE:CONFIGURATION_ACTIVE");
            } else if (payload[0] == 0) {
                const bool wasEnabled = rp2350ArmLeaseEnabled;
                rp2350ArmLeaseEnabled = false;
                stop_output();
                if (wasEnabled) {
                    protocol_println("ARM_LEASE:OFF");
                }
            } else if (hostCoreStalled.load(std::memory_order_acquire) ||
                       !hostMouseConnected.load(std::memory_order_acquire)) {
                rp2350ArmLeaseEnabled = false;
                stop_output();
                protocol_println("ERROR:ARM_LEASE:MOUSE_DISCONNECTED");
            } else {
                const bool wasEnabled = rp2350ArmLeaseEnabled;
                rp2350ArmLeaseEnabled = true;
                lastRp2350ArmLeaseAtMs = millis();
                lastHostKeepAliveAtMs = lastRp2350ArmLeaseAtMs;
                if (!wasEnabled) {
                    protocol_println("ARM_LEASE:ON");
                }
            }
#else
            protocol_println("ERROR:ARM_LEASE:UNSUPPORTED");
#endif
            break;

        case CMD_CONFIG_BEGIN: {
            if (length != 9 || payload[0] != configurationSchemaVersion) {
                protocol_println("ERROR:CONFIG:BEGIN_FORMAT");
                break;
            }
            if (configurationTransactionActive) {
                rollback_configuration_transaction();
            }
            stop_output();
#ifdef ZEROSENSE_RP2350_USB_C
            rp2350ArmLeaseEnabled = false;
#endif
            capture_configuration(previousConfiguration);
            configurationTransactionId = readUint32LittleEndian(payload + 1);
            configurationExpectedHash = readUint32LittleEndian(payload + 5);
            configurationTransactionTouchedAtMs = millis();
            configurationTransactionActive = true;
            protocol_printf(
                "CONFIG:BEGIN:TX=%08lX:HASH=%08lX\n",
                static_cast<unsigned long>(configurationTransactionId),
                static_cast<unsigned long>(configurationExpectedHash));
            break;
        }

        case CMD_CONFIG_COMMIT: {
            if (length != 8) {
                protocol_println("ERROR:CONFIG:COMMIT_FORMAT");
                break;
            }
            const uint32_t transactionId = readUint32LittleEndian(payload);
            const uint32_t requestedHash = readUint32LittleEndian(payload + 4);
            if (!configurationTransactionActive || transactionId != configurationTransactionId) {
                protocol_println("ERROR:CONFIG:TRANSACTION_ID");
                break;
            }
            if (activeMode == MODE_WEAPON_PATTERN &&
                loadedPatternPoints != expectedPatternPoints) {
                rollback_configuration_transaction();
                protocol_println("ERROR:CONFIG:PATTERN_INCOMPLETE");
                break;
            }
            const uint32_t actualHash = current_configuration_hash();
            if (requestedHash != configurationExpectedHash ||
                actualHash != configurationExpectedHash) {
                const uint32_t expectedHash = configurationExpectedHash;
                rollback_configuration_transaction();
                protocol_printf(
                    "ERROR:CONFIG:HASH:EXPECTED=%08lX:ACTUAL=%08lX\n",
                    static_cast<unsigned long>(expectedHash),
                    static_cast<unsigned long>(actualHash));
                break;
            }
            configurationTransactionActive = false;
            configurationTransactionId = 0;
            configurationExpectedHash = 0;
            protocol_printf(
                "CONFIG:COMMIT:TX=%08lX:HASH=%08lX\n",
                static_cast<unsigned long>(transactionId),
                static_cast<unsigned long>(actualHash));
            break;
        }

        case CMD_CONFIG_ABORT: {
            if (length != 4) {
                protocol_println("ERROR:CONFIG:ABORT_FORMAT");
                break;
            }
            const uint32_t transactionId = readUint32LittleEndian(payload);
            if (!configurationTransactionActive || transactionId != configurationTransactionId) {
                protocol_println("ERROR:CONFIG:TRANSACTION_ID");
                break;
            }
            rollback_configuration_transaction();
            protocol_printf(
                "CONFIG:ABORT:TX=%08lX\n",
                static_cast<unsigned long>(transactionId));
            break;
        }

        case CMD_STATUS:
            if (length != 0) {
                protocol_println("ERROR:STATUS:FORMAT");
            } else {
                protocol_printf(
                    "STATUS:HASH=%08lX:TX=%s:FIRE=%s\n",
                    static_cast<unsigned long>(current_configuration_hash()),
                    configurationTransactionActive ? "ACTIVE" : "IDLE",
                    fireActive ? "ON" : "OFF");
                // The startup identity lines may be emitted before the desktop
                // read loop begins. Repeat both board and downstream-mouse
                // state on every status request so Arm Output can recover
                // without requiring a physical unplug/replug event.
                print_hardware_identity();
                uint32_t hostReports = 0;
                uint32_t hostErrors = 0;
                uint32_t hostSaturations = 0;
                uint32_t hostRecoveries = 0;
                uint32_t hostQueueFailures = 0;
                uint32_t hostUnmounts = 0;
                uint32_t hostTaskAgeMs = 0;
                uint32_t pioTxTimeouts = 0;
                uint32_t pioRxFlagTimeouts = 0;
                uint32_t pioRxPacketTimeouts = 0;
                uint32_t pioFilteredDisconnects = 0;
                uint32_t hostEmpty = 0;
                uint32_t pioRxOversize = 0;
#ifdef ZEROSENSE_RP2350_USB_C
                hostReports = hostReportsReceived.load(std::memory_order_relaxed);
                hostErrors = hostDecodeErrors.load(std::memory_order_relaxed);
                hostEmpty = hostEmptyReports.load(std::memory_order_relaxed);
                pioRxOversize = pio_usb_host_rx_oversize_count();
                hostSaturations =
                    hostAccumulatorSaturations.load(std::memory_order_relaxed);
                hostRecoveries = hostReceiveRecoveries.load(std::memory_order_relaxed);
                hostQueueFailures = hostReceiveQueueFailures.load(std::memory_order_relaxed);
                hostUnmounts = hostMouseUnmounts.load(std::memory_order_relaxed);
                // Sample the heartbeat before the clock to avoid a future timestamp.
                const uint32_t hostTaskAt = hostTaskLastAtMs.load(std::memory_order_relaxed);
                hostTaskAgeMs = millis() - hostTaskAt;
                pioTxTimeouts = pio_usb_host_tx_timeout_count();
                pioRxFlagTimeouts = pio_usb_host_rx_flag_timeout_count();
                pioRxPacketTimeouts = pio_usb_host_rx_packet_timeout_count();
                pioFilteredDisconnects =
                    pio_usb_host_filtered_disconnect_count();
#endif
                const int64_t currentQueuedX =
                    static_cast<int64_t>(pendingMouseX) + pendingPhysicalMouseX;
                const int64_t currentQueuedY =
                    static_cast<int64_t>(pendingMouseY) + pendingPhysicalMouseY;
                const uint32_t currentQueuedDelta = static_cast<uint32_t>(
                    std::min<int64_t>(
                        std::max(std::abs(currentQueuedX), std::abs(currentQueuedY)),
                        UINT32_MAX));
                protocol_printf(
                    "METRICS:HID_SENT=%lu:HID_BUSY=%lu:MAX_QUEUE=%lu:"
                    "MAX_ACTIVE_GAP_US=%lu:HOST_REPORTS=%lu:"
                    "HOST_DECODE_ERRORS=%lu:HOST_SATURATIONS=%lu:USB_STOPS=%lu:"
                    "CORRECTION_LATE=%lu:MAX_CORRECTION_LATE_US=%lu:QUEUE=%lu:"
                    "REPORT_INTERVAL_US=%lu:GENERAL_DT_CLAMPS=%lu:"
                    "HOST_RECOVERIES=%lu:HOST_QUEUE_FAILURES=%lu:HOST_UNMOUNTS=%lu:"
                    "HOST_TASK_AGE_MS=%lu:CDC_DROPPED=%lu:"
                    "PIO_TX_TIMEOUTS=%lu:PIO_RX_FLAG_TIMEOUTS=%lu:"
                    "PIO_RX_PACKET_TIMEOUTS=%lu:PIO_SE0_GLITCHES=%lu:"
                    "HOST_EMPTY_REPORTS=%lu:PIO_RX_OVERSIZE=%lu\n",
                    static_cast<unsigned long>(hidReportsSent),
                    static_cast<unsigned long>(hidBusyDeferrals),
                    static_cast<unsigned long>(maximumQueuedDelta),
                    static_cast<unsigned long>(maximumActiveReportGapUs),
                    static_cast<unsigned long>(hostReports),
                    static_cast<unsigned long>(hostErrors),
                    static_cast<unsigned long>(hostSaturations),
                    static_cast<unsigned long>(upstreamDisconnectStops),
                    static_cast<unsigned long>(correctionScheduler.delayedFrames),
                    static_cast<unsigned long>(correctionScheduler.maximumLatenessUs),
                    static_cast<unsigned long>(currentQueuedDelta),
                    static_cast<unsigned long>(currentHidReportIntervalUs),
                    static_cast<unsigned long>(generalIntervalClampCount),
                    static_cast<unsigned long>(hostRecoveries),
                    static_cast<unsigned long>(hostQueueFailures),
                    static_cast<unsigned long>(hostUnmounts),
                    static_cast<unsigned long>(hostTaskAgeMs),
                    static_cast<unsigned long>(protocolOutput.droppedMessages),
                    static_cast<unsigned long>(pioTxTimeouts),
                    static_cast<unsigned long>(pioRxFlagTimeouts),
                    static_cast<unsigned long>(pioRxPacketTimeouts),
                    static_cast<unsigned long>(pioFilteredDisconnects),
                    static_cast<unsigned long>(hostEmpty),
                    static_cast<unsigned long>(pioRxOversize));
            }
            break;

        case CMD_RESET:
            protocol_println("BOOTSEL");
            service_protocol_output();
            delay(20);
            reset_usb_boot(0, 0);
            break;

        default:
            protocol_printf("ERROR:COMMAND:%02X\n", command);
            break;
    }
}

static void service_configuration_transaction() {
    if (configurationTransactionActive &&
        static_cast<uint32_t>(millis() - configurationTransactionTouchedAtMs) >
            configurationTransactionTimeoutMs) {
        const uint32_t transactionId = configurationTransactionId;
        rollback_configuration_transaction();
        protocol_printf(
            "CONFIG:ROLLBACK:TIMEOUT:TX=%08lX\n",
            static_cast<unsigned long>(transactionId));
    }
}

static void service_serial() {
    if (parserState != ParserState::MagicFirst && parserFrameStartedAtMs != 0 &&
        static_cast<uint32_t>(millis() - parserFrameStartedAtMs) > parserTimeoutMs) {
        reset_parser();
        protocol_println("ERROR:FRAME:TIMEOUT");
    }

    // Host traffic must yield to the HID/watchdog services every iteration.
    uint16_t budget = 128;
    while (budget-- > 0 && Serial.available() > 0) {
        const int value = Serial.read();
        if (value < 0) { return; }
        const uint8_t byte = static_cast<uint8_t>(value);

        switch (parserState) {
            case ParserState::MagicFirst:
                if (byte == frameMagicFirst) {
                    parserFrameStartedAtMs = millis();
                    parserState = ParserState::MagicSecond;
                }
                break;

            case ParserState::MagicSecond:
                if (byte == frameMagicSecond) {
                    parserCrc = 0xFFFF;
                    parserState = ParserState::Version;
                } else if (byte == frameMagicFirst) {
                    // A repeated first magic byte may be the beginning of a
                    // valid frame; keep it instead of discarding both bytes.
                    parserFrameStartedAtMs = millis();
                } else {
                    reset_parser();
                }
                break;

            case ParserState::Version:
                if (byte != frameVersion) {
                    reset_parser_preserving_magic(byte);
                    protocol_println("ERROR:FRAME:VERSION");
                    break;
                }
                parser_crc_byte(byte);
                parserState = ParserState::SequenceLow;
                break;

            case ParserState::SequenceLow:
                parserSequence = byte;
                parser_crc_byte(byte);
                parserState = ParserState::SequenceHigh;
                break;

            case ParserState::SequenceHigh:
                parserSequence |= static_cast<uint16_t>(byte) << 8;
                parser_crc_byte(byte);
                parserState = ParserState::Command;
                break;

            case ParserState::Command:
                parserCommand = byte;
                parser_crc_byte(byte);
                parserState = ParserState::Length;
                break;

            case ParserState::Length:
                parserLength = byte;
                parserOffset = 0;
                parser_crc_byte(byte);
                if (parserLength > maxCommandPayload) {
                    reset_parser_preserving_magic(byte);
                    protocol_println("ERROR:FRAME:TOO_LARGE");
                } else if (parserLength == 0) {
                    parserState = ParserState::CrcLow;
                } else {
                    parserState = ParserState::Payload;
                }
                break;

            case ParserState::Payload:
                parserPayload[parserOffset++] = byte;
                parser_crc_byte(byte);
                if (parserOffset == parserLength) {
                    parserPayload[parserOffset] = 0;
                    parserState = ParserState::CrcLow;
                }
                break;

            case ParserState::CrcLow:
                parserReceivedCrc = byte;
                parserState = ParserState::CrcHigh;
                break;

            case ParserState::CrcHigh: {
                parserReceivedCrc |= static_cast<uint16_t>(byte) << 8;
                if (parserReceivedCrc == parserCrc) {
                    const uint8_t command = parserCommand;
                    const uint8_t length = parserLength;
                    reset_parser();
                    process_command(command, parserPayload, length);
                } else {
                    reset_parser_preserving_magic(byte);
                    protocol_println("ERROR:FRAME:CRC");
                }
                break;
            }
        }
    }
}

static int8_t report_delta(int64_t value) {
#ifdef ZEROSENSE_RP2350_USB_C
    return static_cast<int8_t>(std::clamp<int64_t>(value, -127, 127));
#else
    return static_cast<int8_t>(std::clamp<int64_t>(value, -100, 100));
#endif
}

static int8_t random_delta_noise() {
    const int8_t magnitude = rng_next() < 0.0f ? 2 : 3;
    return rng_next() < 0.0f ? static_cast<int8_t>(-magnitude) : magnitude;
}

static bool has_delta_noise_balance() {
    return ZeroSenseDeltaNoise::hasBalance(deltaNoiseX) ||
        ZeroSenseDeltaNoise::hasBalance(deltaNoiseY);
}

struct PreparedDeltaNoise {
    int8_t transmitted;
    int8_t appliedNoise;
};

static PreparedDeltaNoise prepare_delta_noise(
    int8_t baseDelta,
    const ZeroSenseDeltaNoise::AxisState& state,
    bool introduceNoise) {
    if (!introduceNoise && !ZeroSenseDeltaNoise::hasBalance(state)) {
        return {baseDelta, 0};
    }

    const int8_t requestedNoise = ZeroSenseDeltaNoise::preferredDelta(
        state,
        introduceNoise ? random_delta_noise() : 0);
    const int8_t transmitted = report_delta(
        static_cast<int16_t>(baseDelta) + requestedNoise);
    return {
        transmitted,
        static_cast<int8_t>(transmitted - baseDelta)
    };
}

#ifdef ZEROSENSE_RP2350_USB_C
static void merge_host_delta(int32_t& pending, int32_t incoming) {
    pending = static_cast<int32_t>(std::clamp<int64_t>(
        static_cast<int64_t>(pending) + incoming,
        -maxHostAccumulator,
        maxHostAccumulator));
}

static void transfer_host_mouse_input() {
    merge_host_delta(
        pendingPhysicalMouseX,
        hostMouseX.exchange(0, std::memory_order_acq_rel));
    merge_host_delta(
        pendingPhysicalMouseY,
        hostMouseY.exchange(0, std::memory_order_acq_rel));
    merge_host_delta(
        pendingPhysicalWheel,
        hostMouseWheel.exchange(0, std::memory_order_acq_rel));
    merge_host_delta(
        pendingPhysicalPan,
        hostMousePan.exchange(0, std::memory_order_acq_rel));
}

static void cancel_opposing_deltas(int32_t& generated, int32_t& physical) {
    if (generated > 0 && physical < 0) {
        const int32_t cancellation = std::min(generated, -physical);
        generated -= cancellation;
        physical += cancellation;
    } else if (generated < 0 && physical > 0) {
        const int32_t cancellation = std::min(-generated, physical);
        generated += cancellation;
        physical -= cancellation;
    }
}

// Physical input is consumed first to minimize pass-through latency. Any
// generated remainder stays queued and is transmitted by the next HID frame.
static void consume_axis_delta(
    int32_t& generated,
    int32_t& physical,
    int8_t transmitted) {
    int32_t remaining = std::abs(static_cast<int32_t>(transmitted));
    const int32_t direction = transmitted < 0 ? -1 : 1;
    if (physical * direction > 0) {
        const int32_t amount = std::min(std::abs(physical), remaining);
        physical -= direction * amount;
        remaining -= amount;
    }
    if (remaining > 0 && generated * direction > 0) {
        const int32_t amount = std::min(std::abs(generated), remaining);
        generated -= direction * amount;
    }
}

static uint8_t current_output_buttons() {
    const uint8_t physical = hostMouseButtons.load(std::memory_order_acquire);
    if (!rapidFireActive) {
        return physical;
    }
    return static_cast<uint8_t>(
        (physical & ~MOUSE_BUTTON_LEFT) |
        (rapidButtonDown ? MOUSE_BUTTON_LEFT : 0));
}

static void service_host_core_watchdog() {
    const uint32_t last = hostTaskLastAtMs.load(std::memory_order_acquire);
    if (!ZeroSenseHostHealth::stalled(last, millis(), 500) ||
        hostCoreStalled.exchange(true, std::memory_order_acq_rel)) {
        return;
    }

    // Core 1 is no longer servicing the mouse. Release any cached physical
    // inputs so the PC does not retain a stuck click, and disarm output.
    // Restarting core 1 here could interrupt PIO IRQ/DMA in an unknown state.
    hostMouseButtons.store(0, std::memory_order_release);
    hostMouseX.store(0, std::memory_order_release);
    hostMouseY.store(0, std::memory_order_release);
    hostMouseWheel.store(0, std::memory_order_release);
    hostMousePan.store(0, std::memory_order_release);
    pendingPhysicalMouseX = 0;
    pendingPhysicalMouseY = 0;
    pendingPhysicalWheel = 0;
    pendingPhysicalPan = 0;
    hidStateDirty = true;
    hostMouseFaultPending.store(true, std::memory_order_release);
    mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
}

static void service_mouse_proxy_status() {
    if (hostMouseFaultPending.exchange(false, std::memory_order_acq_rel)) {
        rp2350ArmLeaseEnabled = false;
        stop_output();
    }

    switch (mouseProxyEvent.exchange(MouseProxyEvent::None, std::memory_order_acq_rel)) {
        case MouseProxyEvent::Connected:
            protocol_printf(
                "MOUSE:CONNECTED:VID=%04X:PID=%04X\n",
                hostMouseVid.load(std::memory_order_acquire),
                hostMousePid.load(std::memory_order_acquire));
            break;
        case MouseProxyEvent::Disconnected:
            protocol_println("MOUSE:DISCONNECTED");
            break;
        case MouseProxyEvent::Unsupported:
            protocol_println("MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR");
            break;
        case MouseProxyEvent::HostError:
            protocol_println("MOUSE:HOST_ERROR");
            break;
        case MouseProxyEvent::None:
            break;
    }
}

static void service_rp2350_local_activation() {
    const uint8_t rawButtons = hostMouseButtons.load(std::memory_order_acquire);
    const bool rawAimAndFire =
        (rawButtons & (MOUSE_BUTTON_LEFT | MOUSE_BUTTON_RIGHT)) ==
        (MOUSE_BUTTON_LEFT | MOUSE_BUTTON_RIGHT);

    if (!rawAimAndFire) {
        rp2350TriggerLatched = false;
    }

    const uint32_t now = millis();
    const bool mouseReady = hostMouseConnected.load(std::memory_order_acquire) &&
        !hostCoreStalled.load(std::memory_order_acquire);
    const bool leaseFresh = rp2350ArmLeaseEnabled &&
        static_cast<uint32_t>(now - lastRp2350ArmLeaseAtMs) <=
            rp2350ArmLeaseTimeoutMs;

    if (!mouseReady || !leaseFresh) {
        if (rp2350ArmLeaseEnabled && !leaseFresh) {
            rp2350ArmLeaseEnabled = false;
            protocol_println("ARM_LEASE:EXPIRED");
        }
        if (fireActive) {
            stop_output();
            protocol_println(mouseReady ? "STOP:ARM_LEASE" : "STOP:MOUSE_DISCONNECTED");
        }
        return;
    }

    if (rawAimAndFire && !rp2350TriggerLatched) {
        rp2350TriggerLatched = true;
        start_output();
    } else if (!rawAimAndFire && fireActive) {
        stop_output();
        protocol_println("STOP:PHYSICAL_RELEASE");
    }
}
#else
static uint8_t current_output_buttons() {
    return rapidButtonDown ? MOUSE_BUTTON_LEFT : 0;
}
#endif

// Inputs are HID counts per reference 8 ms movement step. General-mode
// acceleration, friction, and displacement are normalized to the actual
// elapsed integration interval. Sensitivity is applied exactly once here.
static void queue_mouse_movement(
    float requested_dx,
    float requested_dy,
    bool apply_smoothing,
    uint32_t intervalUs) {
    const float scaledX = requested_dx * horizontalSensitivityFactor;
    const float scaledY = requested_dy * verticalSensitivityFactor;
    const float timeScale = ZeroSenseMotion::intervalScale(intervalUs);

    if (apply_smoothing) {
        const float maxX = ZeroSenseMotion::calibratedLimit(
            sim.maxVelocity, horizontalSensitivityFactor);
        const float maxY = ZeroSenseMotion::calibratedLimit(
            sim.maxVelocity, verticalSensitivityFactor);
        const float targetX = std::clamp(scaledX, -maxX, maxX);
        const float targetY = std::clamp(scaledY, -maxY, maxY);
        const float maximumStepX = ZeroSenseMotion::calibratedLimit(
            sim.acceleration, horizontalSensitivityFactor) * timeScale;
        const float maximumStepY = ZeroSenseMotion::calibratedLimit(
            sim.acceleration, verticalSensitivityFactor) * timeScale;
        const float friction = ZeroSenseMotion::frictionForInterval(
            sim.friction,
            timeScale);
        sim.velocityX = targetX == 0.0f
            ? sim.velocityX * friction
            : ZeroSenseMotion::approach(sim.velocityX, targetX, maximumStepX);
        sim.velocityY = targetY == 0.0f
            ? sim.velocityY * friction
            : ZeroSenseMotion::approach(sim.velocityY, targetY, maximumStepY);
    } else {
        sim.velocityX = scaledX;
        sim.velocityY = scaledY;
    }
    sim.accelerating = std::abs(sim.velocityX) >= 0.01f ||
        std::abs(sim.velocityY) >= 0.01f;

    fractionalMouseX += sim.velocityX * timeScale;
    fractionalMouseY += sim.velocityY * timeScale;

    const int32_t queuedX = static_cast<int32_t>(fractionalMouseX);
    const int32_t queuedY = static_cast<int32_t>(fractionalMouseY);
    fractionalMouseX -= static_cast<float>(queuedX);
    fractionalMouseY -= static_cast<float>(queuedY);
    pendingMouseX += queuedX;
    pendingMouseY += queuedY;
}

static void schedule_pattern_correction(
    float requestedDx,
    float requestedDy,
    uint32_t shotIntervalUs) {
    // The reusable scheduler owns the fixed-point remainder and absolute
    // deadlines so native fake-clock tests exercise this exact code path.
    ZeroSenseCorrection::schedule(
        correctionScheduler,
        requestedDx * horizontalSensitivityFactor,
        requestedDy * verticalSensitivityFactor,
        shotIntervalUs,
        micros());
}

static void service_scheduled_correction() {
    if (correctionScheduler.frames == 0) {
        return;
    }

    const uint32_t now = micros();
    const auto result = ZeroSenseCorrection::service(correctionScheduler, now);
    pendingMouseX += result.queuedX;
    pendingMouseY += result.queuedY;

    if (correctionScheduler.frames == 0) {
        if (fireActive && !rapidFireActive &&
            activeMode == MODE_WEAPON_PATTERN &&
            shotsInBurst >= loadedPatternPoints) {
            complete_pattern_output();
        }
    }
}

static void service_hid() {
#ifdef ZEROSENSE_RP2350_USB_C
    transfer_host_mouse_input();
    cancel_opposing_deltas(pendingMouseX, pendingPhysicalMouseX);
    cancel_opposing_deltas(pendingMouseY, pendingPhysicalMouseY);
#endif
    static uint8_t lastSentButtons = 0;
    const uint8_t buttons = current_output_buttons();
    if (pendingMouseX == 0 && pendingMouseY == 0 &&
        pendingPhysicalMouseX == 0 && pendingPhysicalMouseY == 0 &&
        pendingPhysicalWheel == 0 && pendingPhysicalPan == 0 &&
        buttons == lastSentButtons && !hidStateDirty &&
        !has_delta_noise_balance()) {
        if (!fireActive && !rapidFireActive) {
            lastHighActivityReportAtUs = 0;
        }
        return;
    }

    if (TinyUSBDevice.suspended()) {
        TinyUSBDevice.remoteWakeup();
    }

    // Both targets use the same 250/500/1000 Hz policy. On RP2350, any queued
    // physical movement, wheel, pan, or button transition immediately selects
    // the 1 ms endpoint, so adaptive idle pacing cannot add pass-through lag.
    bool physical_activity = false;
#ifdef ZEROSENSE_RP2350_USB_C
    physical_activity = pendingPhysicalMouseX != 0 || pendingPhysicalMouseY != 0 ||
        pendingPhysicalWheel != 0 || pendingPhysicalPan != 0 ||
        buttons != lastSentButtons || hidStateDirty;
#endif
    uint32_t poll_interval_us = POLL_NORMAL_US;
    const bool noise_balance_pending = has_delta_noise_balance();
    const bool is_idle = !fireActive && !rapidFireActive && !physical_activity &&
        !noise_balance_pending;
    const bool generated_activity = pendingMouseX != 0 || pendingMouseY != 0 ||
        correctionScheduler.frames > 0 || noise_balance_pending;
    const bool is_high_activity = rapidFireActive || sim.accelerating ||
        generated_activity || physical_activity;

    if (is_idle) {
        poll_interval_us = POLL_IDLE_US; // Reduce packet rate during inactivity
    } else if (is_high_activity) {
        poll_interval_us = POLL_HIGH_US;
    }
    currentHidReportIntervalUs = poll_interval_us;

    const uint32_t now = micros();
    if (static_cast<uint32_t>(now - lastHidReportAtUs) < poll_interval_us ||
        !TinyUSBDevice.mounted()) {
        return;
    }

    if (!usbHid.ready()) {
        ++hidBusyDeferrals;
        return;
    }

    const int64_t combinedX = static_cast<int64_t>(pendingMouseX) + pendingPhysicalMouseX;
    const int64_t combinedY = static_cast<int64_t>(pendingMouseY) + pendingPhysicalMouseY;
    const uint32_t queuedMagnitude = static_cast<uint32_t>(std::min<int64_t>(
        std::max(std::abs(combinedX), std::abs(combinedY)),
        UINT32_MAX));
    maximumQueuedDelta = std::max(maximumQueuedDelta, queuedMagnitude);

    const int8_t baseDx = report_delta(combinedX);
    const int8_t baseDy = report_delta(combinedY);
    const bool generatedDeltaReady = pendingMouseX != 0 || pendingMouseY != 0;
    const bool introduceNoise = deltaNoiseEnabled && generatedDeltaReady;
    const auto noisyX = prepare_delta_noise(baseDx, deltaNoiseX, introduceNoise);
    const auto noisyY = prepare_delta_noise(baseDy, deltaNoiseY, introduceNoise);
    const int8_t dx = noisyX.transmitted;
    const int8_t dy = noisyY.transmitted;
    const int8_t wheel = report_delta(pendingPhysicalWheel);
    const int8_t pan = report_delta(pendingPhysicalPan);

    if (usbHid.mouseReport(0, buttons, dx, dy, wheel, pan)) {
        ZeroSenseDeltaNoise::recordAppliedDelta(deltaNoiseX, noisyX.appliedNoise);
        ZeroSenseDeltaNoise::recordAppliedDelta(deltaNoiseY, noisyY.appliedNoise);
#ifdef ZEROSENSE_RP2350_USB_C
        consume_axis_delta(pendingMouseX, pendingPhysicalMouseX, baseDx);
        consume_axis_delta(pendingMouseY, pendingPhysicalMouseY, baseDy);
#else
        pendingMouseX -= baseDx;
        pendingMouseY -= baseDy;
#endif
        pendingPhysicalWheel -= wheel;
        pendingPhysicalPan -= pan;
        lastSentButtons = buttons;
        hidStateDirty = false;
        ++hidReportsSent;
        if (is_high_activity) {
            if (lastHighActivityReportAtUs != 0) {
                maximumActiveReportGapUs = std::max(
                    maximumActiveReportGapUs,
                    static_cast<uint32_t>(now - lastHighActivityReportAtUs));
            }
            lastHighActivityReportAtUs = now;
        } else {
            lastHighActivityReportAtUs = 0;
        }
        lastHidReportAtUs = now;
    }
}

// Generate one configured recoil step.
static void generate_movement(uint32_t shotIntervalUs) {
    float vertical;
    float horizontal;

    if (activeMode == MODE_WEAPON_PATTERN) {
        if (shotsInBurst >= loadedPatternPoints) {
            if (correctionScheduler.frames == 0) {
                complete_pattern_output();
            }
            return;
        }
        // Pattern values are stored as Q8.8 fixed-point (int16 / 256.0f)
        horizontal = static_cast<float>(patternHorizontal[shotsInBurst]) / 256.0f;
        vertical = static_cast<float>(patternVertical[shotsInBurst]) / 256.0f;
    } else {
        vertical = activeVerticalCompensation;
        horizontal = activeHorizontalCompensation;
    }

    // Burst progression is defined per shot. General continuous mode has no
    // reliable bullet clock, so apply it only when rapid-fire owns that clock.
    if (activeMode == MODE_GENERAL && rapidFireActive &&
        shotsInBurst > 0 && activeBurstProgression > 0) {
        const float reduction = 1.0f - (static_cast<float>(shotsInBurst) * activeBurstProgression / 100.0f);
        vertical *= std::max(0.5f, reduction);
        horizontal *= std::max(0.5f, reduction);
    }

    // General values are velocities in counts per reference 8 ms. Rapid-fire
    // scheduling needs a total displacement for one complete shot interval.
    if (activeMode == MODE_GENERAL && rapidFireActive) {
        const float shotScale = ZeroSenseMotion::shotIntervalScale(shotIntervalUs);
        horizontal *= shotScale;
        vertical *= shotScale;
    }

    if (activeMode == MODE_WEAPON_PATTERN || rapidFireActive) {
        schedule_pattern_correction(horizontal, vertical, shotIntervalUs);
    } else {
        queue_mouse_movement(horizontal, vertical, true, shotIntervalUs);
    }

    ++shotsInBurst;
}

// Service recoil movement using actual elapsed time in General mode and
// phase-locked timing for weapon patterns.
static void service_movement() {
    if (!fireActive || rapidFireActive) {
        return;
    }

    const uint32_t now = micros();
    if (static_cast<int32_t>(now - nextMovementAtUs) >= 0) {
        const uint32_t scheduledInterval = activeMode == MODE_WEAPON_PATTERN
            ? next_rpm_interval(activeRoundsPerMinute, movementIntervalRemainder)
            : get_jittered_interval(8000UL);
        const uint32_t integrationInterval = activeMode == MODE_WEAPON_PATTERN
            ? scheduledInterval
            : std::max<uint32_t>(
                static_cast<uint32_t>(now - lastMovementIntegrationAtUs),
                1U);
        if (activeMode == MODE_GENERAL &&
            (integrationInterval < 2000U || integrationInterval > 32000U)) {
            ++generalIntervalClampCount;
        }
        generate_movement(integrationInterval);
        lastMovementIntegrationAtUs = now;
        if (!fireActive) {
            return;
        }

        nextMovementAtUs += scheduledInterval;
        if (static_cast<int32_t>(now - nextMovementAtUs) >= 0) {
            nextMovementAtUs = now + scheduledInterval;
        }
    }
}

// Service rapid fire with an exact, phase-locked RPM interval.
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

    // Schedule the next shot from the prior deadline to avoid cumulative drift.
    if (!rapidButtonDown && static_cast<int32_t>(now - nextRapidShotAtUs) >= 0) {
        set_rapid_button(true);
        const uint32_t shot_interval = next_rpm_interval(
            rapidFireRoundsPerMinute,
            rapidIntervalRemainder);
        generate_movement(shot_interval);
        if (!fireActive) {
            return;
        }
        rapidButtonReleaseAtUs = now + 8000;

        nextRapidShotAtUs += shot_interval;

        if (static_cast<int32_t>(now - nextRapidShotAtUs) >= 0) {
            nextRapidShotAtUs = now + shot_interval;
        }
    }
}

// Stop output if the host stops sending keepalives.
static void service_host_watchdog() {
    if (fireActive && millis() - lastHostKeepAliveAtMs > hostWatchdogTimeoutMs) {
        stop_output();
        protocol_println("STOP:WATCHDOG");
    }
}

// Never retain generated movement across loss of the upstream PC connection.
// RP2350 also revokes the host arm lease and requires a fresh physical button
// transition, while leaving downstream physical input state untouched.
static void service_upstream_usb_fail_safe() {
    if (TinyUSBDevice.mounted()) {
        return;
    }
#ifdef ZEROSENSE_RP2350_USB_C
    if (rp2350ArmLeaseEnabled || fireActive) {
        rp2350TriggerLatched = true;
    }
    rp2350ArmLeaseEnabled = false;
#endif
    if (fireActive) {
        ++upstreamDisconnectStops;
        stop_output();
    }
}

void setup() {
#ifdef ZEROSENSE_RP2350_USB_C
    TinyUSBDevice.setManufacturerDescriptor("Waveshare");
    TinyUSBDevice.setProductDescriptor("ZeroSense RP2350 Mouse Proxy");
#else
    TinyUSBDevice.setManufacturerDescriptor("RP2040");
    TinyUSBDevice.setProductDescriptor("RP2040 USB Mouse");
#endif

    if (!TinyUSBDevice.isInitialized()) {
        TinyUSBDevice.begin(0);
    }

    Serial.begin(115200);

    usbHid.setBootProtocol(HID_ITF_PROTOCOL_MOUSE);
    // The endpoint permits 1 ms reports; service_hid applies the 1/2/4 ms pacing.
    usbHid.setPollInterval(1);
    usbHid.setReportDescriptor(hidReportDescriptor, sizeof(hidReportDescriptor));

#ifdef ZEROSENSE_RP2350_USB_C
    usbHid.setStringDescriptor("ZeroSense Mouse Proxy");
#else
    usbHid.setStringDescriptor("USB Mouse");
#endif
    usbHid.begin();

    if (TinyUSBDevice.mounted()) {
        TinyUSBDevice.detach();
        delay(10);
        TinyUSBDevice.attach();
    }

#ifdef ZEROSENSE_RP2350_USB_C
    protocol_println("READY:RP2350-USB-C:MOUSE-PROXY");
#else
    protocol_println("READY:RP2040-ZERO:CDC+HID");
#endif
    print_hardware_identity();
}

void loop() {
#ifdef TINYUSB_NEED_POLLING_TASK
    TinyUSBDevice.task();
#endif
    service_serial();
    service_configuration_transaction();
    service_upstream_usb_fail_safe();
#ifdef ZEROSENSE_RP2350_USB_C
    service_host_core_watchdog();
    service_mouse_proxy_status();
    service_rp2350_local_activation();
#endif
    service_host_watchdog();
    service_rapid_fire();
    service_movement();
    service_scheduled_correction();
    service_hid();
    service_protocol_output();
    // Send a zero-delta heartbeat while active but stationary. Never synthesize
    // movement or alter a pressed rapid-fire button state in this path.
    if (pendingMouseX == 0 && pendingMouseY == 0 && !hidStateDirty &&
        fireActive && !rapidButtonDown) {
        const uint32_t now = millis();
        static uint32_t last_idle_report = 0;
        if (now - last_idle_report >= 20U &&
            TinyUSBDevice.mounted() && usbHid.ready()) {
            if (usbHid.mouseReport(0, current_output_buttons(), 0, 0, 0, 0)) {
                hidStateDirty = false;
                last_idle_report = now;
                lastHidReportAtUs = micros();
            }
        }
    }
    delay(1);
}

#ifdef ZEROSENSE_RP2350_USB_C
void setup1() {
    static_assert(
        F_CPU == 120000000L || F_CPU == 240000000L,
        "Pico-PIO-USB requires a 120 MHz or 240 MHz system clock.");

    // Report protocol preserves wheel, pan, extra buttons, and high-resolution
    // fields. If a descriptor cannot be parsed, the mount callback falls back
    // to the standardized three-byte boot-mouse protocol.
    tuh_hid_set_default_protocol(HID_PROTOCOL_REPORT);
    pio_usb_configuration_t configuration = PIO_USB_DEFAULT_CONFIG;
    configuration.pin_dp = hostMouseDpPin;
    configuration.pinout = PIO_USB_PINOUT_DPDM;
    if (!usbHost.configure_pio_usb(1, &configuration) || !usbHost.begin(1)) {
        hostMouseFaultPending.store(true, std::memory_order_release);
        mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
        return;
    }
    hostStackStarted = true;
}

void loop1() {
    if (hostStackStarted) {
        // Never wait forever here: a lost interrupt request otherwise leaves
        // CDC alive on core 0 while core 1 can no longer recover mouse input.
        usbHost.task(1);
        service_host_mouse_receive_recovery();
        hostTaskLastAtMs.store(millis(), std::memory_order_relaxed);
    } else {
        delay(1);
    }
}
#endif
