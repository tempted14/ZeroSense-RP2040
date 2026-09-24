#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <string>

#include "../../RP2040_Firmware/rainbow_recoil/hid_report_decoder.h"
#include "../../RP2040_Firmware/rainbow_recoil/correction_scheduler.h"
#include "../../RP2040_Firmware/rainbow_recoil/delta_noise.h"
#include "../../RP2040_Firmware/rainbow_recoil/host_receive_recovery.h"
#include "../../RP2040_Firmware/rainbow_recoil/motion_math.h"
#include "../../RP2040_Firmware/rainbow_recoil/first_bullet_kick.h"
#include "../../RP2040_Firmware/rainbow_recoil/hid_output_math.h"
#include "../../RP2040_Firmware/rainbow_recoil/protocol_output.h"
#include "../../RP2040_Firmware/lib/PicoPIOUSB/src/pio_usb_host_timing.h"
#include "../../RP2040_Firmware/rainbow_recoil/host_core_health.h"

namespace {
int failures = 0;

void expect(bool condition, const char* name) {
    if (!condition) {
        std::cerr << "FAIL: " << name << '\n';
        ++failures;
    }
}
}

#include "pio_host_guard_tests.h"

int main() {
    using namespace ZeroSenseHid;
    testPioHostGuards();

    expect(ZeroSenseHidOutput::reportDelta(127) == 127 &&
        ZeroSenseHidOutput::reportDelta(-127) == -127,
        "RP2040 and RP2350 use the full declared HID X/Y range");
    expect(ZeroSenseHidOutput::reportDelta(10000) == 127 &&
        ZeroSenseHidOutput::reportDelta(-10000) == -127,
        "large queued deltas remain bounded to one signed-byte report");
    expect(ZeroSenseHidOutput::addPending(INT32_MAX - 2, 50) == INT32_MAX &&
        ZeroSenseHidOutput::addPending(INT32_MIN + 2, -50) == INT32_MIN,
        "extreme General-mode output never wraps its pending HID queue");

    expect(ZeroSenseFirstBullet::verticalForShot(127.0f, 0, 2.0f) == 254.0f,
        "first bullet kick applies after Q8.8 profile saturation");
    expect(ZeroSenseFirstBullet::verticalForShot(127.0f, 1, 2.0f) == 127.0f,
        "later shots retain their original vertical vectors");
    expect(ZeroSenseFirstBullet::verticalForShot(12.0f, 0, 1.0f) == 12.0f,
        "default first bullet kick is neutral");

    expect(pio_usb_host_bit_cycles(2, 128) == 10,
        "120 MHz full-speed PIO bit timing includes half-cycle divider");
    expect(pio_usb_host_bit_cycles(5, 0) == 20,
        "integer PIO divider timing remains exact");
    expect(pio_usb_host_bit_cycles(2, 1) == 9,
        "fractional PIO timing rounds up before EOP wait");
    expect(!ZeroSenseHostHealth::stalled(0, 100000, 500),
        "host watchdog does not trip before the first heartbeat");
    expect(!ZeroSenseHostHealth::stalled(2000, 2500, 500) &&
        ZeroSenseHostHealth::stalled(2000, 2501, 500),
        "host watchdog distinguishes a delayed task from a stalled one");
    expect(ZeroSenseHostHealth::stalled(UINT32_MAX - 200, 400, 500),
        "host watchdog timeout survives millis wrap");
    expect(!ZeroSenseHostHealth::restartDue(0, 100000) &&
        !ZeroSenseHostHealth::restartDue(2000, 5000) &&
        ZeroSenseHostHealth::restartDue(2000, 5001),
        "host restart needs a prior heartbeat and more than three seconds without progress");
    expect(ZeroSenseHostHealth::restartDue(UINT32_MAX - 200, 2900),
        "host restart timeout survives millis wrap");

    expect(std::abs(ZeroSenseMotion::intervalScale(7360) - 0.92f) < 0.0001f,
        "negative timing jitter scale");
    expect(std::abs(ZeroSenseMotion::intervalScale(8640) - 1.08f) < 0.0001f,
        "positive timing jitter scale");
    const float halfDecay = ZeroSenseMotion::frictionForInterval(0.85f, 0.5f);
    expect(std::abs((halfDecay * halfDecay) - 0.85f) < 0.0001f,
        "friction is time normalized");
    expect(std::abs(ZeroSenseMotion::approach(0.0f, 5.0f, 1.2f) - 1.2f) < 0.0001f,
        "acceleration remains bounded");
    expect(std::abs(ZeroSenseMotion::calibratedLimit(40.0f, 4.0f) - 160.0f) < 0.0001f,
        "2.5x vertical boost raises the general-mode velocity cap");
    expect(std::abs(ZeroSenseMotion::calibratedLimit(1.2f, 4.0f) - 4.8f) < 0.0001f,
        "2.5x vertical boost raises acceleration proportionally");
    float baseVelocity = 0.0f;
    float boostedVelocity = 0.0f;
    for (int tick = 0; tick < 40; ++tick) {
        baseVelocity = ZeroSenseMotion::approach(baseVelocity, 40.0f, 1.2f);
        boostedVelocity = ZeroSenseMotion::approach(boostedVelocity, 160.0f, 4.8f);
    }
    expect(std::abs(boostedVelocity - 4.0f * baseVelocity) < 0.0001f,
        "2.5x general-mode smoothing preserves fourfold output after ramp");
    ZeroSenseCorrection::State boostedScheduler = {};
    ZeroSenseCorrection::schedule(boostedScheduler, 0.0f, 127.0f * 4.0f,
        75000, 1000);
    int32_t boostedTotal = 0;
    int32_t largestBoostedFrame = 0;
    for (uint32_t frame = 0; frame < 75; ++frame) {
        const auto result = ZeroSenseCorrection::service(
            boostedScheduler, 1000 + frame * 1000);
        boostedTotal += result.queuedY;
        largestBoostedFrame = std::max(largestBoostedFrame, result.queuedY);
    }
    expect(boostedTotal == 508 && largestBoostedFrame <= 127,
        "capped 127-point pattern delivers boosted correction across HID frames");
    ZeroSenseCorrection::State maximumPatternScheduler = {};
    ZeroSenseCorrection::schedule(maximumPatternScheduler, -127.0f * 4.0f,
        127.0f * 16.0f, 30000, 1000);
    int32_t maximumPatternX = 0;
    int32_t maximumPatternY = 0;
    int32_t maximumPatternFrame = 0;
    for (uint32_t frame = 0; frame < 30; ++frame) {
        const auto result = ZeroSenseCorrection::service(
            maximumPatternScheduler, 1000 + frame * 1000);
        maximumPatternX += result.queuedX;
        maximumPatternY += result.queuedY;
        maximumPatternFrame = std::max(maximumPatternFrame, result.queuedY);
    }
    expect(maximumPatternX == -508 && maximumPatternY == 2032 &&
        maximumPatternFrame <= 127,
        "combined pattern and optic boost emits the full correction safely");
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

    ZeroSenseHostRecovery::QueueState receiveState = {};
    expect(
        ZeroSenseHostRecovery::recordQueueAttempt(receiveState, false, 3) ==
            ZeroSenseHostRecovery::QueueOutcome::Retry,
        "first receive queue failure is retryable");
    expect(
        ZeroSenseHostRecovery::recordQueueAttempt(receiveState, true, 3) ==
            ZeroSenseHostRecovery::QueueOutcome::Recovered,
        "successful requeue reports recovery");
    expect(receiveState.consecutiveFailures == 0 && !receiveState.recoveryPending,
        "successful requeue clears recovery state");
    ZeroSenseHostRecovery::recordQueueAttempt(receiveState, false, 3);
    ZeroSenseHostRecovery::recordQueueAttempt(receiveState, false, 3);
    expect(
        ZeroSenseHostRecovery::recordQueueAttempt(receiveState, false, 3) ==
            ZeroSenseHostRecovery::QueueOutcome::Exhausted,
        "bounded receive failures eventually fail closed");
    expect(!ZeroSenseHostRecovery::retryDue(receiveState, 99, 3),
        "sustained receive failures use a bounded slow retry");
    expect(ZeroSenseHostRecovery::retryDue(receiveState, 100, 3),
        "quarantined interface remains eligible for recovery");
    expect(ZeroSenseHostRecovery::recordQueueAttempt(receiveState, true, 3, 100) ==
        ZeroSenseHostRecovery::QueueOutcome::Recovered,
        "receive submission recovers even after the failure threshold");
    ZeroSenseHostRecovery::recordQueueAttempt(receiveState, false, 3, UINT32_MAX - 2);
    expect(!ZeroSenseHostRecovery::retryDue(receiveState, 1, 3) &&
        ZeroSenseHostRecovery::retryDue(receiveState, 2, 3),
        "receive retry backoff survives clock wrap");
    for (int i = 0; i < 300; ++i) {
        ZeroSenseHostRecovery::recordQueueAttempt(receiveState, false, 3, 200);
    }
    expect(receiveState.consecutiveFailures == 255 &&
        !ZeroSenseHostRecovery::retryDue(receiveState, 299, 3),
        "sustained failure counter saturates without bypassing backoff");

    // A full CDC FIFO must never prevent the firmware loop from advancing.
    ZeroSenseProtocol::OutputQueue<8> output;
    std::string delivered;
    int writes = 0;
    auto writer = [&](const uint8_t* bytes, size_t length) -> size_t {
        ++writes;
        delivered.append(reinterpret_cast<const char*>(bytes), length);
        return length;
    };
    expect(output.enqueue("ACK\n", 4), "CDC queues complete reply");
    int hidServices = 0;
    for (int i = 0; i < 1000; ++i) {
        expect(output.drain(0, 128, writer) == 0, "full CDC FIFO returns immediately");
        ++hidServices;
    }
    expect(writes == 0 && output.size() == 4 && hidServices == 1000,
        "blocked CDC writer leaves all HID loop opportunities available");
    expect(!output.enqueue("ERROR\n", 6) && output.droppedMessages == 1 &&
        output.size() == 4, "overflow drops whole reply without corrupting prior data");
    expect(output.drain(8, 2, writer) == 2 && output.size() == 2,
        "CDC per-loop write budget is respected");
    expect(output.enqueue("STAT\n", 5), "CDC ring wraps on enqueue");
    while (output.size()) output.drain(8, 3, writer);
    expect(delivered == "ACK\nSTAT\n", "CDC wrap preserves complete reply order");
    output.enqueue("ABC", 3);
    expect(output.drain(8, 8, [](const uint8_t*, size_t) -> size_t { return 0; }) == 0 &&
        output.size() == 3, "zero-byte write retains pending data");
    expect(output.drain(8, 8, [](const uint8_t*, size_t) -> size_t { return 1; }) == 1 &&
        output.size() == 2, "short write consumes only accepted bytes");
    output.clear();
    expect(output.size() == 0 && output.droppedMessages == 1,
        "CDC disconnect clears stale replies but preserves diagnostic counter");

    if (failures == 0) {
        std::cout << "HID replay: all fixtures passed\n";
    }
    return failures == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}
