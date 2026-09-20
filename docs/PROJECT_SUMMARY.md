# Project summary

## Supported hardware

Supported targets are the 2 MB TENSTAR/Waveshare RP2040-Zero and the 2 MB,
two-female-port Waveshare RP2350-USB-C (SKU 34641). The similarly named
RP2350-USB-CM male-plug board has a different PIO D+/D- order and is not a
supported target.

Both targets use native USB toward Windows as one composite device:

- CDC ACM carries commands between the Windows app and firmware.
- HID exposes a relative mouse interface.

Windows 10 and 11 provide the composite, CDC serial, and HID class drivers. No USB-to-UART bridge is used.

RP2350 additionally hosts a physical mouse on its GPIO12/13 PIO-USB connector
and merges that mouse's standard input with generated movement. Host work runs
on core 1; native device reports and the serial protocol remain on core 0.

## Repository layout

```text
RainbowRecoil\
├── build.ps1                              Windows app build wrapper
├── WindowsApp\
│   ├── App.xaml / App.xaml.cs             WinUI application bootstrap
│   ├── MainPage.xaml / MainPage.xaml.cs   UI and device connection flow
│   ├── Rp2040DeviceDiscovery.cs           Native RP2040 CDC discovery
│   ├── FirmwareStatusParser.cs             Board/mouse health protocol parser
│   ├── IRecoilDeviceConnection.cs         Testable device boundary and metrics
│   ├── SimulatedRecoilDeviceConnection.cs Board-free protocol/configuration harness
│   ├── ConfigurationValidator.cs          Fail-closed output validation
│   ├── DiagnosticLog.cs                   Bounded, screenshot-free event report
│   ├── DetectionDebouncer.cs              Consecutive OCR confirmation
│   ├── SerialConnection.cs                Length-prefixed command transport
│   ├── MouseButtonTrigger.cs              Physical aim + fire state polling
│   ├── RecoilAttachmentModel.cs           Attachment formula/operator overrides
│   ├── SiegeCatalog.cs                    Y11S3 115-weapon/operator catalog
│   ├── WeaponProfiles.cs                  Runtime numeric profile baselines
│   ├── WeaponPatternCatalog.cs             Timed pattern estimates for automatics
│   ├── RainbowRecoil.csproj               .NET 8 / Windows App SDK project
│   └── build.ps1                          Self-contained x64 publisher
├── RP2040_Firmware\
│   ├── rainbow_recoil\rainbow_recoil.ino  Canonical Arduino source
│   ├── src\main.cpp                       PlatformIO entry point for that source
│   ├── boards\waveshare_rp2350_usb_c.json Exact RP2350 2 MB board definition
│   ├── platformio.ini                     Both Arduino-Pico environments
│   ├── build.bat                          PlatformIO build helper
│   └── flash-firmware.bat                 RPI-RP2 UF2 copy helper
└── docs\                                  Setup and troubleshooting guides
```

The removed Pico-SDK/CMake, Make, and custom UF2-converter paths were not valid build routes. Arduino IDE/CLI and PlatformIO now compile the same canonical sketch.

## Firmware configuration

Arduino IDE can build the RP2040 target with:

```text
Package:   Raspberry Pi Pico/RP2040/RP2350 by Earle F. Philhower III
Board:     Waveshare RP2040 Zero
USB stack: Adafruit TinyUSB
FQBN:      rp2040:rp2040:waveshare_rp2040_zero:usbstack=tinyusb
```

Use PlatformIO for RP2350; it selects the custom board, 120 MHz clock,
`ZEROSENSE_RP2350_USB_C`, and pinned Pico-PIO-USB dependency. The sketch starts
CDC and one upstream HID interface for either build.

PlatformIO uses the same Arduino-Pico core through:

```ini
platform = https://github.com/maxgerhardt/platform-raspberrypi.git#5d4561a
board = waveshare_rp2040_zero
board_build.core = earlephilhower
framework = arduino
build_flags = -DUSE_TINYUSB

[env:waveshare_rp2350_usb_c]
board = waveshare_rp2350_usb_c
board_build.f_cpu = 120000000L
build_flags = -DUSE_TINYUSB -DZEROSENSE_RP2350_USB_C
```

## Build outputs

From `RP2040_Firmware`:

```powershell
pio run -e waveshare_rp2040_zero -e waveshare_rp2350_usb_c
```

creates:

```text
.pio\build\waveshare_rp2040_zero\firmware.uf2
.pio\build\waveshare_rp2350_usb_c\firmware.uf2
```

`build.bat` also creates `rainbow_recoil.uf2` and
`rainbow_recoil_rp2350_usb_c.uf2`.

From `WindowsApp`:

```powershell
.\build.ps1
```

creates the self-contained app folder at:

```text
WindowsApp\artifacts\win-x64\
```

Run `zerosense.exe` from that folder and keep the accompanying files together. The project explicitly publishes its WinUI PRI resource file because it is required to load the XAML UI.

## Flash and enumeration states

To enter the ROM UF2 loader while connected, hold `BOOT`, press and release `RESET`, then release `BOOT` when the `RPI-RP2` drive appears. Copy the Waveshare-targeted UF2 to that drive. A valid copy causes the drive to eject and the board to restart automatically.

| State | Windows view | USB identity |
|---|---|---|
| ROM loader | `RPI-RP2` removable drive only | `VID_2E8A&PID_0003` |
| Firmware | USB Composite Device, USB Serial Device (COMx), HID-compliant mouse | VID `2E8A`; runtime PID varies with interface composition |

The desktop app does not assume a fixed COM number or look for a bridge-chip name. It prioritizes present CDC ports whose PnP hardware key uses Raspberry Pi VID `2E8A`, prefers the last successful port, then requires the firmware-specific `PING`/`PONG` identity handshake. If registry discovery is restricted, all present COM ports are filtered by that same handshake. The boot-loader state is ignored because it has no COM interface.

## Command protocol

Desktop-to-firmware packets are:

```text
[A5 5A][version][sequence u16 LE][command][length][payload][CRC16-CCITT u16 LE]
```

| Command | Byte | Payload |
|---|---:|---|
| PING | `F0` | Empty; firmware replies `PONG:RAINBOW-RECOIL:4` |
| START | `F1` | RP2040 only; RP2350 activation is raw-device-local |
| STOP | `F2` | Empty |
| PROFILE | `F3` | Version 2 mode/baselines/RPM/count/name; version 1 remains accepted |
| SENSITIVITY | `F4` | Horizontal float followed by vertical float, little-endian |
| PATTERN | `F5` | Versioned chunks containing Q8.8 horizontal/vertical point pairs |
| RAPID_FIRE | `F6` | Enabled flag plus semi-automatic rate in RPM |
| KEEPALIVE | `F7` | Empty; refreshes the active-output safety watchdog |
| ARM_LEASE | `F8` | RP2350 foreground/armed lease; does not replace raw M1+M2 activation |
| CONFIG_BEGIN / COMMIT / ABORT | `F9` / `FA` / `FB` | Transaction ID, rollback, and canonical configuration hash |
| STATUS | `FC` | Current hash, transaction state, and output state |
| RESET | `FF` | Empty; enter UF2 mode |

The current app sends version 2 profiles inside an all-or-nothing v4 configuration transaction. The firmware snapshots the last-good state and commits only after exact readbacks, a complete pattern, and an identical canonical FNV-1a hash. Frames use a sync word, sequence, bounded one-byte length, CRC-16, and partial-frame timeout recovery. Pattern points remain bounded chunks. General mode schedules movement every 8 ms; pattern mode uses phase-locked `60,000,000 / RPM` deadlines and distributes each point across 1 ms frames with Q16 remainder preservation. Experimental calibration remains host-side.

Arming output does not start HID movement. RP2040 uses the Windows foreground-gated M1+M2 monitor and 750 ms keepalive. RP2350 receives only a short app lease: the board itself activates from raw downstream M1+M2, stops immediately on raw release or interface disconnect, and never uses its synthesized upstream rapid-fire report as trigger input.

## User calibration and roster

The checked-in calibration defaults match the supplied `GameSettings.ini` input values: yaw/pitch `55`, `MouseSensitivityMultiplierUnit=0.001000`, 1600 DPI, and ADS values 38/67/72/74 for 1.0x/2.5x/3.5x/8.0x. Automatic optic selection applies 2.5x to attackers, 1.0x to defenders, and 2.5x to the defender DMR exceptions TCSG12, Tubarão's AR-15.50, and Aruni's Mk 14 EBR; disabling it enables a persistent manual selection. Display resolution and aspect ratio are not used because USB mouse reports are relative counts; FOV is likewise not part of the direct per-optic ADS scaling. At the default `0.02` multiplier, the hip-fire value is equivalent to 2.75.

All 115 weapons in the current Y11S3 catalog are individually selectable, including SIX12 SD. Sixty-one automatic weapons have current RPM/magazine timing and deterministic, staged estimates derived from recent weapon/attachment footage, the supplied 2025 reference tables, official balance notes, and maintained public statistics. Reaper uses Ubisoft's published current stage boundaries at bullets 0/3/10/25; the numeric stage magnitudes remain explicitly estimated. Supported semi-automatic weapons have explicit rapid-fire rates and non-zero per-shot recoil. Every automatic profile applies Vertical Grip. Attachment modifiers are centralized: vertical grip and flash hider modify vertical compensation, compensator modifies estimated horizontal movement, and muzzle brake modifies the first pattern point. The current F2 setup is vertical grip plus flash hider; the SCORPION EVO 3 A1 uses vertical grip while retaining Compensator as its barrel exception. The source hierarchy and calculation invariants are documented in `docs/RECOIL_DATA_SOURCES.md`.

Automatic profiles apply Vertical Grip and prefer Flash Hider anywhere they previously selected Compensator, with Ela's SCORPION EVO 3 A1 retaining Compensator as the sole barrel exception. Older saved profiles are normalized to those preferences when loaded. Shared-weapon attachment availability is resolved after operator selection. Aruni's Mk 14 EBR and Tubarão's AR-15.50 do not receive the Y11S3-removed muzzle brake, while Dokkaebi and Maverick retain it on attack. Supported semi-automatic profiles instead apply recoil once per generated shot; pump/manual weapons do not enable rapid fire.

These are starting estimates, not authoritative recoil vectors. Ubisoft describes multi-stage recoil behavior but does not publish numeric per-shot coordinates, and ordinary gameplay video includes player correction. Physical shooting-range validation is still required after balance changes.

The opt-in Experimental mode stores independent first-shot, early, middle, late, and horizontal gains per weapon. Its `1.00` defaults reproduce the standard pattern exactly, reset removes only the selected weapon's tuning, and General/standard Pattern sources are never mutated. This isolates iterative shooting-range calibration from the known-stable modes while preserving a General fallback for profiles without automatic patterns.

A separate neutral-default per-weapon output multiplier scales General baselines, automatic pattern points, Experimental results, and semi-automatic per-shot correction without mutating catalog data. Custom automatic profiles use their effective RPM and magazine size when their deterministic curve is generated, keeping the host pattern and firmware scheduler consistent. Settings migrations are saved atomically after a successful load, and the legacy armed flag is always cleared because arming is session-only.

The client now depends on a small device interface implemented by both the real
CDC transport and an explicit no-HID simulator. Output configuration is
validated before every synchronization, continuous OCR changes require two
matching scans, and active output stops when Siege loses foreground focus. A
bounded diagnostic report exposes command/acknowledgement counts, serial and OCR
latency, memory use, and recent failures. Custom profile saves keep a validated
rolling backup and automatically preserve/recover a corrupt primary file.

## Verification baseline

The checked-in configuration is intended to be verified with both:

```powershell
arduino-cli compile --fqbn rp2040:rp2040:waveshare_rp2040_zero:usbstack=tinyusb .\rainbow_recoil
dotnet build .\RainbowRecoil.csproj -c Release
```

Physical USB verification additionally requires the actual board: confirm the loader drive, successful UF2 reboot, runtime COM interface, HID interface, and app connection status on the target Windows machine.
