# ZeroSense v1.4.0

## Installation and update safety

- The in-app installation page now describes the prebuilt release path instead
  of requiring the .NET SDK and PlatformIO.
- Installation and troubleshooting guides ship beside the portable app.
- Update results distinguish an installer asset from a cryptographically signed
  installer and report whether `SHA256SUMS.txt` is present.
- Release version metadata is centralized, release jobs use least-privilege
  permissions, and sensitive certificate/key file extensions are ignored.

## Recoil and measured profiles

- General-mode velocity smoothing, friction, and displacement are normalized to
  elapsed update time, so ±8% interval jitter no longer changes average output.
- Measured patterns are selected only after the complete weapon, grip, barrel,
  optic, and optional operator context is known.
- A newer measurement for a different optic can no longer shadow an older exact
  match. Operator-specific measurements take priority over generic measurements.

## Desktop quality

- Rapid edits are debounced into one settings write and one device
  synchronization, reducing disk and serial-command churn.
- Core navigation, loadout, device, arming, update, and overlay controls expose
  accessible names; primary navigation includes keyboard access keys.
- PlatformIO 6.2.0 is pinned for CI and tagged-release firmware builds.

## Verification before release

- Windows app: clean Release build with warnings treated as errors.
- Core simulator/regression suite: 33 tests.
- Firmware/profile contract suite: 24 tests.
- Firmware: RP2040-Zero and RP2350-USB-C targets compile successfully, and both
  UF2 files validate against their expected target-family identifiers.

Hardware-in-the-loop acceptance still requires the physical RP2350 board and a
real downstream mouse; software simulation cannot verify Type-C CC strapping,
signal integrity, device-specific HID descriptors, or electrical disconnects.
