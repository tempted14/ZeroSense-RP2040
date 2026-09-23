# Changelog

## Unreleased (1.8.0 candidate)

- Add a persistent, adjustable 1–4× Original weapon pattern output multiplier,
  defaulting to 2×. It scales both X and Y of stock estimated patterns after
  the firmware profile's 127-point transfer limit while leaving the stored
  pattern and timing intact. Modified and measured profiles, plus General,
  Experimental, and Research modes, do not receive this multiplier.
- Raise the exact app/firmware sensitivity-factor contract to 16× so the
  maximum original-pattern gain can combine with the existing 2.5× vertical
  optic boost. Keep configuration validation and exact readback fail-closed.

## 1.7.0 - 2026-09-22

- Add a persistent 1.00–4.00× vertical boost scoped to automatic weapons using
  the 2.5× optic. Apply it through the device sensitivity scale after the
  127-point profile limit, so a saturated master gain can still increase real
  output. Scale General-mode smoothing velocity and acceleration together so
  its old 40-count cap does not erase the requested boost. Default remains 1×.
- Replace the RP2350 PIO USB host's racy EOP program-counter poll with a
  bounded four-bit-time wait, including the RP2350's fractional PIO divider.
  This targets a reported host-core hang that matches live freeze telemetry;
  hardware soak validation is still pending.
- Mark the RP2350 mouse unavailable when its host core stops completing tasks,
  release any cached physical buttons, and revoke generated output instead of
  continuing to show a stale connected state.
- Keep a quarantined RP2350 mouse interface retryable after sustained receive
  submission failures, with bounded backoff and explicit re-arm after a fault.
- Prevent a stalled CDC reader or serial input flood from monopolizing the HID
  loop; expose queue failures, unmounts, host-loop age and dropped
  replies in diagnostics. Add a STATUS-only capture tool for app-closed freezes.
- Ignore delayed callbacks from replaced app connections, preserve complete
  diagnostic lines, and fail CI immediately after an unsuccessful restore/test.
- Recover an RP2350 downstream mouse when its HID interrupt receive request is
  temporarily dropped instead of leaving COM connected with frozen input.
- Bound the PIO-USB host task wait, poll board/mouse health every two seconds,
  expose receive-recovery telemetry, and distinguish a live board from an
  unavailable downstream mouse in the app.
- Repeat RP2350 board and downstream-mouse identity on every status request so
  Arm Output does not remain disabled when startup messages precede the app's
  serial read loop.
- Preserve a newly mounted HID interface until TinyUSB's authoritative unmount
  callback fires; querying the device-wide mounted flag from inside the first
  post-callback host iteration could otherwise discard a valid wired mouse.
- Raise the master recoil calibration default from 3.50x to 12.00x and make
  global and per-weapon output independently editable from 0 through the real
  127-unit HID/Q8.8 limit.
- Add persistent per-weapon horizontal controls that can retain the resolved
  profile trace, mirror it, disable lateral correction, or apply deterministic
  left-pull, right-pull, and alternating overrides with an independent 0-10x
  strength. Source and measured profiles remain immutable.

## 1.6.0 - 2026-09-21

- Restore exact floating-point configuration acknowledgements in both firmware
  targets so the RP2350 and RP2040 UF2s synchronize with the desktop client.
- Add a visible 0.50-4.00 master recoil gain with a 3.50 default based on the
  first physical RP2350 calibration, while preserving the original pattern
  shape and a separate per-weapon fine trim.
- Normalize default ADS calibration against each optic's own reference value so
  2.5x, 3.5x, and 8.0x are not incorrectly weakened relative to 1.0x.
- Retry a lost configuration acknowledgement, tolerate isolated transient CDC
  read errors, and automatically attempt to reconnect a genuinely lost device.
- Keep reconnect fail-safe: generated movement stops immediately and output
  remains disarmed until the user explicitly arms it again.
- Retain optional timing variance, balanced delta noise, device-local RP2350
  M1+M2 activation, and additive physical mouse pass-through.

## 1.5.0 - 2026-09-20

- Add a separately selectable Y11S1.3 Research profile derived from the supplied
  per-weapon data while preserving the original profile catalog unchanged.
- Resolve the Research profile against the exact operator, loadout, optic, and
  measured-profile source before applying its staged recoil envelope; unsupported
  XK23 data falls back explicitly instead of inventing measurements.
- Add an A/B profile-source control and immediately refresh the visible profile
  quality and provenance when its selection changes.
- Add a default-off General timing-variance control for the retained ±8% cadence
  option, including transactional firmware synchronization and exact readback.
- Add default-off balanced ±2–3 HID-count delta noise to generated compensation
  only, with repayment of the opposite delta to prevent random-walk drift and no
  modification of the downstream physical-mouse contribution.
- Replace update-count-dependent smoothing with elapsed-time scaling and
  exponential friction, and use absolute fixed-point scheduling for smoother,
  drift-resistant per-shot compensation.
- Extend simulator, protocol-contract, recoil-model, native scheduler, and delta-
  noise regression coverage, and surface the app version and configuration schema
  in the title area for easier app/firmware compatibility checks.

## 1.4.0 - 2026-09-20

- Normalize General-mode smoothing and displacement to actual elapsed time and
  disable optional cadence jitter in official builds for deterministic output.
- Spread semi-automatic per-shot correction across the exact RPM interval with
  the same fixed-point 1 ms scheduler used by automatic patterns.
- Resolve measured patterns against the complete attachment, optic, and optional
  operator context instead of allowing a newer unrelated optic to shadow a match;
  ambiguous game-build matches now fail closed.
- Replace developer-oriented in-app setup instructions with the prebuilt release
  path, add a one-download app-and-firmware starter bundle, and make updater
  asset/signature wording accurate.
- Debounce settings persistence and device synchronization during rapid edits,
  and add keyboard/accessibility metadata to the primary UI controls.
- Report HID backpressure, queue high-water marks, active report gaps, downstream
  report/decode counts, accumulator saturation, and upstream safety stops on the
  Device page.
- Stop immediately and discard generated movement if the PC-side USB link is
  lost; RP2350 also revokes its local arm lease without clearing physical input.
- Centralize v1.4 version metadata, reduce release-job permissions, add release
  contract checks, pin GitHub Actions to immutable commits, generate SPDX release
  SBOMs and provenance attestations, and update CI firmware builds to PlatformIO
  6.2.0.
- Clearly label Estimated, Experimental, and Measured profile data in the UI and
  document the RP2350's supported 1000 Hz downstream mouse configuration.

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
