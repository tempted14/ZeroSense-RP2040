# ZeroSense v1.3.0

## Safety and protocol

- RP2350 activation now comes from raw downstream M1+M2 and requires a short
  foreground/armed lease from the app. Raw release, mouse-host failure, or
  interface disconnect revokes output immediately.
- Protocol v4 adds sync bytes, sequence numbers, CRC-16, partial-frame timeout
  recovery, configuration transaction IDs, rollback snapshots, exact readback,
  and canonical configuration hashes.
- App and firmware share the same compensation and sensitivity contract. The
  sensitivity safety range is widened to `0.05..8.0` and values are transferred
  exactly rather than silently clamped.

## Mouse proxy and movement

- The RP2350 host accepts up to four HID mouse interfaces, multiple report IDs,
  split button/movement reports, and signed 8/16/32-bit relative fields.
- The dependency-free HID decoder is shared by firmware and executable native
  replay fixtures.
- Pattern corrections are distributed smoothly across 1 ms frames while Q16
  remainder accounting preserves the configured per-shot total. Fractional
  microsecond RPM periods are carried into later shots, eliminating systematic
  interval rounding drift, and frame counts cover the full shot interval.
- General mode retains independent-axis velocity smoothing, 40-count maximum
  velocity, 0.85 friction, ±8% cadence jitter, and adaptive 250/500 Hz plus a
  1 kHz high-activity target (typically about 900 Hz after USB/host overhead).
- RP2350 now follows that same adaptive policy while immediately promoting any
  queued physical mouse input to the 1 ms path, preserving pass-through latency.

## Profiles, testing, and distribution

- Versioned measured-profile packs require source, game build, timestamp, exact
  weapon/grip/barrel/optic metadata, and bounded points. Built-in estimates are
  never relabeled as measured.
- Recoil data is cross-checked against current Ubisoft balance notes. Reaper now
  uses the published 0/3/10/25 stage boundaries, calculation/attachment
  invariants are executable tests, and normal pattern completion preserves the
  final queued fixed-point correction instead of clearing it.
- CI builds both UF2 targets, replays HID fixtures, and runs desktop/Python tests.
  `tools/hil_rp2350.py` adds non-destructive parser recovery and optional raw
  M1+M2 hardware-in-the-loop checks.
- The app includes an official-release update check. Every release includes a
  portable Windows ZIP. When an Authenticode certificate is configured, the
  workflow signs and verifies the optional installer; it never publishes an
  unsigned installer. SHA-256 checksums cover every downloadable binary.

## Assets

- `ZeroSense-1.3.0-Windows-x64.zip`
- `ZeroSense-Setup-1.3.0-win-x64.exe` when an Authenticode certificate is configured
- `ZeroSense-RP2040-Zero-1.3.0.uf2`
- `ZeroSense-RP2350-USB-C-1.3.0.uf2`
- `SHA256SUMS.txt`
