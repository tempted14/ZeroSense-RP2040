# Changelog

## 1.3.0 - 2026-09-19

- Gate RP2350 compensation from raw downstream M1+M2 plus a short host arm
  lease, with immediate mouse-disconnect, interface-fault, and release stops.
- Add CRC-framed protocol v4 transactions, rollback, exact acknowledgements,
  canonical hashes, parser timeout recovery, and a shared app/firmware range
  contract.
- Support four downstream HID interfaces, multiple report IDs, split reports,
  and signed 8/16/32-bit relative mouse fields through a fail-closed decoder.
- Smooth Q16 pattern output across full 1 ms intervals, carry fractional RPM
  timing without systematic drift, preserve the final queued correction, and
  prevent rapid fire from running alongside pattern mode.
- Cross-check current balance changes against Ubisoft notes, apply Reaper's
  published 0/3/10/25 stage boundaries, and add executable recoil invariants,
  HID replay fixtures, UF2 validation, and optional RP2350 HIL checks.
- Add exact-loadout measured profile packs, a configuration-sync service,
  official-release update checks, a per-user installer, and a fail-closed
  Authenticode release workflow.

## 1.2.0 - 2026-09-18

- Add a separate Waveshare RP2350-USB-C UF2 target for the exact 2 MB,
  two-female-port board.
- Host the downstream mouse through PIO-USB on GPIO12/13 and core 1 while native
  USB remains the Windows CDC + HID device.
- Decode report IDs and common 8/16/32-bit relative mouse fields, with boot
  mouse fallback; forward movement, eight buttons, wheel, and horizontal pan.
- Merge physical and generated deltas additively at a 1 ms upstream service
  interval, preserving physical input across START, STOP, and output watchdogs.
- Report board identity and mouse health to the Windows app, and keep RP2350
  arming disabled while its downstream mouse is unavailable or unsupported.
- Build both firmware environments in CI and add an RP2350 installation and
  physical-validation checklist.

## 1.1.0 - 2026-09-18

- Apply horizontal and vertical sensitivity exactly once in firmware.
- Preserve configured pattern points as HID counts instead of applying the old
  fixed-point rescaling, damping, movement noise, and general-mode smoothing.
- Schedule pattern and rapid-fire shots from phase-locked RPM deadlines to avoid
  cadence drift.
- Smooth vertical and horizontal transitions in generated staged patterns.
- Add regression coverage for recoil units, scheduling, pattern continuity,
  zero-drift HID output, and rapid-fire release behavior.

## 1.0.0 - 2026-09-18

- Initial tested ZeroSense desktop, simulator, documentation, and RP2040-Zero
  firmware release.
