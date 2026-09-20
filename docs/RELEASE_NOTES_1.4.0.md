# ZeroSense v1.4.0

## Installation and update safety

- The in-app installation page now describes the prebuilt release path instead
  of requiring the .NET SDK and PlatformIO.
- Installation and troubleshooting guides ship beside the portable app.
- A checked release also includes one starter bundle containing the portable
  app, quick-start instructions, and clearly separated firmware for both boards.
- Update results report starter-bundle, installer, and checksum availability
  without assuming that an installer filename proves an Authenticode signature.
- Release version metadata is centralized, release jobs use least-privilege
  permissions, sensitive certificate/key file extensions are ignored, all
  GitHub Actions are pinned to immutable commits, and release assets receive
  GitHub build-provenance attestations plus an SPDX 2.3 asset SBOM.

## Recoil and measured profiles

- General-mode velocity smoothing, friction, and displacement use the actual
  elapsed integration time. Official builds use deterministic timing; the
  retained ±8% jitter option is disabled by default.
- Semi-automatic rapid-fire correction is distributed over each exact RPM
  interval instead of arriving as one movement impulse.
- Measured patterns are selected only after the complete weapon, grip, barrel,
  optic, and optional operator context is known.
- A newer measurement for a different optic can no longer shadow an older exact
  match. Operator-specific measurements take priority over generic measurements.
- Duplicate exact loadouts from different game builds now fail closed to the
  estimate until an exact game build is requested.

## Desktop quality

- Rapid edits are debounced into one settings write and one device
  synchronization, reducing disk and serial-command churn.
- The loadout card now labels the active data as Estimated, Experimental, or
  Measured and shows its source.
- Core navigation, loadout, device, arming, update, and overlay controls expose
  accessible names; primary navigation includes keyboard access keys.
- The Device page now shows cumulative HID backpressure, queue, active-gap, and
  RP2350 downstream decode/saturation telemetry plus upstream-disconnect safety
  stops after configuration sync.
- Loss of the PC-side USB connection now stops output immediately, clears stale
  generated movement, and revokes the RP2350 arm lease before reconnection.
- PlatformIO 6.2.0 is pinned for CI and tagged-release firmware builds.
- RP2350 documentation now states the supported 1000 Hz downstream mouse setup;
  4/8 kHz input is accumulated but cannot retain native latency through a USB
  full-speed upstream endpoint and remains unqualified without hardware.

## Verification before release

- Windows app: clean Release build with warnings treated as errors.
- Core simulator/regression suite: 33 tests.
- Firmware/profile and release contract suite: 33 tests.
- Firmware: RP2040-Zero and RP2350-USB-C targets compile successfully, and both
  UF2 files validate against their expected target-family identifiers.

Hardware-in-the-loop acceptance still requires the physical RP2350 board and a
real downstream mouse; software simulation cannot verify Type-C CC strapping,
signal integrity, device-specific HID descriptors, or electrical disconnects.
