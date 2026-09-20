# Changelog

## 1.4.0 - unreleased

- Normalize General-mode smoothing and displacement to elapsed time so cadence
  jitter does not change average correction strength.
- Spread semi-automatic per-shot correction across the exact RPM interval with
  the same fixed-point 1 ms scheduler used by automatic patterns.
- Resolve measured patterns against the complete attachment, optic, and optional
  operator context instead of allowing a newer unrelated optic to shadow a match.
- Replace developer-oriented in-app setup instructions with the prebuilt release
  path, add a one-download app-and-firmware starter bundle, and make updater
  asset/signature wording accurate.
- Debounce settings persistence and device synchronization during rapid edits,
  and add keyboard/accessibility metadata to the primary UI controls.
- Report HID backpressure, queue high-water marks, active report gaps, downstream
  report/decode counts, and accumulator saturation on the Device page.
- Centralize v1.4 version metadata, reduce release-job permissions, add release
  contract checks, and update CI firmware builds to PlatformIO 6.2.0.

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
- Apply the same 250/500/1000 Hz adaptive pacing on RP2350, with queued physical
  mouse reports promoted immediately to the 1 ms path.
- Cross-check current balance changes against Ubisoft notes, apply Reaper's
  published 0/3/10/25 stage boundaries, and add executable recoil invariants,
  HID replay fixtures, UF2 validation, and optional RP2350 HIL checks.
- Add exact-loadout measured profile packs, a configuration-sync service,
  official-release update checks, a portable Windows package, and an optional
  Authenticode-signed per-user installer that is never emitted unsigned.

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
