# ZeroSense v2.0.0 — RP2350 stability validation prerelease

This build has software/build verification, but the affected VXE R1 Pro and
Waveshare RP2350-USB-C still require a real-device soak test. **It is not a
claim that the occasional mouse freeze is fully resolved.** Keep a backup
mouse directly connected to the PC and the previously usable UF2 available.

## RP2350 passthrough and polling

- The host-core watchdog continues to release buttons and disarm generated
  output after a 500 ms host-task stall. If the host task still has made no
  progress after 3 seconds, it requests a one-shot full-board watchdog reset.
  This restores the host controller to a known boot state instead of leaving
  a permanently frozen mouse. It briefly disconnects/re-enumerates both USB
  interfaces; it is a fallback, not an electrical or PIO root-cause repair.
  After such a reset, STATUS includes `LAST_RESET:HOST_WATCHDOG` so the event
  is distinguishable from a cable unplug or manual reboot.
- RP2350's main loop now checks the upstream HID endpoint every 100 us rather
  than sleeping at least 1 ms per iteration. HID sends remain capped by the
  existing 1 ms high-activity interval, so this removes avoidable scheduling
  delay without claiming more than USB full-speed's 1 kHz frame rate.
- STATUS now reports `HOST_SOF_FRAMES`, `HOST_IN_ATTEMPTS`, and
  `HOST_IN_NAKS`. The app shows their rates alongside accepted mouse reports,
  empty callbacks, and outgoing HID reports. IN attempts reveal whether the
  PIO host is actually polling frequently; NAKs can simply mean that the
  mouse had no changed report to send. Accepted-report and HID-send rates are
  **not** the same as the mouse's configured polling rate.

## Compatibility and limits

The previous recoil profiles, first-bullet kick, per-weapon multipliers,
RP2350 local M1+M2 activation, physical passthrough, optional variance, and
optional delta noise are unchanged. No firmware configuration schema or
saved-settings format changed. Update the app and board UF2 together to see
the new telemetry. The firmware reports
`BUILD:V2.0-HOST-WATCHDOG-20260923` in STATUS.

The watchdog cannot detect a host loop that is still running while an
individual mouse endpoint silently stops completing. If the mouse freezes
again, close ZeroSense and capture the `BUILD`, `MOUSE`, and `METRICS` lines
with `tools/read_usb_status.ps1 -Port COM4 -Samples 3` **before unplugging**,
using the actual COM port if it differs. Compare `HOST_TASK_AGE_MS`,
`HOST_SOF_FRAMES`, `HOST_IN_ATTEMPTS`, `HOST_IN_NAKS`, `HOST_UNMOUNTS`,
`HOST_REPORTS`, `HOST_DECODE_ERRORS`, `HOST_EMPTY_REPORTS`, `HID_SENT`, and
`HID_BUSY` across the samples. The diagnostic script does not arm, reset, or
configure the board. If COM cannot be opened, record that error too.

For hardware acceptance, test idle/resume, rapid mouse motion, all buttons,
wheel and pan, physical unplug/replug, and a session longer than the typical
freeze interval at 1000 Hz with ZeroSense closed. Repeat at 500 Hz only as an
isolation test if 1000 Hz still freezes. Do not call this build stable until
those checks pass on the affected board and mouse.
