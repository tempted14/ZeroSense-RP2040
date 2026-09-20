# USB identity migration

Older revisions configured the board with USB identifiers and product strings
belonging to unrelated commercial devices. The current firmware intentionally
does not do that.

## Current behavior

`RP2040_Firmware/platformio.ini` leaves USB VID/PID selection to the supported
Arduino-Pico `waveshare_rp2040_zero` board definition. The sketch uses descriptive
Rainbow Recoil manufacturer/product strings for its own interfaces.

This has three practical benefits:

- Device Manager accurately describes the connected hardware.
- Driver and interface behavior follows the board package's tested defaults.
- A core or TinyUSB update cannot silently create a mismatch between a borrowed
  VID/PID, the advertised device class, and the actual CDC + HID interfaces.

## Migrating an already-flashed board

1. Build the current `waveshare_rp2040_zero` environment.
2. Enter UF2 mode by holding `BOOT`, tapping `RESET`, and releasing `BOOT` when
   `RPI-RP2` appears.
3. Flash the newly built `rainbow_recoil.uf2`.
4. Allow Windows to enumerate the replacement composite device.
5. Start the desktop app and select **Scan for device**.

The app still recognizes the legacy VID families during migration, but it accepts
a serial port only after receiving `PONG:RAINBOW-RECOIL:4`. If registry discovery
is unavailable, it tries present COM ports in numeric order and applies the same
handshake.

## Troubleshooting stale Windows entries

Changing USB descriptors may cause Windows to allocate a new COM number. That is
normal. Use Device Manager's **View > Show hidden devices** only if you want to
remove an obsolete, disconnected entry; do not remove the active composite, CDC,
or HID interfaces.

The ROM loader remains a separate device (`VID_2E8A&PID_0003`) and has no runtime
COM interface. Seeing `RPI-RP2` means the application firmware is not currently
running.
