# Rainbow Recoil firmware

This directory contains the canonical firmware for a TENSTAR/Waveshare
RP2040-Zero. It presents one native USB composite device:

- CDC ACM for the Windows application's command channel.
- A relative HID mouse for the configured output.

The firmware uses the USB identity supplied by the Arduino-Pico Waveshare board
definition. Its deliberately neutral descriptive strings are manufacturer
`RP2040`, product `RP2040 USB Mouse`, and HID interface `USB Mouse`. It does not
claim another manufacturer's VID, PID, or product name.

## Required configuration

| Item | Value |
|---|---|
| Board | TENSTAR RP2040-Zero / Waveshare RP2040 Zero-compatible |
| Flash | 2 MB |
| Arduino core | Earle F. Philhower Arduino-Pico |
| Board menu | `Waveshare RP2040 Zero` |
| USB stack | `Adafruit TinyUSB` |
| Arduino CLI FQBN | `rp2040:rp2040:waveshare_rp2040_zero:usbstack=tinyusb` |

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
```

The helper accepts `pio` on `PATH`, PlatformIO's standard virtual environment,
or a local Python installation containing the `platformio` module.

## Build with Arduino IDE or CLI

Add this Boards Manager URL:

```text
https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
```

Install **Raspberry Pi Pico/RP2040/RP2350 by Earle F. Philhower III**, select
**Waveshare RP2040 Zero** and **Adafruit TinyUSB**, then verify or export the
sketch.

The equivalent Arduino CLI commands are:

```powershell
arduino-cli core update-index --additional-urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
arduino-cli core install rp2040:rp2040 --additional-urls https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
arduino-cli compile --fqbn rp2040:rp2040:waveshare_rp2040_zero:usbstack=tinyusb --output-dir .\arduino-build .\rainbow_recoil
```

## Flash

1. Connect the board with a data-capable cable.
2. Hold `BOOT`, tap `RESET`, and release `BOOT` when `RPI-RP2` appears.
3. Run `.\flash-firmware.bat`, or copy the Waveshare-targeted UF2 to the root
   of `RPI-RP2`.
4. Wait for the volume to eject and the board to restart.

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

Payloads are limited to 63 bytes. Oversized frames are consumed and rejected so
the parser is synchronized for the next valid frame. A pattern profile is not
allowed to start until every declared point has arrived in order.

General mode emits one movement step every 8 ms. Pattern mode emits one point per
shot using `60,000,000 / RPM` microseconds and stops at the loaded pattern length.
Fractional movement is accumulated so sub-count values are retained.
Supported semi-automatic profiles pulse the left-button report at their configured
rate and emit one recoil step per pulse. While output is active, the desktop app
sends a keepalive every 250 ms; missing keepalives stop all output after 750 ms.
