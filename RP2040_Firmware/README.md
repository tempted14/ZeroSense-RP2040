# Rainbow Recoil firmware

This directory contains one canonical sketch with two compile-time targets:

| Environment | Board | Role |
|---|---|---|
| `waveshare_rp2040_zero` | TENSTAR/Waveshare RP2040-Zero, 2 MB | Standalone CDC + HID output |
| `waveshare_rp2350_usb_c` | Waveshare RP2350-USB-C SKU 34641, 2 MB | Additive PIO-USB mouse proxy + CDC + HID |

Both present one native USB composite device to Windows:

- CDC ACM for the Windows application's command channel.
- A relative HID mouse for the configured output.

The RP2350 target hosts the mouse on core 1 through Pico-PIO-USB, GPIO12 D+ and
GPIO13 D-, then queues decoded physical reports for core 0. Core 0 merges those
reports with generated movement before transmitting one upstream HID stream.
Only rapid fire's active left-button pulse is substituted; other physical
buttons, movement, wheel, and pan remain live.

## Required configuration

| Item | Value |
|---|---|
| Board | RP2040-Zero or Waveshare RP2350-USB-C (not USB-CM) |
| Flash | 2 MB |
| Arduino core | Earle F. Philhower Arduino-Pico |
| RP2040 board menu | `Waveshare RP2040 Zero` |
| RP2350 build | Checked-in PlatformIO board definition |
| USB stack | `Adafruit TinyUSB` |
| PIO-USB library | Commit `5a37a66dc5d3fbe0ef3cdbeda923a757440f984f` |
| RP2350 system clock | 120 MHz |

The canonical sketch is `rainbow_recoil\rainbow_recoil.ino`.
`src\main.cpp` includes that sketch so Arduino and PlatformIO do not maintain
separate implementations.

## Build with PlatformIO

Install PlatformIO Core and Git, then run from this directory:

```powershell
.\build.bat
```

The configuration pins the PlatformIO platform fork to a known commit. Outputs
are written to:

```text
.pio\build\waveshare_rp2040_zero\firmware.uf2
rainbow_recoil.uf2
.pio\build\waveshare_rp2350_usb_c\firmware.uf2
rainbow_recoil_rp2350_usb_c.uf2
```

The helper accepts `pio` on `PATH`, PlatformIO's standard virtual environment,
or a local Python installation containing the `platformio` module.

## Build RP2040 with Arduino IDE or CLI

Add this Boards Manager URL:

```text
https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
```

Install **Raspberry Pi Pico/RP2040/RP2350 by Earle F. Philhower III**, select
**Waveshare RP2040 Zero** and **Adafruit TinyUSB**, then verify or export the
sketch. Use PlatformIO for RP2350 because its target also selects the custom
2 MB board definition, 120 MHz clock, compile guard, and pinned PIO-USB library.

The equivalent Arduino CLI commands are:

```powershell
arduino-cli core update-index --additional-urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
arduino-cli core install rp2040:rp2040 --additional-urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
arduino-cli compile --fqbn rp2040:rp2040:waveshare_rp2040_zero:usbstack=tinyusb --output-dir .\arduino-build .\rainbow_recoil
```

## Flash

1. Connect the board with a data-capable cable.
2. Hold `BOOT`, tap `RESET`, and release `BOOT` when `RPI-RP2` appears.
3. Copy the UF2 for the exact board to the root of `RPI-RP2`.
4. Wait for the volume to eject and the board to restart.

Run `flash-firmware.bat rp2040` or `flash-firmware.bat rp2350`; omission keeps
the backward-compatible RP2040 default. See
[`docs/RP2350_MOUSE_PROXY.md`](../docs/RP2350_MOUSE_PROXY.md) before connecting
the downstream mouse.

The RP2040 ROM loader uses `VID_2E8A&PID_0003`. Runtime PID and interface suffixes
can vary with the core version and composite layout; the desktop app discovers
the present COM port and verifies it with the protocol handshake.

## Serial protocol

Every host-to-device frame is:

```text
[payload length: uint16 little-endian][command: uint8][payload]
```

| Command | Byte | Payload |
|---|---:|---|
| PING | `F0` | Empty; replies `PONG:RAINBOW-RECOIL:3` |
| START | `F1` | Empty; resets and starts the loaded burst |
| STOP | `F2` | Empty |
| PROFILE | `F3` | Versioned mode, compensation, timing, and name data |
| SENSITIVITY | `F4` | Horizontal and vertical IEEE-754 floats |
| PATTERN | `F5` | Sequential Q8.8 horizontal/vertical point chunks |
| RAPID_FIRE | `F6` | Enabled byte and little-endian uint16 RPM |
| KEEPALIVE | `F7` | Empty; refreshes the active-output watchdog |
| RESET | `FF` | Empty; enters the UF2 boot loader |

After `PONG`, current firmware also emits a board identity line. RP2350 emits
mouse-host health changes asynchronously:

```text
DEVICE:RP2350-USB-C:MOUSE-PROXY
MOUSE:CONNECTED:VID=vvvv:PID=pppp
MOUSE:DISCONNECTED
MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR
MOUSE:HOST_ERROR
```

Payloads are limited to 63 bytes. Oversized frames are consumed and rejected so
the parser is synchronized for the next valid frame. A pattern profile is not
allowed to start until every declared point has arrived in order.

General mode emits one movement step every 8 ms. Pattern mode emits one point per
shot using a phase-locked `60,000,000 / RPM` interval and stops at the loaded
pattern length. Pattern points are interpreted as HID counts, sensitivity is
applied once, and pattern output bypasses the general-mode velocity smoothing.
Fractional movement is accumulated so sub-count values are retained.
Supported semi-automatic profiles pulse the left-button report at their configured
rate and emit one recoil step per pulse. While output is active, the desktop app
sends a keepalive every 250 ms; missing keepalives stop all output after 750 ms.

On RP2350, physical reports use a 1 ms upstream service interval even while
generated output is idle. Relative fields are decoded from the attached mouse's
HID report descriptor, including report IDs and common 8/16/32-bit field sizes.
The upstream descriptor exposes eight buttons plus X, Y, wheel, and pan. Larger
deltas are retained and drained rather than clipped away. STOP clears generated
output state but never clears pending physical input.
