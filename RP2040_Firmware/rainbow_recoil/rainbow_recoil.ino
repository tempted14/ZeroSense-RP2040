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
#include "pico/bootrom.h"

#ifdef ZEROSENSE_RP2350_USB_C
#include "pio_usb.h"
#endif

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

// General-mode cadence variation. Pattern and rapid-fire timing use exact RPM
// intervals so their configured shot sequence does not drift.
static constexpr float TIMING_JITTER_PCT = 8.0f;   // ±8% variance on intervals

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
static int32_t pendingMouseX = 0;
static int32_t pendingMouseY = 0;
static int32_t pendingPhysicalMouseX = 0;
static int32_t pendingPhysicalMouseY = 0;
static int32_t pendingPhysicalWheel = 0;
static int32_t pendingPhysicalPan = 0;
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
static constexpr uint8_t maxHostMouseLayouts = 8;
static constexpr uint8_t maxHostMouseFields = 16;
static constexpr int32_t maxHostAccumulator = 32767;

enum class HostMouseFieldKind : uint8_t {
    Button,
    X,
    Y,
    Wheel,
    Pan
};

struct HostMouseField {
    uint16_t bitOffset;
    uint8_t bitSize;
    HostMouseFieldKind kind;
    uint8_t buttonMask;
    bool isSigned;
};

struct HostMouseLayout {
    bool used;
    uint8_t reportId;
    uint8_t fieldCount;
    HostMouseField fields[maxHostMouseFields];
};

struct HidGlobalState {
    uint16_t usagePage;
    int32_t logicalMinimum;
    uint8_t reportSize;
    uint8_t reportCount;
    uint8_t reportId;
};

enum class MouseProxyEvent : uint8_t {
    None,
    Connected,
    Disconnected,
    Unsupported,
    HostError
};

static Adafruit_USBH_Host usbHost;
static HostMouseLayout hostMouseLayouts[maxHostMouseLayouts] = {};
static uint8_t activeHostDevice = 0;
static uint8_t activeHostInstance = 0;
static bool hostMouseUsesBootProtocol = false;
static bool descriptorHadMouseApplication = false;
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

static int32_t signed_hid_value(uint32_t value, uint8_t bitSize) {
    if (bitSize == 0 || bitSize >= 32) {
        return static_cast<int32_t>(value);
    }
    const uint32_t signBit = 1UL << (bitSize - 1);
    const uint32_t mask = (1UL << bitSize) - 1UL;
    value &= mask;
    return (value & signBit) != 0
        ? static_cast<int32_t>(value | ~mask)
        : static_cast<int32_t>(value);
}

static uint32_t read_hid_bits(
    const uint8_t* report,
    uint16_t reportLength,
    uint16_t bitOffset,
    uint8_t bitSize,
    bool* valid) {
    if (bitSize == 0 || bitSize > 32 ||
        static_cast<uint32_t>(bitOffset) + bitSize >
            static_cast<uint32_t>(reportLength) * 8U) {
        *valid = false;
        return 0;
    }

    uint32_t value = 0;
    for (uint8_t bit = 0; bit < bitSize; ++bit) {
        const uint16_t sourceBit = bitOffset + bit;
        if ((report[sourceBit / 8] & (1U << (sourceBit % 8))) != 0) {
            value |= 1UL << bit;
        }
    }
    return value;
}

static HostMouseLayout* find_or_add_host_layout(uint8_t reportId) {
    for (auto& layout : hostMouseLayouts) {
        if (layout.used && layout.reportId == reportId) {
            return &layout;
        }
    }
    for (auto& layout : hostMouseLayouts) {
        if (!layout.used) {
            layout.used = true;
            layout.reportId = reportId;
            layout.fieldCount = 0;
            return &layout;
        }
    }
    return nullptr;
}

static bool add_host_mouse_field(
    uint8_t reportId,
    uint16_t bitOffset,
    uint8_t bitSize,
    HostMouseFieldKind kind,
    uint8_t buttonMask,
    bool isSigned) {
    HostMouseLayout* layout = find_or_add_host_layout(reportId);
    if (layout == nullptr || layout->fieldCount >= maxHostMouseFields ||
        bitSize == 0 || bitSize > 32) {
        return false;
    }
    layout->fields[layout->fieldCount++] = {
        bitOffset,
        bitSize,
        kind,
        buttonMask,
        isSigned
    };
    return true;
}

static uint32_t hid_item_value(const uint8_t* data, uint8_t size) {
    uint32_t value = 0;
    for (uint8_t index = 0; index < size; ++index) {
        value |= static_cast<uint32_t>(data[index]) << (index * 8);
    }
    return value;
}

static int32_t hid_item_signed_value(const uint8_t* data, uint8_t size) {
    return signed_hid_value(hid_item_value(data, size), size * 8);
}

static uint32_t qualified_usage(uint16_t usagePage, uint32_t usage) {
    return usage > 0xffffU
        ? usage
        : (static_cast<uint32_t>(usagePage) << 16) | usage;
}

// Parse the standard HID short-item format so report-ID, 16-bit movement, and
// ordinary gaming-mouse descriptors work without assuming a fixed byte layout.
static bool parse_host_mouse_descriptor(
    const uint8_t* descriptor,
    uint16_t descriptorLength) {
    memset(hostMouseLayouts, 0, sizeof(hostMouseLayouts));
    descriptorHadMouseApplication = false;
    if (descriptor == nullptr || descriptorLength == 0) {
        return false;
    }

    HidGlobalState global = {};
    HidGlobalState globalStack[4] = {};
    uint8_t globalDepth = 0;
    uint16_t bitOffsets[256] = {};
    uint32_t usages[24] = {};
    uint8_t usageCount = 0;
    uint32_t usageMinimum = 0;
    uint32_t usageMaximum = 0;
    bool hasUsageMinimum = false;
    bool hasUsageMaximum = false;
    bool mouseCollectionStack[12] = {};
    uint8_t collectionDepth = 0;
    bool parsedField = false;

    const auto clearLocals = [&]() {
        usageCount = 0;
        usageMinimum = 0;
        usageMaximum = 0;
        hasUsageMinimum = false;
        hasUsageMaximum = false;
    };

    uint16_t offset = 0;
    while (offset < descriptorLength) {
        const uint8_t prefix = descriptor[offset++];
        if (prefix == 0xfe) {
            if (offset + 2 > descriptorLength) {
                return false;
            }
            const uint8_t longSize = descriptor[offset];
            offset += 2;
            if (offset + longSize > descriptorLength) {
                return false;
            }
            offset += longSize;
            continue;
        }

        uint8_t itemSize = prefix & 0x03U;
        itemSize = itemSize == 3 ? 4 : itemSize;
        if (offset + itemSize > descriptorLength) {
            return false;
        }
        const uint8_t itemType = (prefix >> 2) & 0x03U;
        const uint8_t itemTag = (prefix >> 4) & 0x0fU;
        const uint32_t value = hid_item_value(descriptor + offset, itemSize);
        const int32_t signedValue = hid_item_signed_value(descriptor + offset, itemSize);
        offset += itemSize;

        if (itemType == 1) {
            switch (itemTag) {
                case 0: global.usagePage = static_cast<uint16_t>(value); break;
                case 1: global.logicalMinimum = signedValue; break;
                case 7: global.reportSize = static_cast<uint8_t>(value); break;
                case 8: global.reportId = static_cast<uint8_t>(value); break;
                case 9: global.reportCount = static_cast<uint8_t>(value); break;
                case 10:
                    if (globalDepth < 4) {
                        globalStack[globalDepth++] = global;
                    }
                    break;
                case 11:
                    if (globalDepth > 0) {
                        global = globalStack[--globalDepth];
                    }
                    break;
                default: break;
            }
            continue;
        }

        if (itemType == 2) {
            switch (itemTag) {
                case 0:
                    if (usageCount < 24) {
                        usages[usageCount++] = qualified_usage(global.usagePage, value);
                    }
                    break;
                case 1:
                    usageMinimum = qualified_usage(global.usagePage, value);
                    hasUsageMinimum = true;
                    break;
                case 2:
                    usageMaximum = qualified_usage(global.usagePage, value);
                    hasUsageMaximum = true;
                    break;
                default: break;
            }
            continue;
        }

        if (itemType != 0) {
            continue;
        }

        const uint32_t firstUsage = usageCount > 0
            ? usages[0]
            : hasUsageMinimum ? usageMinimum : 0;
        if (itemTag == 10) {
            const bool parentIsMouse = collectionDepth > 0 &&
                mouseCollectionStack[collectionDepth - 1];
            const bool isMouseApplication = value == HID_COLLECTION_APPLICATION &&
                firstUsage == ((static_cast<uint32_t>(HID_USAGE_PAGE_DESKTOP) << 16) |
                    HID_USAGE_DESKTOP_MOUSE);
            descriptorHadMouseApplication = descriptorHadMouseApplication ||
                isMouseApplication;
            if (collectionDepth < 12) {
                mouseCollectionStack[collectionDepth++] =
                    parentIsMouse || isMouseApplication;
            }
            clearLocals();
            continue;
        }
        if (itemTag == 12) {
            if (collectionDepth > 0) {
                --collectionDepth;
            }
            clearLocals();
            continue;
        }

        if (itemTag == 8) {
            const uint16_t startingBit = bitOffsets[global.reportId];
            const bool inMouseCollection = collectionDepth > 0 &&
                mouseCollectionStack[collectionDepth - 1];
            const bool isData = (value & HID_CONSTANT) == 0;
            const bool isVariable = (value & HID_VARIABLE) != 0;
            const bool isRelative = (value & HID_RELATIVE) != 0;

            if (inMouseCollection && isData && isVariable) {
                for (uint8_t index = 0; index < global.reportCount; ++index) {
                    uint32_t usage = 0;
                    if (index < usageCount) {
                        usage = usages[index];
                    } else if (hasUsageMinimum && hasUsageMaximum &&
                        usageMinimum + index <= usageMaximum) {
                        usage = usageMinimum + index;
                    } else if (usageCount > 0) {
                        usage = usages[usageCount - 1];
                    }

                    const uint16_t page = static_cast<uint16_t>(usage >> 16);
                    const uint16_t id = static_cast<uint16_t>(usage);
                    const uint16_t fieldOffset = startingBit +
                        static_cast<uint16_t>(index) * global.reportSize;
                    bool added = false;
                    if (page == HID_USAGE_PAGE_BUTTON && id >= 1 && id <= 8) {
                        added = add_host_mouse_field(
                            global.reportId,
                            fieldOffset,
                            global.reportSize,
                            HostMouseFieldKind::Button,
                            static_cast<uint8_t>(1U << (id - 1)),
                            false);
                    } else if (page == HID_USAGE_PAGE_DESKTOP && isRelative) {
                        HostMouseFieldKind kind;
                        bool supported = true;
                        if (id == HID_USAGE_DESKTOP_X) {
                            kind = HostMouseFieldKind::X;
                        } else if (id == HID_USAGE_DESKTOP_Y) {
                            kind = HostMouseFieldKind::Y;
                        } else if (id == HID_USAGE_DESKTOP_WHEEL) {
                            kind = HostMouseFieldKind::Wheel;
                        } else {
                            supported = false;
                        }
                        if (supported) {
                            added = add_host_mouse_field(
                                global.reportId,
                                fieldOffset,
                                global.reportSize,
                                kind,
                                0,
                                global.logicalMinimum < 0);
                        }
                    } else if (page == HID_USAGE_PAGE_CONSUMER &&
                        id == HID_USAGE_CONSUMER_AC_PAN && isRelative) {
                        added = add_host_mouse_field(
                            global.reportId,
                            fieldOffset,
                            global.reportSize,
                            HostMouseFieldKind::Pan,
                            0,
                            global.logicalMinimum < 0);
                    }
                    parsedField = parsedField || added;
                }
            }

            bitOffsets[global.reportId] = startingBit +
                static_cast<uint16_t>(global.reportSize) * global.reportCount;
        }
        clearLocals();
    }

    return parsedField;
}

static void configure_boot_mouse_layout() {
    memset(hostMouseLayouts, 0, sizeof(hostMouseLayouts));
    for (uint8_t button = 0; button < 8; ++button) {
        add_host_mouse_field(
            0,
            button,
            1,
            HostMouseFieldKind::Button,
            static_cast<uint8_t>(1U << button),
            false);
    }
    add_host_mouse_field(0, 8, 8, HostMouseFieldKind::X, 0, true);
    add_host_mouse_field(0, 16, 8, HostMouseFieldKind::Y, 0, true);
}

static void add_host_accumulator(std::atomic<int32_t>& accumulator, int32_t value) {
    int32_t current = accumulator.load(std::memory_order_relaxed);
    while (true) {
        const int32_t next = static_cast<int32_t>(std::clamp<int64_t>(
            static_cast<int64_t>(current) + value,
            -maxHostAccumulator,
            maxHostAccumulator));
        if (accumulator.compare_exchange_weak(
                current,
                next,
                std::memory_order_release,
                std::memory_order_relaxed)) {
            return;
        }
    }
}

static bool decode_host_mouse_report(const uint8_t* report, uint16_t length) {
    if (report == nullptr || length == 0) {
        return false;
    }

    const HostMouseLayout* layout = nullptr;
    const uint8_t* payload = report;
    uint16_t payloadLength = length;
    for (const auto& candidate : hostMouseLayouts) {
        if (!candidate.used) {
            continue;
        }
        if (candidate.reportId == 0) {
            if (layout == nullptr) {
                layout = &candidate;
            }
        } else if (report[0] == candidate.reportId) {
            layout = &candidate;
            payload = report + 1;
            payloadLength = length - 1;
            break;
        }
    }
    if (layout == nullptr) {
        return false;
    }

    uint8_t buttons = 0;
    int32_t deltaX = 0;
    int32_t deltaY = 0;
    int32_t wheel = 0;
    int32_t pan = 0;
    for (uint8_t index = 0; index < layout->fieldCount; ++index) {
        const HostMouseField& field = layout->fields[index];
        bool valid = true;
        const uint32_t raw = read_hid_bits(
            payload,
            payloadLength,
            field.bitOffset,
            field.bitSize,
            &valid);
        if (!valid) {
            return false;
        }
        const int32_t value = field.isSigned
            ? signed_hid_value(raw, field.bitSize)
            : static_cast<int32_t>(raw);
        switch (field.kind) {
            case HostMouseFieldKind::Button:
                if (value != 0) {
                    buttons |= field.buttonMask;
                }
                break;
            case HostMouseFieldKind::X: deltaX += value; break;
            case HostMouseFieldKind::Y: deltaY += value; break;
            case HostMouseFieldKind::Wheel: wheel += value; break;
            case HostMouseFieldKind::Pan: pan += value; break;
        }
    }

    hostMouseButtons.store(buttons, std::memory_order_release);
    add_host_accumulator(hostMouseX, deltaX);
    add_host_accumulator(hostMouseY, deltaY);
    add_host_accumulator(hostMouseWheel, wheel);
    add_host_accumulator(hostMousePan, pan);
    return true;
}

static void clear_host_mouse_state() {
    hostMouseX.store(0, std::memory_order_release);
    hostMouseY.store(0, std::memory_order_release);
    hostMouseWheel.store(0, std::memory_order_release);
    hostMousePan.store(0, std::memory_order_release);
    hostMouseButtons.store(0, std::memory_order_release);
    hostMouseConnected.store(false, std::memory_order_release);
    hostMouseUsesBootProtocol = false;
    activeHostDevice = 0;
    activeHostInstance = 0;
    memset(hostMouseLayouts, 0, sizeof(hostMouseLayouts));
}

extern "C" {
void tuh_hid_mount_cb(
    uint8_t deviceAddress,
    uint8_t instance,
    const uint8_t* reportDescriptor,
    uint16_t descriptorLength) {
    if (activeHostDevice != 0) {
        return;
    }

    const uint8_t protocol = tuh_hid_interface_protocol(deviceAddress, instance);
    const bool parsed = parse_host_mouse_descriptor(reportDescriptor, descriptorLength);
    if (!parsed) {
        if (protocol == HID_ITF_PROTOCOL_MOUSE &&
            tuh_hid_set_protocol(deviceAddress, instance, HID_PROTOCOL_BOOT)) {
            activeHostDevice = deviceAddress;
            activeHostInstance = instance;
            hostMouseUsesBootProtocol = true;
            return;
        }
        if (protocol == HID_ITF_PROTOCOL_MOUSE || descriptorHadMouseApplication) {
            mouseProxyEvent.store(MouseProxyEvent::Unsupported, std::memory_order_release);
        }
        return;
    }

    activeHostDevice = deviceAddress;
    activeHostInstance = instance;
    uint16_t vid = 0;
    uint16_t pid = 0;
    tuh_vid_pid_get(deviceAddress, &vid, &pid);
    hostMouseVid.store(vid, std::memory_order_release);
    hostMousePid.store(pid, std::memory_order_release);
    hostMouseConnected.store(true, std::memory_order_release);
    mouseProxyEvent.store(MouseProxyEvent::Connected, std::memory_order_release);
    if (!tuh_hid_receive_report(deviceAddress, instance)) {
        clear_host_mouse_state();
        mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
    }
}

void tuh_hid_set_protocol_complete_cb(
    uint8_t deviceAddress,
    uint8_t instance,
    uint8_t protocol) {
    if (deviceAddress != activeHostDevice || instance != activeHostInstance ||
        protocol != HID_PROTOCOL_BOOT || !hostMouseUsesBootProtocol) {
        return;
    }

    configure_boot_mouse_layout();
    uint16_t vid = 0;
    uint16_t pid = 0;
    tuh_vid_pid_get(deviceAddress, &vid, &pid);
    hostMouseVid.store(vid, std::memory_order_release);
    hostMousePid.store(pid, std::memory_order_release);
    hostMouseConnected.store(true, std::memory_order_release);
    mouseProxyEvent.store(MouseProxyEvent::Connected, std::memory_order_release);
    if (!tuh_hid_receive_report(deviceAddress, instance)) {
        clear_host_mouse_state();
        mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
    }
}

void tuh_hid_umount_cb(uint8_t deviceAddress, uint8_t instance) {
    if (deviceAddress == activeHostDevice && instance == activeHostInstance) {
        clear_host_mouse_state();
        mouseProxyEvent.store(MouseProxyEvent::Disconnected, std::memory_order_release);
    }
}

void tuh_hid_report_received_cb(
    uint8_t deviceAddress,
    uint8_t instance,
    const uint8_t* report,
    uint16_t length) {
    if (deviceAddress == activeHostDevice && instance == activeHostInstance) {
        decode_host_mouse_report(report, length);
        if (!tuh_hid_receive_report(deviceAddress, instance)) {
            clear_host_mouse_state();
            mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
        }
    }
}
} // extern "C"
#endif

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

// General mode may vary its fixed 8 ms update cadence slightly. This is not
// used for configured weapon patterns or rapid-fire shot scheduling.
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

static void print_hardware_identity() {
#ifdef ZEROSENSE_RP2350_USB_C
    Serial.println("DEVICE:RP2350-USB-C:MOUSE-PROXY");
    if (hostMouseConnected.load(std::memory_order_acquire)) {
        Serial.printf(
            "MOUSE:CONNECTED:VID=%04X:PID=%04X\n",
            hostMouseVid.load(std::memory_order_acquire),
            hostMousePid.load(std::memory_order_acquire));
    } else {
        Serial.println("MOUSE:DISCONNECTED");
    }
#else
    Serial.println("DEVICE:RP2040-ZERO:CDC+HID");
#endif
}

// Validate and apply a complete host profile.
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
            print_hardware_identity();
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

static int8_t report_delta(int32_t value) {
#ifdef ZEROSENSE_RP2350_USB_C
    return static_cast<int8_t>(std::clamp<int32_t>(value, -127, 127));
#else
    return static_cast<int8_t>(std::clamp<int32_t>(value, -100, 100));
#endif
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

static void service_mouse_proxy_status() {
    switch (mouseProxyEvent.exchange(MouseProxyEvent::None, std::memory_order_acq_rel)) {
        case MouseProxyEvent::Connected:
            Serial.printf(
                "MOUSE:CONNECTED:VID=%04X:PID=%04X\n",
                hostMouseVid.load(std::memory_order_acquire),
                hostMousePid.load(std::memory_order_acquire));
            break;
        case MouseProxyEvent::Disconnected:
            Serial.println("MOUSE:DISCONNECTED");
            break;
        case MouseProxyEvent::Unsupported:
            Serial.println("MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR");
            break;
        case MouseProxyEvent::HostError:
            Serial.println("MOUSE:HOST_ERROR");
            break;
        case MouseProxyEvent::None:
            break;
    }
}
#else
static uint8_t current_output_buttons() {
    return rapidButtonDown ? MOUSE_BUTTON_LEFT : 0;
}
#endif

// Inputs are HID counts for this movement step. Sensitivity is applied exactly
// once here; pattern points bypass smoothing so the transmitted curve retains
// its calibrated per-shot displacement.
static void queue_mouse_movement(
    float requested_dx,
    float requested_dy,
    bool apply_smoothing) {
    const float scaledX = requested_dx * horizontalSensitivityFactor;
    const float scaledY = requested_dy * verticalSensitivityFactor;

    const auto approach = [](float current, float target, float maximumStep) {
        if (current < target) {
            return std::min(current + maximumStep, target);
        }
        if (current > target) {
            return std::max(current - maximumStep, target);
        }
        return current;
    };

    if (apply_smoothing) {
        const float targetX = std::clamp(scaledX, -sim.maxVelocity, sim.maxVelocity);
        const float targetY = std::clamp(scaledY, -sim.maxVelocity, sim.maxVelocity);
        sim.velocityX = targetX == 0.0f
            ? sim.velocityX * sim.friction
            : approach(sim.velocityX, targetX, sim.acceleration);
        sim.velocityY = targetY == 0.0f
            ? sim.velocityY * sim.friction
            : approach(sim.velocityY, targetY, sim.acceleration);
    } else {
        sim.velocityX = scaledX;
        sim.velocityY = scaledY;
    }
    sim.accelerating = std::abs(sim.velocityX) >= 0.01f ||
        std::abs(sim.velocityY) >= 0.01f;

    fractionalMouseX += sim.velocityX;
    fractionalMouseY += sim.velocityY;

    const int32_t queuedX = static_cast<int32_t>(fractionalMouseX);
    const int32_t queuedY = static_cast<int32_t>(fractionalMouseY);
    fractionalMouseX -= static_cast<float>(queuedX);
    fractionalMouseY -= static_cast<float>(queuedY);
    pendingMouseX += queuedX;
    pendingMouseY += queuedY;
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
        buttons == lastSentButtons && !hidStateDirty) {
        return;
    }

    if (TinyUSBDevice.suspended()) {
        TinyUSBDevice.remoteWakeup();
    }

    // The RP2350 proxy always services the upstream HID endpoint at 1 kHz so a
    // high-polling physical mouse is not artificially reduced to the idle rate.
#ifdef ZEROSENSE_RP2350_USB_C
    const uint32_t poll_interval_us = POLL_HIGH_MS * 1000UL;
#else
    // Adaptive polling for the standalone RP2040 output device.
    uint32_t poll_interval_us = POLL_NORMAL_MS * 1000UL;
    const bool is_idle = !fireActive && !rapidFireActive;
    const bool is_high_activity = rapidFireActive || sim.accelerating;

    if (is_idle) {
        poll_interval_us = POLL_IDLE_MS * 1000UL; // Reduce packet rate during inactivity
    } else if (is_high_activity) {
        poll_interval_us = POLL_HIGH_MS * 1000UL;
    }
#endif

    const uint32_t now = micros();
    if (static_cast<uint32_t>(now - lastHidReportAtUs) < poll_interval_us ||
        !TinyUSBDevice.mounted() || !usbHid.ready()) {
        return;
    }

    const int8_t dx = report_delta(pendingMouseX + pendingPhysicalMouseX);
    const int8_t dy = report_delta(pendingMouseY + pendingPhysicalMouseY);
    const int8_t wheel = report_delta(pendingPhysicalWheel);
    const int8_t pan = report_delta(pendingPhysicalPan);

    if (usbHid.mouseReport(0, buttons, dx, dy, wheel, pan)) {
#ifdef ZEROSENSE_RP2350_USB_C
        consume_axis_delta(pendingMouseX, pendingPhysicalMouseX, dx);
        consume_axis_delta(pendingMouseY, pendingPhysicalMouseY, dy);
#else
        pendingMouseX -= dx;
        pendingMouseY -= dy;
#endif
        pendingPhysicalWheel -= wheel;
        pendingPhysicalPan -= pan;
        lastSentButtons = buttons;
        hidStateDirty = false;
        lastHidReportAtUs = now;
    }
}

// Generate one configured recoil step.
static void generate_movement() {
    float vertical;
    float horizontal;

    if (activeMode == MODE_WEAPON_PATTERN) {
        if (shotsInBurst >= loadedPatternPoints) {
            stop_output();
            return;
        }
        // Pattern values are stored as Q8.8 fixed-point (int16 / 256.0f)
        horizontal = static_cast<float>(patternHorizontal[shotsInBurst]) / 256.0f;
        vertical = static_cast<float>(patternVertical[shotsInBurst]) / 256.0f;
    } else {
        vertical = activeVerticalCompensation;
        horizontal = activeHorizontalCompensation;
    }

    // Burst progression reduction for diminishing recoil over shot sequence
    if (activeMode == MODE_GENERAL && shotsInBurst > 0 && activeBurstProgression > 0) {
        const float reduction = 1.0f - (static_cast<float>(shotsInBurst) * activeBurstProgression / 100.0f);
        vertical *= std::max(0.5f, reduction);
        horizontal *= std::max(0.5f, reduction);
    }

    queue_mouse_movement(horizontal, vertical, activeMode == MODE_GENERAL);

    ++shotsInBurst;
}

// Service recoil movement using phase-locked timing for weapon patterns.
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
        const uint32_t interval = activeMode == MODE_WEAPON_PATTERN
            ? base_interval
            : get_jittered_interval(base_interval);
        nextMovementAtUs += interval;
        if (static_cast<int32_t>(now - nextMovementAtUs) >= 0) {
            nextMovementAtUs = now + interval;
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
        generate_movement();
        if (!fireActive) {
            return;
        }
        rapidButtonReleaseAtUs = now + 8000;

        const uint32_t base_interval = static_cast<uint32_t>(60000000.0f / rapidFireRoundsPerMinute);
        const uint32_t shot_interval = base_interval;
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
        Serial.println("STOP:WATCHDOG");
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
    usbHid.setPollInterval(POLL_HIGH_MS);
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
    Serial.println("READY:RP2350-USB-C:MOUSE-PROXY");
#else
    Serial.println("READY:RP2040-ZERO:CDC+HID");
#endif
    print_hardware_identity();
}

void loop() {
#ifdef TINYUSB_NEED_POLLING_TASK
    TinyUSBDevice.task();
#endif
    service_serial();
#ifdef ZEROSENSE_RP2350_USB_C
    service_mouse_proxy_status();
#endif
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
        mouseProxyEvent.store(MouseProxyEvent::HostError, std::memory_order_release);
        return;
    }
    hostStackStarted = true;
}

void loop1() {
    if (hostStackStarted) {
        usbHost.task();
    } else {
        delay(1);
    }
}
#endif
