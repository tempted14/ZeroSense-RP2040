#pragma once

#include <stdint.h>
#include <stdbool.h>

// Four PIO TX cycles emit one USB bit. The divider has an 8-bit fractional
// component; round the resulting CPU-cycle wait up so a 120 MHz RP2350 does
// not resume host RX before the EOP tail has actually finished.
static inline uint32_t pio_usb_host_bit_cycles(uint16_t divider_integer,
                                                uint8_t divider_fraction) {
  return 4u * divider_integer + (divider_fraction + 63u) / 64u;
}

// Timer arithmetic is modulo 2^32, so this also works across a micros wrap.
static inline bool pio_usb_host_timeout_elapsed(uint32_t start_us,
                                                 uint32_t now_us,
                                                 uint32_t timeout_us) {
  return (uint32_t)(now_us - start_us) > timeout_us;
}

// Generous fault budget: normal PIO transfers take far less than 2 ms, but
// this avoids the 1 ms timeout that caused compatibility reports upstream.
static inline uint32_t pio_usb_host_tx_timeout_us(uint16_t encoded_bytes,
                                                  bool low_speed) {
  return 2000u + (low_speed ? 24u : 4u) * encoded_bytes;
}
