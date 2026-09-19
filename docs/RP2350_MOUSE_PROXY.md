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

The firmware parses normal HID report descriptors, including report IDs and
8/16/32-bit relative fields. If a boot-capable mouse has an unparseable or
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

1. Download `rainbow-recoil-rp2350-usb-c-1.2.0.uf2` from the latest release.
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
5. Use the app's simulator for protocol checks. The simulator intentionally
   produces no HID input and does not simulate electrical USB timing.
6. Only after those checks, arm output and confirm physical motion can both add
   to and oppose the generated movement.

## Verification boundary

Both firmware targets are compiled in CI, the RP2350 descriptor/mixing/status
contracts are regression-tested, and the Windows app has a board-free protocol
simulator. A software-only test cannot validate connector power, CC resistors,
signal integrity, or a specific mouse's descriptor. Final acceptance therefore
requires the short physical checklist above on the actual board and mouse.

Official board documentation: <https://docs.waveshare.com/RP2350-USB-C>
