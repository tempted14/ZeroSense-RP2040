#pragma once

#include <array>

namespace PioGuardFixture {
inline uint32_t reads = 0;
inline uint32_t transitionAfter = 0;
inline uint32_t before = 0;
inline uint32_t after = 0;
inline volatile uint32_t dummyRegister = 0;

inline uint32_t read(volatile const uint32_t*) {
    return reads++ < transitionAfter ? before : after;
}
inline void reset(uint32_t initial, uint32_t eventual, uint32_t delay) {
    reads = 0;
    before = initial;
    after = eventual;
    transitionAfter = delay;
}
}

#define PIO_USB_HOST_READ_REGISTER(reg) PioGuardFixture::read(reg)
#include "../../RP2040_Firmware/lib/PicoPIOUSB/src/pio_usb_host_guard.h"
#undef PIO_USB_HOST_READ_REGISTER

static void testPioHostGuards() {
    using namespace PioGuardFixture;
    constexpr uint32_t flag = 8;
    reset(flag, flag, 0);
    expect(pio_usb_host_wait_set(&dummyRegister, flag, 10) && reads == 1,
        "already-complete TX takes exactly one register read");
    reset(0, flag, 9);
    expect(pio_usb_host_wait_set(&dummyRegister, flag, 10) && reads == 10,
        "TX can complete on the final permitted poll");
    reset(0, flag, 10);
    expect(!pio_usb_host_wait_set(&dummyRegister, flag, 10) && reads == 10,
        "TX that never completes during budget terminates");
    reset(16, 16, 0);
    expect(!pio_usb_host_wait_set(&dummyRegister, flag, 10) && reads == 10,
        "unrelated IRQ cannot satisfy TX completion");
    reset(0, 0, 0);
    expect(pio_usb_host_wait_clear(&dummyRegister, flag, 10) && reads == 1,
        "normal RX clear handshake has one register read and no clock");
    reset(flag, 0, 9);
    expect(pio_usb_host_wait_clear(&dummyRegister, flag, 10) && reads == 10,
        "delayed RX flag clear succeeds at budget boundary");
    reset(flag, flag, 0);
    expect(!pio_usb_host_wait_clear(&dummyRegister, flag, 10) && reads == 10,
        "stuck RX flag cannot spin indefinitely");
    reset(flag, flag, 0);
    expect(!pio_usb_host_wait_set(&dummyRegister, flag, 0) && reads == 0,
        "zero TX budget does not underflow into a long wait");
    expect(!pio_usb_host_wait_clear(&dummyRegister, flag, 0) && reads == 0,
        "zero RX budget does not read the register");
    reset(0, 0, 0);
    expect(!pio_usb_host_wait_set(&dummyRegister, flag, PIO_USB_HOST_WAIT_POLLS) &&
        reads == PIO_USB_HOST_WAIT_POLLS, "production TX guard is finite");
    reset(flag, flag, 0);
    expect(!pio_usb_host_wait_clear(&dummyRegister, flag, PIO_USB_HOST_WAIT_POLLS) &&
        reads == PIO_USB_HOST_WAIT_POLLS, "production RX guard is finite");

    // Exercise the production capacity check with normal and babbling input.
    // This checks memory bounds, not USB electrical/handshake timing.
    for (uint32_t requested : {2u, 4u, 12u, 68u, 128u, 129u, 65536u}) {
        std::array<uint8_t, 130> guardedBuffer{};
        guardedBuffer.front() = 0xA5;
        guardedBuffer.back() = 0x5A;
        uint16_t index = 0;
        for (uint32_t byte = 0; byte < requested; ++byte) {
            if (!pio_usb_host_rx_has_room(index, 128)) break;
            guardedBuffer[1 + index++] = static_cast<uint8_t>(byte);
        }
        expect(index == (requested < 128 ? requested : 128) &&
            guardedBuffer.front() == 0xA5 && guardedBuffer.back() == 0x5A,
            "bounded RX cannot overrun or wrap on babbling input");
    }
    expect(!pio_usb_host_rx_has_room(UINT16_MAX, 128),
        "corrupted high RX index is rejected instead of treated as negative");
}
