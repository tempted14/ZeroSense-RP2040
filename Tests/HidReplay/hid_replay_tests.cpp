#include <cstdint>
#include <cstdlib>
#include <iostream>

#include "../../RP2040_Firmware/rainbow_recoil/hid_report_decoder.h"
#include "../../RP2040_Firmware/rainbow_recoil/correction_scheduler.h"
#include "../../RP2040_Firmware/rainbow_recoil/delta_noise.h"
#include "../../RP2040_Firmware/rainbow_recoil/motion_math.h"

namespace {
int failures = 0;

void expect(bool condition, const char* name) {
    if (!condition) {
        std::cerr << "FAIL: " << name << '\n';
        ++failures;
    }
}
}

int main() {
    using namespace ZeroSenseHid;

    expect(std::abs(ZeroSenseMotion::intervalScale(7360) - 0.92f) < 0.0001f,
        "negative timing jitter scale");
    expect(std::abs(ZeroSenseMotion::intervalScale(8640) - 1.08f) < 0.0001f,
        "positive timing jitter scale");
    const float halfDecay = ZeroSenseMotion::frictionForInterval(0.85f, 0.5f);
    expect(std::abs((halfDecay * halfDecay) - 0.85f) < 0.0001f,
        "friction is time normalized");
    expect(std::abs(ZeroSenseMotion::approach(0.0f, 5.0f, 1.2f) - 1.2f) < 0.0001f,
        "acceleration remains bounded");
    const float jitteredDistance =
        10.0f * ZeroSenseMotion::intervalScale(7360) +
        10.0f * ZeroSenseMotion::intervalScale(8640);
    expect(std::abs(jitteredDistance - 20.0f) < 0.0001f,
        "paired jitter preserves integrated displacement");
    expect(std::abs(ZeroSenseMotion::shotIntervalScale(120000) - 15.0f) < 0.0001f,
        "semi-automatic shot scale is not clamped to smoothing limits");

    ZeroSenseDeltaNoise::AxisState noise = {};
    const auto firstNoise = ZeroSenseDeltaNoise::preferredDelta(noise, 3);
    ZeroSenseDeltaNoise::recordAppliedDelta(noise, firstNoise);
    expect(firstNoise == 3 && noise.balance == 3,
        "delta noise records the emitted offset");
    const auto repayment = ZeroSenseDeltaNoise::preferredDelta(noise, -2);
    ZeroSenseDeltaNoise::recordAppliedDelta(noise, repayment);
    expect(repayment == -3 && noise.balance == 0,
        "delta noise repays its offset before introducing another");
    ZeroSenseDeltaNoise::recordAppliedDelta(noise, 1);
    expect(ZeroSenseDeltaNoise::preferredDelta(noise, 3) == -1,
        "saturated delta noise repays the exact applied amount");

    // Fake-clock scheduler coverage: output totals survive fractional values,
    // stalls are counted, and a catch-up batch does not rephase the deadline.
    ZeroSenseCorrection::State scheduler = {};
    ZeroSenseCorrection::schedule(scheduler, 10.5f, -3.25f, 8000, 1000);
    int32_t scheduledX = 0;
    int32_t scheduledY = 0;
    for (uint32_t now = 1000; now <= 8000; now += 1000) {
        const auto result = ZeroSenseCorrection::service(scheduler, now);
        scheduledX += result.queuedX;
        scheduledY += result.queuedY;
    }
    scheduledX += ZeroSenseCorrection::roundedFraction(scheduler.fractionXQ16);
    scheduledY += ZeroSenseCorrection::roundedFraction(scheduler.fractionYQ16);
    expect(scheduler.frames == 0, "fake clock drains scheduled frames");
    expect(scheduledX == 11 && scheduledY == -3,
        "fake clock preserves rounded scheduled displacement");

    scheduler = {};
    ZeroSenseCorrection::schedule(scheduler, 8.0f, 0.0f, 8000, 1000);
    const auto stalledBatch = ZeroSenseCorrection::service(scheduler, 6000);
    expect(stalledBatch.servicedFrames == 4 && scheduler.frames == 4,
        "scheduler bounds one catch-up batch");
    expect(scheduler.nextFrameAtUs == 5000,
        "catch-up keeps the original absolute deadline");
    expect(scheduler.delayedFrames == 4 && scheduler.maximumLatenessUs == 5000,
        "scheduler exposes delayed-frame telemetry");
    const auto finalBatch = ZeroSenseCorrection::service(scheduler, 10000);
    expect(finalBatch.servicedFrames == 4 && scheduler.frames == 0,
        "later fake-clock service preserves all remaining frames");

    const uint8_t bootDescriptor[] = {
        0x05,0x01,0x09,0x02,0xA1,0x01,0x09,0x01,0xA1,0x00,
        0x05,0x09,0x19,0x01,0x29,0x03,0x15,0x00,0x25,0x01,
        0x95,0x03,0x75,0x01,0x81,0x02,0x95,0x01,0x75,0x05,
        0x81,0x01,0x05,0x01,0x09,0x30,0x09,0x31,0x15,0x81,
        0x25,0x7F,0x75,0x08,0x95,0x02,0x81,0x06,0xC0,0xC0
    };
    Decoder decoder = {};
    bool mouseApplication = false;
    expect(parseDescriptor(
        decoder, bootDescriptor, sizeof(bootDescriptor), mouseApplication),
        "boot descriptor parses");
    expect(mouseApplication, "mouse application recognized");
    const uint8_t bootReport[] = {0x03, 0x05, 0xFD};
    DecodedReport decoded = {};
    expect(decodeReport(decoder, bootReport, sizeof(bootReport), decoded),
        "boot report decodes");
    expect(decoded.reportedButtonMask == 0x07 && decoded.buttons == 0x03,
        "boot buttons decode");
    expect(decoded.deltaX == 5 && decoded.deltaY == -3,
        "signed 8-bit axes decode");

    // Buttons and 16-bit movement are deliberately split across report IDs.
    const uint8_t splitDescriptor[] = {
        0x05,0x01,0x09,0x02,0xA1,0x01,
        0x85,0x01,0x05,0x09,0x19,0x01,0x29,0x02,0x15,0x00,
        0x25,0x01,0x75,0x01,0x95,0x02,0x81,0x02,0x75,0x06,
        0x95,0x01,0x81,0x01,
        0x85,0x02,0x05,0x01,0x09,0x30,0x09,0x31,0x16,0x00,
        0x80,0x26,0xFF,0x7F,0x75,0x10,0x95,0x02,0x81,0x06,
        0xC0
    };
    decoder = {};
    expect(parseDescriptor(
        decoder, splitDescriptor, sizeof(splitDescriptor), mouseApplication),
        "split report-ID descriptor parses");
    uint8_t combinedButtons = 0;
    const uint8_t buttonReport[] = {0x01, 0x03};
    expect(decodeReport(decoder, buttonReport, sizeof(buttonReport), decoded),
        "button report ID decodes");
    combinedButtons = static_cast<uint8_t>(
        (combinedButtons & ~decoded.reportedButtonMask) |
        (decoded.buttons & decoded.reportedButtonMask));
    expect(combinedButtons == 0x03 && decoded.reportedButtonMask == 0x03,
        "split buttons latch");

    const uint8_t movementReport[] = {0x02, 0x2C,0x01, 0x38,0xFF};
    expect(decodeReport(decoder, movementReport, sizeof(movementReport), decoded),
        "movement report ID decodes");
    combinedButtons = static_cast<uint8_t>(
        (combinedButtons & ~decoded.reportedButtonMask) |
        (decoded.buttons & decoded.reportedButtonMask));
    expect(decoded.reportedButtonMask == 0 && combinedButtons == 0x03,
        "movement-only report preserves buttons");
    expect(decoded.deltaX == 300 && decoded.deltaY == -200,
        "signed 16-bit axes decode");

    // Exercise the widest supported relative field plus both scroll axes.
    const uint8_t wideDescriptor[] = {
        0x05,0x01,0x09,0x02,0xA1,0x01,0x85,0x03,
        0x09,0x30,0x17,0x00,0x00,0x00,0x80,
        0x27,0xFF,0xFF,0xFF,0x7F,0x75,0x20,0x95,0x01,0x81,0x06,
        0x09,0x38,0x15,0x81,0x25,0x7F,0x75,0x08,0x95,0x01,0x81,0x06,
        0x05,0x0C,0x0A,0x38,0x02,0x75,0x08,0x95,0x01,0x81,0x06,
        0xC0
    };
    decoder = {};
    expect(parseDescriptor(
        decoder, wideDescriptor, sizeof(wideDescriptor), mouseApplication),
        "32-bit wheel/pan descriptor parses");
    const uint8_t wideReport[] = {0x03, 0x60,0x79,0xFE,0xFF, 0xFE, 0x03};
    expect(decodeReport(decoder, wideReport, sizeof(wideReport), decoded),
        "32-bit wheel/pan report decodes");
    expect(decoded.deltaX == -100000, "signed 32-bit axis decodes");
    expect(decoded.wheel == -2 && decoded.pan == 3,
        "vertical wheel and horizontal pan decode");

    const uint8_t unknownReport[] = {0x7F, 0, 0};
    expect(!decodeReport(decoder, unknownReport, sizeof(unknownReport), decoded),
        "unknown report ID rejected");
    expect(!parseDescriptor(
        decoder, splitDescriptor, sizeof(splitDescriptor) - 1, mouseApplication),
        "truncated descriptor rejected");

    if (failures == 0) {
        std::cout << "HID replay: all fixtures passed\n";
    }
    return failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
