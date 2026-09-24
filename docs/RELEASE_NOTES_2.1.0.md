# ZeroSense v2.1.0 — RP2040-Zero validation prerelease

The RP2040-Zero and RP2350-USB-C firmware images are separate. Flash **only**
the UF2 whose board name matches yours. This release passed software builds and
simulated tests, but the RP2040-Zero was not connected for a real-device test.
Do not interpret this prerelease as a guarantee of hardware reliability.

## RP2040-Zero

- The RP2040 is a separate CDC + relative-HID output device. Keep your normal
  mouse connected directly to the PC; RP2040-Zero has no mouse passthrough.
- It now uses the full ±127 HID X/Y range declared by its descriptor instead
  of the previous ±100 per-report cap. Oversized pending output is saturated
  rather than allowed to wrap around.
- Its HID service loop now runs at sub-frame granularity while retaining the
  existing 250/500/1000 Hz idle/normal/active target intervals.
- General and weapon patterns, first-bullet kick, per-weapon gain, optional
  timing variance and delta noise, rapid fire, exact configuration readback,
  and the 750 ms keepalive fail-safe remain available. RP2040 activation uses
  the Windows app's foreground-gated M1+M2 monitor, **not** the RP2350's
  device-local raw mouse trigger.

## Windows app and RP2350

- A Windows USB-serial driver stall during COM open/close no longer blocks the
  app's UI dispatcher. Open attempts time out after three seconds and later
  retries wait for an incomplete native operation to finish. This protects app
  responsiveness; it does not repair a wedged Windows USB driver or mouse-side
  PIO-USB host.
- RP2350 retains its v2.0 host watchdog and mouse proxy. The shared HID range
  and bounded generated queue changes also apply to its generated output.
  Intermittent RP2350 mouse freezes still require physical diagnosis.

Firmware STATUS identifies this build as `BUILD:V2.1-RP2040-HID-20260924`.
Use the matching v2.1 app and UF2 together. The portable Windows ZIP is
unsigned unless the release also contains a separately signed installer.

## Required physical acceptance

With a backup mouse directly attached to the PC, verify the RP2040 boots as
CDC+HID, the app synchronizes its configuration, Arm Output works, M1+M2 starts
and release stops movement, both General and Pattern modes follow the selected
profile, optional variance/noise toggles and rapid fire behave as configured,
and unplugging/closing the app stops generated output. Do not rely on the UF2
for an important session until these checks pass on your board.
