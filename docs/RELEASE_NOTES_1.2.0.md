# ZeroSense v1.2.0

This release adds a second, board-specific UF2 for the two-female-port
**Waveshare RP2350-USB-C (SKU 34641)** while retaining the original
RP2040-Zero firmware.

## RP2350 mouse proxy

- Physical mouse hosted on the female PIO-USB connector through GPIO12/13.
- Native USB-C remains the Windows CDC + HID composite connection.
- Physical movement and generated movement are merged additively at a 1 ms
  upstream service interval.
- Forwards relative X/Y, up to eight buttons, vertical wheel, and horizontal
  pan; supports report IDs and common 8/16/32-bit fields with boot fallback.
- START, STOP, and the generated-output watchdog do not clear physical input.
- Active rapid fire substitutes only the left-button bit; other physical input
  remains live.
- The Device page shows board identity, downstream mouse VID:PID, disconnect,
  unsupported-descriptor, and host-error state. RP2350 arming is disabled until
  mouse forwarding is ready.

## Install

Download the Windows ZIP and exactly one UF2:

- RP2040-Zero: `rainbow-recoil-rp2040-zero-1.2.0.uf2`
- Waveshare RP2350-USB-C: `rainbow-recoil-rp2350-usb-c-1.2.0.uf2`

For RP2350, read `Docs/RP2350_MOUSE_PROXY.md` in the Windows ZIP or the
[repository guide](https://github.com/tempted14/ZeroSense-RP2040/blob/v1.2.0/docs/RP2350_MOUSE_PROXY.md).
The PC uses the native port beside `BOOT`/`RESET`; the mouse uses the opposite
female PIO-USB port. Its CC1 and CC2 selection must be `1 / Source`.

## Verification

- 27/27 .NET core tests passed.
- 19/19 Python contract/profile tests passed.
- Windows x64 self-contained publish compiled with zero warnings and passed a
  responsive-startup smoke test.
- Both PlatformIO targets compiled successfully.
- UF2 framing and family IDs validated independently: RP2040 `0xE48BFF56`,
  RP2350 `0xE48BFF59`.

The RP2350 board and mouse were not physically attached during development.
Software tests cannot validate connector power, CC resistors, signal integrity,
or every vendor-specific mouse descriptor. Complete the included desktop
hardware checklist before relying on the proxy. Vendor configuration/RGB
interfaces and buttons above eight are not forwarded.

The fair-play warning in the repository still applies: use generated-input
features only in an offline, private, or otherwise explicitly permitted test
environment.
