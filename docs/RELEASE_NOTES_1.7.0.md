# ZeroSense v1.7.0

This release improves RP2350 mouse-host diagnostics and recovery, adds a
separate 2.5× automatic-weapon vertical boost, and retains the existing
RP2040 and RP2350 configuration protocol.

## RP2350 mouse passthrough

- Apply a targeted fix to Pico-PIO-USB's unbounded transmit EOP wait. A live
  freeze capture showed the host loop stopped while COM remained responsive;
  this change targets a matching upstream issue. The upstream library fix is
  not yet merged, and this specific board/mouse still needs a long hardware
  soak test.
- When the host loop stops making progress, report `MOUSE:HOST_ERROR`, release
  cached buttons, and disarm generated output instead of claiming the mouse is
  still connected. Retain bounded receive retries and expanded STATUS metrics.
- Include a read-only `tools/read_usb_status.ps1` capture tool for collecting
  telemetry with the app closed and mouse frozen.

## Recoil controls

- Add a saved **2.5× auto vertical boost**, 1.00–4.00×, default 1.00×. It only
  applies to automatic weapons while the selected optic is 2.5×, and it acts
  after the 127-point source-profile limit. General-mode velocity and
  acceleration limits now follow sensitivity calibration, so they no longer
  discard the requested vertical boost.
- Keep the original and Research profile datasets unchanged. The separate
  horizontal controls, master/per-weapon gains, optional timing variance, and
  balanced delta noise remain available.
- The separately requested 2× change to the supplied recoil patterns is **not**
  part of v1.7.0; it will be developed after this release snapshot.

## Installation and verification

- Download the starter bundle for the easiest install, or select the RP2040
  or RP2350 UF2 separately for the exact board. Install the v1.7.0 app and
  matching firmware together. The RP2350 build reports
  `BUILD:HOST-FIX-OPTIC-20260922` in STATUS.
- Both firmware targets compile, UF2 family checks pass, the Windows portable
  app publishes, and automated core, contract, and native HID replay tests
  pass. Real-device USB-host stability and recoil strength cannot be certified
  by simulation; please validate with a safe test setup before relying on it.
- The portable app is unsigned unless the release also offers a separately
  signed installer. Verify `SHA256SUMS.txt` before installing.
