# Waveshare RP2350-USB-C mouse proxy

This guide is only for the Waveshare **RP2350-USB-C**, SKU 34641, with two
female USB-C connectors. It is not for the similarly named RP2350-USB-CM board
with a male plug. The firmware pinout follows Waveshare's official GPIO12 D+ /
GPIO13 D- routing for the female PIO-USB connector.

## What the firmware does

```text
physical mouse
    -> female PIO-USB connector (GPIO12/13, host on core 1)
    -> physical reports + ZeroSense-generated deltas
    -> native USB-C connector (CDC + HID)
    -> Windows
```

Physical X/Y motion is never replaced by generated motion. Opposing deltas can
cancel naturally and same-direction deltas add. Movement larger than one HID
packet is drained over consecutive 1 ms reports. The proxy forwards relative
movement, eight standard buttons, vertical wheel, and horizontal pan. Rapid fire
only substitutes the left-button bit while it is actively pulsing; other
buttons and all movement continue to pass through.

The firmware parses up to four mouse HID interfaces and their report layouts,
including split button/movement report IDs and 8/16/32-bit relative fields.
Movement-only IDs preserve button state from button-only IDs. If a boot-capable mouse has an unparseable or
oversized descriptor, it falls back to boot mouse mode. Vendor-only interfaces,
RGB/profile programming, wireless-dongle management, and buttons above eight
are not mirrored to Windows.

## Cable and CC setup

1. Leave the board unpowered while inspecting or changing solder selectors.
2. Use the USB-C connector beside the `BOOT` and `RESET` buttons as the native
   connection to the Windows PC.
3. Use the opposite female connector beside the `CC1`/`CC2` selector area for
   the mouse.
4. For a mouse attached to the female PIO-USB port, both CC selectors must be in
   the silkscreen's `1 / Source` position. Never bridge both `0` and `1` on one
   selector. If the purchased board is already configured as Source, do not
   resolder it.
5. Use data-capable USB-C cables. The PC connection must also supply enough bus
   power for the board and mouse; avoid a high-current device or unpowered hub.

The CC setting describes the PIO-USB connector's Type-C role. The firmware
cannot change a soldered CC resistor selection in software.

## Flash the board

1. Download `ZeroSense-RP2350-USB-C-1.3.0.uf2` from the latest release.
2. Disconnect the mouse during the first flash.
3. Hold `BOOT`, connect the native PC-side port, and release `BOOT` when the
   `RPI-RP2` drive appears. With the board already connected, hold `BOOT`, tap
   `RESET`, then release `BOOT`.
4. Copy the RP2350 UF2 to `RPI-RP2`. The drive ejects automatically.
5. Wait for Windows to enumerate a composite device, COM port, and HID mouse.
6. Connect the mouse to the PIO-USB port and open ZeroSense.

On the Device page, confirm both of these before arming:

- `Hardware: Waveshare RP2350-USB-C mouse proxy`
- `Physical mouse: VVVV:PPPP · forwarding movement, 8 buttons, wheel, and pan`

The app keeps arming disabled while the proxy reports the mouse disconnected,
an unsupported descriptor, or a PIO-USB host error.

## Safe first test

Do this on the Windows desktop, with ZeroSense disarmed:

1. Move slowly and quickly in every direction; the pointer must follow without
   axis reversal, clipping, or a dead direction.
2. Check left, right, middle, back, forward, wheel, and horizontal pan where the
   mouse provides them.
3. Hold a button while moving, then unplug the mouse. Windows must receive a
   clean button release and the app must show `Physical mouse: disconnected`.
4. Reconnect the mouse and confirm forwarding resumes.
5. Run `python tools/hil_rp2350.py COMx` for CRC/timeout recovery and identity checks;
   add `--interactive` to validate raw device-local M1+M2 start/release. Use the
   app's simulator for board-free protocol checks. The simulator intentionally
   produces no HID input and does not simulate electrical USB timing.
6. Only after those checks, arm output and confirm physical motion can both add
   to and oppose the generated movement.

## Verification boundary

Both firmware targets are compiled in CI, the exact firmware HID decoder is
replayed natively against boot, report-ID, split-report, 16-bit, unknown-ID,
and malformed fixtures, and the Windows app has a board-free protocol
simulator. A software-only test cannot validate connector power, CC resistors,
signal integrity, or a specific mouse's descriptor. Final acceptance therefore
requires the short physical checklist above on the actual board and mouse.

Official board documentation: <https://docs.waveshare.com/RP2350-USB-C>

## Output parity and pacing

The RP2350 and RP2040 builds share the same compensation generator, exact
sensitivity scaling, 40-count velocity ceiling, 0.85 friction, General-mode
±8% cadence jitter, exact-RPM pattern scheduler, and 250/500/1000 Hz adaptive
HID pacing. The practical high-activity rate is commonly about 900 Hz after USB
and host overhead. RP2350 physical movement, wheel, pan, or button transitions
select the 1 ms path immediately; the slower idle interval is used only when no
physical report is waiting, so mouse pass-through responsiveness is preserved.

The intentional differences are activation and transport: RP2350 reads raw
M1+M2 locally and merges the hosted mouse report, whereas RP2040 receives the
foreground-gated trigger from the app and acts as a standalone HID device.
