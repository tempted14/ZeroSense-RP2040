# ZeroSense 2.2.0 — USB recovery validation candidate

## Changes

- Windows CDC now uses cancelable overlapped I/O. Teardown aborts outstanding
  transfers without the unbounded transmit flush performed by `SerialPort.Close`.
  Buffers and port ownership remain reserved until cancellation really completes.
- Automatic reconnect uses three quick attempts, then a 15-second background
  retry. Recovery never re-arms output; manual Reconnect supersedes old retries.
- Firmware serial, HID, and CDC flush operations coordinate with the native USB
  background task through Adafruit's USB mutex. Explicit task servicing prevents
  skipped task interrupts from leaving control transfers waiting indefinitely.
- A two-second main-loop watchdog restarts genuinely hung firmware disarmed.
  Quiet mice and idle CDC do not trigger it. RP2350's separate host-core recovery
  remains active; watchdog resets are reported in diagnostics.
- Waveshare RP2350-USB-A has its own UF2, board identity, build/CI target, starter
  bundle asset and app setup guidance. USB-C goes to the PC; USB-A hosts the mouse.
- `read_usb_status.ps1` runs captures in a hidden bounded child process, so a
  Windows driver open/close stall cannot leave the diagnostic shell waiting forever.
  It is included under `Tools` in the portable app as well as in the source.

Profiles, strengths, horizontal patterns, first-bullet kick, time-based smoothing,
optional General variance, balanced delta noise and varied 16-CPS rapid fire are
retained. This change does not increase multipliers or alter profile data.

## Hardware requirements and limits

Waveshare documents that USB-A host hot-plugging requires removal of R13. This is
a physical board change, not a firmware option; after removal that PIO port cannot
operate as a USB device. Consult the official FAQ and a qualified technician if
needed. Do not change a powered board. The USB-C board's CC selectors are separate
and do not apply to the USB-A board.

This remains a validation candidate, not a guarantee against all USB faults.
The connected RP2040 can be tested here; the unplugged RP2350-USB-A's power,
enumeration, hot-plug behavior and mouse passthrough require physical validation.
Use 1000-Hz downstream mice; native 4/8-kHz passthrough is not supported.

## Validation evidence

On the connected RP2040-Zero, the previous v2.1.4 firmware stalled after 74
connect/status/close cycles using the new transport. The recovery UF2 completed
1,000 cycles with no reported watchdog reset. A separate 100-transaction test
verified exact configuration readback and hashes while alternating automatic
and semi-auto profiles, rapid fire, variance, delta noise, and first-bullet kick.
No test armed output or generated physical mouse movement. Three diagnostic
captures then returned complete lines with `FIRE=OFF` and `CDC_DROPPED=0`.

All three firmware targets compile and the native HID replay fixtures pass.
The 55 core regression tests, 46 Python contract/packaging tests and 200 live
CDC read/close cancellation races pass. The Windows package is self-contained
but unsigned; no Authenticode signature or GitHub attestation is claimed for
this locally built candidate.
These checks do not prove in-game behavior, sustained active-output reliability,
or RP2350-USB-A hardware compatibility; those still require physical use.

Sources: [Adafruit RP2040 USB task mutex](https://github.com/adafruit/Adafruit_TinyUSB_Arduino/blob/3.7.7/src/arduino/ports/rp2040/Adafruit_TinyUSB_rp2040.cpp),
[Windows cancellation lifetime rules](https://learn.microsoft.com/en-us/windows/win32/api/ioapiset/nf-ioapiset-cancelioex),
[.NET SerialStream close/flush implementation](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialStream.Windows.cs),
[Waveshare USB-A FAQ](https://www.waveshare.com/wiki/RP2350-USB-A),
[Waveshare USB-A schematic](https://files.waveshare.com/wiki/RP2350-USB-A/RP2350-USB-A.pdf).
