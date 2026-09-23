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
The full PR remains unmerged and has compatibility reports, so the original
v1.7.0 release copied only that EOP change.

Three later host-stall candidates failed the user's RP2350 testing. They
shared new timer reads in timing-sensitive TX/RX paths; an upstream comment
specifically reports TX timer polling causing device-recognition failures.
Increasing a timeout's duration does not eliminate that overhead. We did not
isolate EOP reset or single-clear as individual causes, and the old decode
counter also includes zero-byte transfer failures, not just malformed reports.

The current unreleased candidate returns to the confirmed rollback's bus
ordering, EOP state and disconnect behavior. `pio_usb_host_guard.h` supplies
register-only fault-bounded waits (not calibrated USB timeouts) and a capacity
check that rejects babbling RX before buffer/index overflow. The existing
response/inter-byte timers are unchanged. Transfer failures propagate to
normal endpoint retries rather than stale-buffer processing. Fake-register
and capacity tests run as part of `Tests/HidReplay/hid_replay_tests.cpp`.

See `docs/HOST_STALL_TEST_2026-09-22.md` for each failed candidate, new counter
semantics, verification limits and physical acceptance steps. The current
candidate is software-tested only; real 1000 Hz passthrough and a long soak
with the affected RP2350/mouse are still required. No new release is published.
