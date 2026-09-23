# ZeroSense v1.9.0 — local validation build

**Not published or hardware-certified.** This version addresses regressions
in the unreleased RP2350 host-stall candidates. Real 1000 Hz passthrough and a
sustained soak on the affected RP2350/VXE mouse remain acceptance gates.

## RP2350 USB changes

- Restore the confirmed v1.8 rollback's bus sequencing and disconnect detection.
- Remove extra timer reads from critical transmit/RX-clear waits. Replace
  indefinite polling with register-only fault budgets, keeping the existing
  EOP-tail race fix and protocol response timers.
- Reject oversized/babbling RX before buffer overrun or index wrap.
- Reinitialize stuck RX/EOP state only on a confirmed guard failure, not
  during healthy traffic. Discard the failed transaction before retrying.
- Propagate failed transfer/receive-start results into endpoint retries; do
  not use stale handshake bytes or report a failed ACK as success.
- Separate empty HID callbacks from non-empty decoder failures in diagnostics.

## Desktop polish

- App and Windows manifest identify v1.9.0; UI labels this as validation.
- Short, current-sample USB summary with approximate accepted-input, empty
  callback and outgoing-HID rates. Idle traffic is not called a disconnect.
- Detect and label a stalled host task even if serial still responds.
- Collapsible, selectable lifetime counters; firmware build shown separately.
- BUILD/STATUS protocol replies no longer overwrite the connection explanation.
- Counter resets, wraps and long sample gaps cannot produce bogus rate spikes.

## Retained

Existing profiles, original-pattern multiplier, global/per-weapon tuning,
horizontal options, 2.5x boost, jitter and delta noise are unchanged. Settings
schema and configuration protocol are unchanged. No calibration migration is
introduced. RP2040 remains an output-only board; local physical-button
activation stays RP2350-only.

## Installation and rollback

Use the local validation package's START_HERE guide. The package includes the
v1.9 portable app, both explicitly named UF2 targets, diagnostics, checksums
and a byte-for-byte copy of the user's confirmed-good RP2350 rollback.
Never flash the RP2040 file onto RP2350. The portable app is unsigned; a local
SHA256 manifest checks integrity, not publisher authenticity. No signing
certificate or GitHub attestation is claimed for these local artifacts.

For investigation details, rejected builds, evidence and verification limits,
see [the USB audit](HOST_STALL_TEST_2026-09-22.md). Software tests do not
prove hardware polling quality or eliminate every possible disconnect cause.
