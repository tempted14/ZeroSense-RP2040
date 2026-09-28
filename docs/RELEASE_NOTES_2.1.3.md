# ZeroSense v2.1.3 — Windows freeze recovery validation prerelease

**Use the v2.1.3 Windows app.** This fixes a remaining major UI freeze path:
START, STOP, keepalive/status, arm-lease, and initial configuration writes could
still block the UI inside the Windows USB serial driver. Moving only COM
open/close off the UI in earlier patches did not fix those live writes.

## What changed

- A single ordered background writer handles all live serial commands.
  Enqueueing a command never waits for a native USB write on the UI thread.
- A 750 ms write deadline reports a stalled session without waiting for the
  driver to return. The pending queue is capped at 32 frames; commands waiting
  500 ms are rejected as stale. These are fault limits, not added send delays.
- A fault discards unsent commands, including START and keepalives, rather than
  playing them back when the driver recovers. Output is disarmed through the
  existing connection-loss path and firmware watchdog/session fail-safe.
- Reconnect retains the per-port guard until old reads, writes, and close
  actually finish. A timeout is not mistaken for cancellation of a native call.
- Even a stuck final STOP cannot prevent attempting port close. This cleanup
  runs off the UI thread; permanently stuck Windows driver operations may still
  require unplugging/replugging the board once.
- Configuration is only accepted after exact readback/commit verification, not
  merely after enqueue. Sent-command counters count completed writes.

M1+M2, recoil profiles, gains, first-bullet kick, jitter, delta noise, saved
settings, and firmware USB/HID polling behavior are unchanged. The RP2040
two-mouse rapid-fire limitation documented in v2.1.1 is also unchanged.

## Downloads and firmware

The easiest download is `ZeroSense-2.1.3-Starter-Bundle.zip`. Extract it fully
and run `ZeroSense/zerosense.exe`; do not run the older executable alongside it.
If you only want the app, use `ZeroSense-2.1.3-Windows-x64.zip`.

Firmware is unchanged from v2.1.2 and both board-specific UF2s are included.
If v2.1.2 firmware is already flashed, no reflash is needed. Otherwise flash
`ZeroSense-RP2040-Zero-2.1.3.uf2` for RP2040-Zero, or the separately named RP2350
file for RP2350. The firmware BUILD marker intentionally remains
`V2.1.2-CDC-RECOVERY-20260927`; the Windows app version is 2.1.3.

## Validation and limits

52 .NET regression tests and 44 Python checks cover the app/protocol/tools,
including new fault injection against the actual serial connection. GitHub CI
also builds the Windows package and both firmware targets, checks UF2 families,
and runs native HID replay/scheduler tests before publication.

This is a validation prerelease. Software fault tests demonstrate that the UI
callers return while the fake driver is hung; they do not prove that your exact
physical cable, board, or driver failure will never recur. If it fails again,
copy the app diagnostic report and report whether the app stays responsive,
the exact serial error, and whether the COM port remains visible.

The portable app is unsigned unless a separately signed installer is present.
Use `SHA256SUMS.txt` to verify downloads. CI also publishes an SPDX SBOM and
artifact provenance attestations.
