# ZeroSense v1.6.0

## RP2350/RP2040 synchronization repair

- Both PlatformIO targets now explicitly retain newlib-nano floating-point
  `printf` support. Exact profile acknowledgements such as `V=1.837` and
  `H=0.000` are therefore complete instead of silently losing their values.
- The regression suite verifies this linker contract so a future UF2 cannot
  reintroduce the initial-synchronization failure unnoticed.
- Configuration protocol schema 3 is unchanged. The v1.6 app and matching v1.6
  UF2s are nevertheless distributed together to ensure the repaired firmware is
  installed.

## Recoil calibration

- A visible **Master Recoil Gain** control scales the complete X/Y output while
  preserving every pattern's direction and per-shot shape. Its supported range
  is 0.50x to 4.00x and the physical RP2350 calibration default is 3.50x.
- The existing per-weapon output strength remains an independent fine trim. The
  app shows the resulting total gain in the main view and compact overlay.
- Default ADS calibration is normalized against the reference value for the
  selected optic. The previous implementation compared every optic with 1.0x
  ADS 38, which unintentionally reduced 2.5x output to about 57 percent before
  any profile correction was applied.
- Settings migration preserves the user's timing-variance and delta-noise
  choices. Neither option modifies physical RP2350 pass-through input.

## Connection reliability and safety

- Configuration acknowledgements receive up to three bounded attempts before a
  synchronization failure is reported.
- An isolated transient USB CDC read exception no longer tears down an otherwise
  open connection. Repeated failures still fail closed.
- A genuine device loss triggers three bounded automatic reconnect attempts.
  Successful recovery resynchronizes the selected configuration but never
  restores the armed state automatically.
- RP2350 raw M1+M2 activation, immediate mouse-disconnect stopping, upstream USB
  fail-safe behavior, and additive physical input remain unchanged.

## Verification

- Windows core/simulator and serial reliability tests pass.
- Firmware, profile, starter-bundle, and release-contract tests pass.
- Both PlatformIO firmware targets compile and are validated for the correct UF2
  target-family identifier.
- The self-contained Windows x64 package builds with centralized v1.6.0 metadata.

The physical test performed before this release confirms downstream mouse
pass-through and device-local M1+M2 activation on the Waveshare RP2350-USB-C.
Real-world gain still depends on in-game vertical/ADS sensitivity and should be
fine-tuned in the shooting range. Display resolution does not directly scale
relative HID counts.
