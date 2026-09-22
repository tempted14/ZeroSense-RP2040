# Pico PIO USB dependency

`PicoPIOUSB/` is the upstream MIT-licensed Pico-PIO-USB `src/` and
`library.properties` from commit
`5a37a66dc5d3fbe0ef3cdbeda923a757440f984f`, plus its LICENSE. It is
vendored so the RP2350 build uses the same reviewed host fix locally and in CI.

Local change in `src/pio_usb.c`: the host TX EOP completion path no longer polls
the PIO state machine program counter. That polling window can be missed and
leave core 1 spinning indefinitely while CDC on core 0 still answers. It now
waits for the fixed four-bit EOP tail using the full integer/fractional PIO clock
divider. The added `src/pio_usb_host_timing.h` makes the divider calculation
testable. The change is based on upstream
[issue #197](https://github.com/sekigon-gonnoc/Pico-PIO-USB/issues/197) and
[PR #206, first commit](https://github.com/sekigon-gonnoc/Pico-PIO-USB/pull/206/commits/02ca10b779ba4bba191bd46c7360d061fe703ce0).
The full PR remains unmerged and has compatibility reports, so its other changes
were deliberately not copied. RP2350 hardware soak testing is required before
calling this a confirmed fix.
