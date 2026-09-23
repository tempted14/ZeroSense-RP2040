#pragma once

#include <stdbool.h>
#include <stdint.h>

// Fault limit, NOT a USB timing interval. At 120/240 MHz this is far longer
// than a legal LS/FS packet. Keep timer MMIO, division and function calls out
// of these turnaround-sensitive waits (upstream PR #206 compatibility report).
#define PIO_USB_HOST_WAIT_POLLS 1000000u

// Tests substitute a deterministic fake register; production is one volatile
// read, fully inlined. No callbacks or clock reads on the normal path.
#ifndef PIO_USB_HOST_READ_REGISTER
#define PIO_USB_HOST_READ_REGISTER(reg) (*(reg))
#endif

static inline __attribute__((always_inline)) bool pio_usb_host_wait_set(
    volatile const uint32_t *reg, uint32_t mask, uint32_t polls) {
  while (polls-- != 0u) {
    if ((PIO_USB_HOST_READ_REGISTER(reg) & mask) != 0u) {
      return true;
    }
  }
  return false;
}

static inline __attribute__((always_inline)) bool pio_usb_host_wait_clear(
    volatile const uint32_t *reg, uint32_t mask, uint32_t polls) {
  while (polls-- != 0u) {
    if ((PIO_USB_HOST_READ_REGISTER(reg) & mask) == 0u) {
      return true;
    }
  }
  return false;
}

static inline __attribute__((always_inline)) bool pio_usb_host_rx_has_room(
    uint16_t index, uint16_t capacity) {
  return index < capacity;
}
