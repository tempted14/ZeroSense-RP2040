# Waveshare RP2350-USB-C / RP2350-USB-A mouse proxy

This guide supports the Waveshare **RP2350-USB-C**, SKU 34641, with two
female USB-C connectors, and **RP2350-USB-A**, with native USB-C and female USB-A.
It is not for the similarly named RP2350-USB-CM board
with a male plug. The firmware pinout follows Waveshare's official GPIO12 D+ /
GPIO13 D- routing for the female PIO-USB connector.

## USB-A board: separate UF2 and hardware setup

Choose `ZeroSense-RP2350-USB-A-<version>.uf2`, not the USB-C image. The shared
proxy uses the USB-A schematic's GPIO12 D+ / GPIO13 D- routing and 2 MB flash.
The app recognizes `DEVICE:RP2350-USB-A:MOUSE-PROXY` and uses the same local
M1+M2 activation, configuration protocol, profiles and additive output.

```text
PC <-- data cable --> [USB-C | RP2350-USB-A | USB-A] <-- mouse/receiver
                       native CDC + HID    PIO host
```

**Hardware limitation:** Waveshare's [official FAQ](https://www.waveshare.com/wiki/RP2350-USB-A)
requires removal of **R13** for PIO-USB host hot-plugging. After removal that port
cannot be used as a USB device. Check your board revision with Waveshare, and
have a qualified technician handle the change if necessary. Unplug all power
before any hardware work. Software cannot remove a soldered resistor. The
USB-C board's CC1/CC2 instructions below do not apply to USB-A.

Flash through USB-C using BOOT/RESET. Confirm `Hardware: Waveshare RP2350-USB-A
mouse proxy` and a connected mouse in the app. Run the safe first-test checklist
below and `python tools/hil_rp2350.py COMx --board usb-a` with the app closed.
The USB-A target is compiled/tested in software, but has not been qualified with
your physical USB-A board and mouse while that board is disconnected.

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

## USB-C board only: cable and CC setup

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

1. Download the USB-C or USB-A UF2 matching your exact board from the intended
   release. Validation prereleases may not be listed as GitHub's latest release.
2. Disconnect the mouse during the first flash.
3. Hold `BOOT`, connect the native PC-side port, and release `BOOT` when the
   `RPI-RP2` drive appears. With the board already connected, hold `BOOT`, tap
   `RESET`, then release `BOOT`.
4. Copy the RP2350 UF2 to `RPI-RP2`. The drive ejects automatically.
5. Wait for Windows to enumerate a composite device, COM port, and HID mouse.
6. Connect the mouse to the PIO-USB port and open ZeroSense.

On the Device page, confirm both of these before arming:

- `Hardware: Waveshare RP2350-USB-C mouse proxy` or the USB-A equivalent
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
5. Run `python tools/hil_rp2350.py COMx` for CRC/timeout recovery and identity checks
   (add `--board usb-a` for that board);
   add `--interactive` to validate raw device-local M1+M2 start/release. Use the
   app's simulator for board-free protocol checks. The simulator intentionally
   produces no HID input and does not simulate electrical USB timing.
6. Only after those checks, arm output and confirm physical motion can both add
   to and oppose the generated movement.

## Verification boundary

All three firmware targets are compiled in CI, the exact firmware HID decoder is
replayed natively against boot, report-ID, split-report, 16-bit, unknown-ID,
and malformed fixtures, and the Windows app has a board-free protocol
simulator. A software-only test cannot validate connector power, CC resistors,
signal integrity, or a specific mouse's descriptor. Final acceptance therefore
requires the short physical checklist above on the actual board and mouse.

Official board documentation: <https://docs.waveshare.com/RP2350-USB-C>

## Wireless receiver troubleshooting

A 2.4 GHz receiver can relay its standard USB HID mouse interface; proprietary
pairing, RGB, battery-status and configuration interfaces are not forwarded.
Pair the mouse to its receiver directly on the PC first and use 1000 Hz or less.
The `RP2350A` chip name does not identify the connector variant: select USB-C
firmware for two USB-C sockets, or USB-A firmware for one USB-C plus USB-A socket.

The receiver validation build expands TinyUSB's configuration/report descriptor
buffer from 256 to 1024 bytes for both proxy targets, without changing USB
polling, PIO timing, recoil, jitter or noise. Larger descriptors remain subject
to this bounded limit; boot fallback still requires receiver support.

Close ZeroSense and run `tools/read_usb_status.ps1 -Port COMx -Samples 3`.
The receiver validation build additionally reports `HOST_BUS`:

- `INITIALIZED=1, CONNECTED=0, DEVICES=0`: host started, but no physical USB
  attach is recognized. Check the correct mouse-side socket, data adapter,
  USB-C plug orientation and the board's documented CC role. Descriptor parsing
  cannot repair a missing electrical connection.
- `CONNECTED=1, DEVICES=0`: physical attach recognized but enumeration has not
  completed. The adapter, power, signal integrity or control transfers need
  further investigation; do not assume this is a mouse report format problem.
- `DEVICES` and `HID_MOUNTS` greater than zero but `MOUSE:DISCONNECTED`: inspect
  the mouse report descriptor and any explicit unsupported-interface message.
- `MOUSE:CONNECTED` and increasing accepted report counters: test movement,
  buttons and wheel with output disarmed before enabling compensation.

`DP`/`DM` are sampled digital USB data-line levels, not a voltage/power test.
The PIO input inversion is undone when reporting these levels. `FULL_SPEED`
is the host's detected bus speed. `DESC_SEEN` counts full device descriptors
received; zero with a recognized attach indicates failure before a full device
descriptor, not a failure to parse the HID mouse report. `CONFIG_LEN` is the
last received full configuration descriptor length. `HOST_IN_ERROR` captures
the last failed IN's cached header/address/endpoint; it is not a power diagnosis.
Mount counters are cumulative; `DEVICES` is the current mounted-device count.
Diagnostics are sampled outside the packet/SOF timing path every 100 ms.

## Output parity and pacing

The RP2350 and RP2040 builds share the same compensation generator, exact
sensitivity scaling, 40-count velocity ceiling, 0.85 friction, actual elapsed
time integration, exact-RPM pattern scheduler, and 250/500/1000 Hz adaptive
HID pacing. Official UF2s use a deterministic 8 ms General-mode cadence; the
retained ±8% timing-jitter path is disabled by default. The practical
high-activity rate is commonly about 900 Hz after USB
and host overhead. RP2350 physical movement, wheel, pan, or button transitions
select the 1 ms path immediately; the slower idle interval is used only when no
physical report is waiting, so mouse pass-through responsiveness is preserved.

USB full-speed limits the upstream endpoint to one report per 1 ms frame. The
decoder accumulates relative deltas from faster downstream reports rather than
silently dropping them, but a 4 kHz or 8 kHz mouse cannot retain native 4/8 kHz
latency through this proxy and those rates have not been hardware-qualified.
Configure the mouse to 1000 Hz for the supported setup.

The intentional differences are activation and transport: RP2350 reads raw
M1+M2 locally and merges the hosted mouse report, whereas RP2040 receives the
foreground-gated trigger from the app and acts as a standalone HID device.
