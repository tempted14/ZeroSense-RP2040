#pragma once

#include "pico/mutex.h"

// Adafruit's RP2040/RP2350 port runs tud_task in a software interrupt and
// exports this mutex expressly to serialize application USB access. Do not
// disable USB interrupts for an entire firmware loop or block on this lock.
extern mutex_t __usb_mutex;

class NativeUsbGuard {
public:
    NativeUsbGuard() : acquired_(mutex_try_enter(&__usb_mutex, nullptr)) {}
    ~NativeUsbGuard() { if (acquired_) mutex_exit(&__usb_mutex); }
    explicit operator bool() const { return acquired_; }
    NativeUsbGuard(const NativeUsbGuard&) = delete;
    NativeUsbGuard& operator=(const NativeUsbGuard&) = delete;
private:
    bool acquired_;
};
