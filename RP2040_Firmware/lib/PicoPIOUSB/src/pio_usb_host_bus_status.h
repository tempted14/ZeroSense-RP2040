#pragma once
#include <stdbool.h>
#include <stdint.h>

enum {
  PIO_USB_HOST_ROOT_INITIALIZED = 1u,
  PIO_USB_HOST_ROOT_CONNECTED = 2u,
  PIO_USB_HOST_ROOT_SUSPENDED = 4u,
  PIO_USB_HOST_DP_HIGH = 8u,
  PIO_USB_HOST_DM_HIGH = 16u,
  PIO_USB_HOST_ROOT_FULL_SPEED = 32u
};

static inline uint32_t pio_usb_host_pack_root_status(
    bool initialized, bool connected, bool suspended, bool dp, bool dm, bool full_speed) {
  return (initialized ? (uint32_t)PIO_USB_HOST_ROOT_INITIALIZED : 0u) |
         (connected ? (uint32_t)PIO_USB_HOST_ROOT_CONNECTED : 0u) |
         (suspended ? (uint32_t)PIO_USB_HOST_ROOT_SUSPENDED : 0u) |
         (dp ? (uint32_t)PIO_USB_HOST_DP_HIGH : 0u) |
         (dm ? (uint32_t)PIO_USB_HOST_DM_HIGH : 0u) |
         (full_speed ? (uint32_t)PIO_USB_HOST_ROOT_FULL_SPEED : 0u);
}
