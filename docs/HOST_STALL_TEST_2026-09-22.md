# RP2350 passthrough regression investigation (unreleased)

## Baseline and observations

The user's mouse is a VXE R1 Pro (3554:F58C), set to 1000 Hz, connected to
the Waveshare RP2350-USB-C PIO port. Failures occur even with the application
closed. The user explicitly confirmed that the archived local v1.8 rollback
restores working passthrough/polling; that is the baseline, not a failed test.
The earlier occasional freeze remains a separate, unresolved hardware finding.

Baseline source: `581f189` (the original-pattern multiplier candidate).
Known-good rollback SHA256:
`6AF79744B80C9F770208AE5037E3E14511D593501742B9FEB8473C469432AFE4`.

| Build marker | Actual observation | Conclusion / limitation |
| --- | --- | --- |
| Original v1.8 rollback | Passthrough/polling confirmed working; earlier occasional freezes | Reference behavior. Not evidence of indefinite reliability. |
| `HOST-STALL-TEST-X2-20260922` | No enumeration; 30 RX flag timeouts, no reports | Rejected. Changed EOP reset, timer waits, flag ordering, and disconnect handling together. |
| `HOST-RX-FIX-X2-20260922` | Enumerated but severe jitter; callback increases 322/280, rejected increases 248/249 | Rejected. Removing the flag-clear wait did not resolve the shared timing changes. |
| `HOST-RX-HANDSHAKE-X2-20260922` | User reports still not working; rollback works | Rejected. Restoring the flag loop with timer reads was insufficient. |
| `HOST-BASELINE-GUARDS-X2-20260923` | Local compilation/software tests only | Current candidate; physical passthrough, polling and soak acceptance pending. |

The first freeze capture showed `HOST_TASK_AGE_MS` growing beyond a minute
while host-report and outgoing HID counters stopped. This implicates host-core
progress, but does not locate an exact instruction. A later Windows semaphore
timeout happened while opening COM4, before a STATUS request could run. It
cannot identify a particular firmware wait or rule out Windows/upstream USB.

## Documentation cross-check

[Upstream PR #206](https://github.com/sekigon-gonnoc/Pico-PIO-USB/pull/206)
is still unmerged and reports device-recognition regressions. A September 3
comment specifically identifies the timer-reading TX wait as incompatible
with several devices. The proposal's soak test covered RP2040 MIDI, not RP2350.
Increasing the numerical timeout does not remove the timer-read overhead.
This is a strong lead for the new regression, not a hardware-proven root cause.

The rollback already includes the deterministic EOP-tail fix associated with
[issue #197](https://github.com/sekigon-gonnoc/Pico-PIO-USB/issues/197).
Reintroducing the transient program-counter polling loop would be a regression.
[Issue #192](https://github.com/sekigon-gonnoc/Pico-PIO-USB/issues/192)
also describes continuous RX data keeping a receive loop alive and allowing
its signed index to wrap. That failure can be bounded by packet capacity
without inserting another timer read on every FIFO poll.

### Correction to earlier diagnosis

`HOST_DECODE_ERRORS` does **not** prove corrupt USB bytes. In the pinned
Adafruit TinyUSB `hid_host.c`, `hidh_xfer_cb` ignores the transfer result and
invokes the report callback even when zero bytes were transferred. The
application's old counters counted those callbacks as reports and decode
errors. Descriptor/report-ID rejection also contributes to that total.

Neither resetting the EOP detector nor removing the flag-clear loop has been
individually proven to cause the observed failure. The previous notes claiming
that either change demonstrably corrupted reports were too strong.

## Current changes relative to the rollback

- Retain the original DMA/IRQ ordering, normally continuously running EOP detector,
  disconnect check, CPU frequency, USB descriptor and main-loop pacing.
- Keep the existing fractional-divider EOP-tail fix.
- Bound TX completion/PRE and RX flag waits with inlined, register-only
  polling. No added clock reads in those waits. The one-million-poll limit is
  a fault guard, **not** a calibrated microsecond timeout; its duration varies
  with CPU speed, code and preemption. It can delay service during a fault.
- Return explicit failure from transfer/token/RX-start functions. IN, OUT
  and SETUP transactions retry through the existing three-attempt policy.
  OUT checks the validated handshake rather than a stale receive-buffer byte.
  A failed ACK transmission cannot report a successful IN packet.
- Restart the RX/EOP state machines only after an exhausted RX flag budget or
  oversized packet, discard that transaction and let the next retry re-arm RX.
  This keeps recovery off the healthy-packet path, unlike the first failed build.
- Reject an RX packet that would exceed the 128-byte buffer, before writing
  or incrementing past the limit. This prevents indefinite babbling/index
  wrap without the failed candidates' per-packet timer. Existing USB protocol
  response and inter-byte timers remain unchanged.
- Add empty-callback and oversized-packet counters with matching desktop
  parser/UI support. No recoil, profile, jitter, delta-noise or configuration
  math changes; the local v1.8 original-pattern multiplier remains intact.

This is not a claim that every possible hardware/SDK wait is now bounded.
In particular, TX fault recovery still uses the SDK's DMA-abort operation.
Physical detach/reconnect, low-speed hubs, sustained throughput and the
original rare freeze need real-device verification.

## Diagnostics

`HOST_REPORTS` counts HID callbacks, not necessarily valid mouse reports.
`HOST_DECODE_ERRORS` remains the total rejected callbacks for compatibility.
New `HOST_EMPTY_REPORTS` is the zero-length subset (commonly failed transfers,
but not proof of the cause). New `PIO_RX_OVERSIZE` counts capacity rejections.
Between two samples, accepted-report count is approximately the change in
`HOST_REPORTS - HOST_DECODE_ERRORS`; compare while continuously moving the
mouse. Idle mice can NAK, and counters are sampled independently across cores.

`PIO_TX_TIMEOUTS` and `PIO_RX_FLAG_TIMEOUTS` now count exhausted poll budgets.
Legacy `PIO_RX_PACKET_TIMEOUTS` and `PIO_SE0_GLITCHES` are retained as zero
because those experimental features were removed; zero is not evidence that
the electrical bus never glitched. `REPORT_INTERVAL_US` is the requested
output interval, not a measured end-to-end polling-rate guarantee.

## Verification and acceptance

Local verification: both RP2040 and RP2350 firmware builds; UF2 family and
embedded BUILD checks; 42 Python tests; 40 .NET core tests; native HID replay
including fake-register completion/stuck-flag tests and RX capacity/babble
fixtures. The extended METRICS line fits its buffer even at maximum counters.
Disassembly inspection confirms TX and RX-clear polling use register reads
and decrement/branch instructions, not timer MMIO. These checks cannot model
PIO electrical timing or certify a real mouse.

Test package: `dist/ZeroSense-v1.9.0-validation/`. Do not publish it as
a stable release until physical acceptance succeeds:

1. Keep a backup mouse directly connected to the PC. Close ZeroSense.
2. Flash only `Firmware/ZeroSense-RP2350-USB-C-1.9.0-validation.uf2` using BOOTSEL.
3. First check movement, left/right/side buttons, wheel, and sustained 1000 Hz
   movement with the app closed, using the same setup as the rollback.
4. If passthrough is stable, open this package's `App/zerosense.exe` for the
   updated diagnostic display; verify normal arm/disarm and existing settings.
5. Close the app and run `tools/read_usb_status.ps1 -Port COM4`. Check BUILD,
   rising accepted reports during motion, low host-task age, and fault counters.
6. Include idle/resume, mouse unplug/replug, and a sustained run longer than
   the usual freeze interval. Stop testing immediately if movement becomes
   unreliable; reflash `Rollback/CONFIRMED-GOOD-RP2350-X2.uf2`.

No COM port was opened, no board was flashed, and no release was published by
the local software verification. Hardware acceptance is still pending.
