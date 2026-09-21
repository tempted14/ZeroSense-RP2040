# ZeroSense v1.5.0

## Recoil profiles and controls

- A new **Y11S1.3 Research** profile source is available beside the original
  ZeroSense catalog. The original profiles and their source data are unchanged.
- The Research source uses the supplied weapon data to shape staged vertical
  compensation while preserving the selected profile's total output and
  horizontal trace. It resolves the exact operator, grip, barrel, optic, and
  measured-profile source before applying that shape.
- The profile-source selector makes original-versus-research A/B testing explicit,
  and the profile quality/provenance label refreshes immediately when switched.
- Weapons without supplied research data, including the XK23, use an explicit
  original-profile fallback rather than fabricated measurements.

## Timing variance and delta noise

- **Timing variance** is an optional, default-off General-mode setting. When
  enabled it applies the retained ±8% interval variance; its state is included in
  transactional configuration synchronization and exact firmware readback.
- **Delta noise** is an optional, default-off setting that applies balanced ±2–3
  HID-count perturbations only to generated compensation. A later report repays
  each perturbation, preventing an accumulating random-walk offset.
- Physical RP2350 mouse movement is not noised. Saturated or busy HID reports do
  not consume physical input or falsely mark a noise delta as delivered. STOP,
  disconnect, and configuration changes clear any pending noise repayment so no
  generated movement can leak past the safety boundary.

## Recoil engine and reliability

- General-mode velocity, acceleration, friction, and displacement now scale from
  actual elapsed time instead of update count. Friction is exponential, so output
  remains consistent when the scheduler interval varies.
- Pattern and semi-automatic correction use absolute fixed-point deadlines and
  smooth 1 ms delivery across the real shot interval, reducing cadence drift and
  one-frame impulses.
- App/firmware configuration schema 3 carries the new controls. Update the app and
  the matching board UF2 together; mismatched schemas fail closed rather than
  silently accepting a partial configuration.
- The title area shows the app version and configuration schema to make support
  and compatibility checks easier.

## Verification

- Windows core/simulator and protocol tests pass.
- Firmware/profile/release-contract Python tests pass.
- Both RP2040-Zero and Waveshare RP2350-USB-C firmware targets compile, and their
  UF2 outputs are checked for the correct target-family identifier.
- The self-contained Windows x64 app publishes and passes a clean startup smoke
  test. The tagged GitHub workflow also runs the native fake-clock scheduler and
  delta-noise fixture before creating release assets.

Physical hardware-in-the-loop acceptance still requires the RP2350 board and a
real downstream mouse. Simulation cannot prove USB-C CC wiring, electrical signal
integrity, every real-world HID descriptor, or disconnect timing on the specific
hardware. The Research profile is experimental user-supplied data, not an official
game measurement, and is labeled accordingly in the app.
