#pragma once

#include <stdint.h>

// Four PIO TX cycles emit one USB bit. The divider has an 8-bit fractional
// component; round the resulting CPU-cycle wait up so a 120 MHz RP2350 does
// not resume host RX before the EOP tail has actually finished.
static inline uint32_t pio_usb_host_bit_cycles(uint16_t divider_integer,
                                                uint8_t divider_fraction) {
  return 4u * divider_integer + (divider_fraction + 63u) / 64u;
}
