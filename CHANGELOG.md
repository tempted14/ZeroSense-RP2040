# Changelog

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
