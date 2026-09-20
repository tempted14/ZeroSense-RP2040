# ZeroSense

This project contains a Windows 10/11 desktop application and two independently
built firmware targets:

- TENSTAR/Waveshare RP2040-Zero as a standalone CDC + HID output device.
- [Waveshare RP2350-USB-C](https://docs.waveshare.com/RP2350-USB-C), SKU
  34641 with two female USB-C ports, as an additive USB mouse proxy.

The RP2350 path is `mouse -> female PIO-USB port -> RP2350 -> native USB-C ->
Windows`. Physical movement, up to eight buttons, vertical wheel, and horizontal
pan are forwarded. Software-generated movement is added to the physical deltas,
so it does not lock or replace normal movement.

## What is in this repository

```text
RainbowRecoil\
├── RP2040_Firmware\
│   ├── rainbow_recoil\rainbow_recoil.ino   Arduino IDE source
│   ├── src\main.cpp                        PlatformIO entry point
│   ├── boards\waveshare_rp2350_usb_c.json  Exact 2 MB RP2350 board target
│   ├── platformio.ini                      RP2040 and RP2350 environments
│   ├── build.bat                           PlatformIO build helper
│   └── flash-firmware.bat                  RPI-RP2 copy helper
├── WindowsApp\                             .NET 8 / WinUI application
│   ├── SiegeCatalog.cs                     Current 115-weapon/operator roster
│   ├── WeaponProfiles.cs                   Runtime profile baselines
│   ├── WeaponPatternCatalog.cs             Timed automatic-weapon estimates
│   └── MouseButtonTrigger.cs               Aim + fire burst trigger
├── Tests\                                  Dependency-free core regression suite
├── tools\profile_editor.py                 Optional custom-profile editor
└── docs\
```

Both firmware targets present one TinyUSB composite device to Windows:

- CDC ACM provides the `USB Serial Device (COMx)` channel used by the Windows app.
- HID provides a relative mouse interface.

The RP2350 build additionally runs Pico-PIO-USB on core 1 using GPIO12/13, the
official wiring for the female PIO-USB connector. A descriptor-aware parser
handles report IDs and ordinary 8/16/32-bit relative mouse fields, with a boot
mouse fallback. No USB-to-UART bridge or third-party Windows driver is used.

> **Fair-play warning:** Ubisoft's current Siege player-protection rules prohibit
> hardware or software that runs recoil macros, rapid-fire scripts, or similar
> automation and allow account sanctions. Use this project only in an offline,
> private, or otherwise explicitly permitted test environment. See Ubisoft's
> [Player Protection Core Rules](https://www.ubisoft.com/th-th/game/rainbow-six/siege/news-updates/r8JQttOHXnT902HHbw7x3/player-protection-core-rules).

## Easiest install

Open the repository's [latest release](https://github.com/tempted14/ZeroSense-RP2040/releases/latest). The recommended download is `ZeroSense-<version>-Starter-Bundle.zip`; it contains the portable app, a short `START_HERE.txt`, and both clearly separated board images. Verify the bundle against `SHA256SUMS.txt`, extract it, then flash exactly one matching firmware image.

The same files are also available separately:

1. `ZeroSense-<version>-Windows-x64.zip` — verify it against `SHA256SUMS.txt`, extract it, and run `zerosense.exe`.
   A signed `ZeroSense-Setup-<version>-win-x64.exe` is also published when the release runner has the project's Authenticode certificate; verify its publisher before running it.
2. For TENSTAR/RP2040-Zero: `ZeroSense-RP2040-Zero-<version>.uf2`.
3. For the two-female-port Waveshare board: `ZeroSense-RP2350-USB-C-<version>.uf2`.

Never flash the RP2350 UF2 to an RP2040 or the RP2040 UF2 to an RP2350. For the
RP2350 cabling and CC selector check, follow
[the mouse-proxy guide](docs/RP2350_MOUSE_PROXY.md) before connecting a mouse.

The Windows archive is self-contained; users do not need the .NET SDK. The
Microsoft Visual C++ 2015–2022 x64 Redistributable is still required. Detailed
setup, flashing, and troubleshooting steps are in
[docs/INSTALLATION.md](docs/INSTALLATION.md).

## Building from source

Release users do not need the .NET SDK, Arduino IDE, PlatformIO, or Git. Use
[docs/INSTALLATION.md](docs/INSTALLATION.md) for the no-build release path.
Developers building the project from source need:

- Windows 10 or Windows 11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) for the desktop app
- [Microsoft Visual C++ 2015-2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) for the unpackaged Windows App SDK runtime
- One firmware toolchain for source builds:
  - [Arduino IDE 2](https://www.arduino.cc/en/software) with the Earle F. Philhower Arduino-Pico core, or
  - [PlatformIO](https://platformio.org/install) and Git

The Arduino-Pico Boards Manager URL is:

```text
https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json
```

Arduino IDE instructions below apply to the RP2040 target. The RP2350 proxy uses
the checked-in custom board definition and Pico-PIO-USB pin selection, so build
that target with PlatformIO (or use the release UF2). Install **Raspberry Pi
Pico/RP2040/RP2350 by Earle F. Philhower III**, then select for RP2040:

```text
Tools > Board > Raspberry Pi Pico/RP2040/RP2350 > Waveshare RP2040 Zero
Tools > USB Stack > Adafruit TinyUSB
```

The matching Arduino CLI FQBN is:

```text
rp2040:rp2040:waveshare_rp2040_zero:usbstack=tinyusb
```

The Arduino-Pico core supplies the compatible Adafruit TinyUSB integration used by the sketch. Do not install a separate USB device driver or substitute a different board package.

## Build the firmware

### PlatformIO

From `RainbowRecoil\RP2040_Firmware`:

```powershell
.\build.bat
```

The underlying command and outputs are:

```powershell
pio run -e waveshare_rp2040_zero
# .pio\build\waveshare_rp2040_zero\firmware.uf2
# rainbow_recoil.uf2  (copy made by build.bat)

pio run -e waveshare_rp2350_usb_c
# .pio\build\waveshare_rp2350_usb_c\firmware.uf2
# rainbow_recoil_rp2350_usb_c.uf2  (copy made by build.bat)
```

`platformio.ini` pins the Arduino-Pico-compatible platform for both boards and
pins Pico-PIO-USB for the RP2350 build. `build.bat` compiles both targets. The
first build downloads its compiler, framework, and library dependencies.

### Arduino IDE

Open `RP2040_Firmware\rainbow_recoil\rainbow_recoil.ino`, select **Waveshare RP2040 Zero** and **Adafruit TinyUSB**, then choose **Sketch > Verify/Compile**. Choose **Sketch > Export Compiled Binary** to create a UF2 for drag-and-drop flashing.

See [RP2040_Firmware/README.md](RP2040_Firmware/README.md) for Arduino CLI commands and interface details.

## Put the board in UF2 mode and flash it

With the board already connected:

1. Hold `BOOT`.
2. Press and release `RESET` while continuing to hold `BOOT`.
3. Release `BOOT` when File Explorer shows the volume named `RPI-RP2`.
4. Copy the board-specific `.uf2` file to the root of `RPI-RP2`, or run
   `flash-firmware.bat rp2040` / `flash-firmware.bat rp2350`.
5. Wait for the copy to finish. The volume ejects and the board restarts automatically.

Alternatively, disconnect the cable, hold `BOOT` while reconnecting USB Type-C, and release `BOOT` when `RPI-RP2` appears.

UF2 mode is a boot-loader state, not a COM port. Seeing only `RPI-RP2` before flashing is expected. Seeing it continuously after a copy indicates that the UF2 was not accepted or the application did not start; use the [troubleshooting guide](docs/TROUBLESHOOTING.md).

## Build and run the Windows app

From `RainbowRecoil\WindowsApp`:

```powershell
.\build.ps1
```

The self-contained x64 publish output is:

```text
WindowsApp\artifacts\win-x64\zerosense.exe
```

Pass `--overlay` to start directly in the compact always-on-top view.

Run the executable after the firmware has restarted in runtime mode. The app prioritizes currently present Raspberry Pi VID `2E8A` COM ports, tries the last successful port first, and accepts a port only after the firmware returns the Rainbow Recoil protocol identity. If registry lookup is restricted, it safely applies the same handshake to present COM ports. It does not assume a fixed COM number, runtime PID, or USB-to-serial bridge name.

## Desktop client, overlay, and operator detection

The Windows client uses a compact five-page shell: Overview, Detection,
Calibration, Device, and Installation. Operator and weapon controls remain on
the Overview page.

The in-game overlay is enabled by default and toggled with `F8`. Its modifier
and key can be changed on the Detection page. Overlay mode reuses the same app
window and state, keeps it above ordinary windows, and exposes the active
operator, weapon, connection, detection, and arm state. Press the shortcut
again or choose **Open full client** to restore the previous window size and
position.

Operator recognition is opt-in and defaults to **Disabled**. It uses Windows'
local OCR engine on a configurable percentage of the desktop; captured pixels
remain in memory and are not written to disk. Two modes are available:

- **Continuous detection** checks every two seconds while another application
  is in the foreground.
- **Keybind detection** performs one capture on demand. The default shortcut is
  `Shift + M1`, and mouse buttons or function keys can be selected.

The default region is the upper 55% of the virtual desktop. Borderless or
windowed game display is recommended because exclusive-fullscreen applications
can prevent ordinary desktop capture. Use **Detect now** to tune the region and
confidence threshold. Automatic application of a confident match can be
disabled independently so detections are shown without changing the loadout.

Weapon recognition is separately opt-in and is intended for Siege's **Loadout**
screen, where the primary and secondary cards contain names even though the
in-round HUD uses icons. It scans a configurable left-side region, OCRs the two
cards independently, and limits matching to the selected operator's primary and
secondary choices. **Detect loadout now** shows both OCR results. The supplied
2560x1440 loadout capture resolves `R4-C` and `5.7 USG` with the default region;
other UI scales may require region tuning.

Manual OCR captures apply immediately. Continuous OCR requires the same
operator or loadout on two consecutive scans before it changes a profile, and
the UI shows the confidence of every accepted weapon match. Raw recognized text
is shown only in the Detection page; the copied diagnostic report records the
decision, character count, and elapsed OCR time but never stores screenshots or
raw captured text.

The global `1` and `2` shortcuts are enabled by default while Rainbow Six has
focus: `1` selects the operator's remembered primary profile and `2` selects the
remembered secondary profile. ZeroSense remembers both choices independently
for every operator. The shortcuts can be disabled on the Detection page and are
suppressed in every other app so ordinary number-row typing cannot change the
active profile.

## Included Siege calibration

The app contains 115 individual weapon entries from the current Y11S3 roster, including the previously missing SIX12 SD. Of those, 61 automatic weapons have current rate-of-fire and magazine timing plus a deterministic per-shot estimate. Supported semi-automatic DMRs, pistols, revolvers, and self-loading shotguns have non-zero per-shot compensation and default-on rapid fire; pump shotguns, shields, the hand cannon, and automatic weapons are excluded from rapid fire.

Three modes are available:

- **General (constant adjustment)** keeps the original, steady per-axis correction.
- **Weapon pattern (video-derived estimate)** sends a staged per-shot trace at the selected weapon's RPM and stops at its magazine length.
- **Experimental (per-weapon calibrated)** makes a separate copy of the standard pattern and applies saved first-shot, early, middle, late, and horizontal gains. It never edits the General or standard Pattern data and safely falls back to General on weapons without an automatic pattern.

The pattern points are useful starting estimates, not extracted game constants. Ubisoft describes a staged recoil system but does not publish numeric per-shot vectors, and normal YouTube recoil footage contains the presenter's mouse input. The app labels the mode accordingly instead of presenting inferred points as exact measurements. Reaper uses Ubisoft's current stage starts at bullets 0/3/10/25, but its stage magnitudes remain estimates.

Experimental tuning starts at `1.00`, which is exactly the standard estimated pattern. Calibrate one weapon in the shooting range, change one gain by `0.05` at a time, and use **Reset weapon** to return only that weapon to `1.00`. **First** affects shot one, **Early** the remainder of the first 25% of the magazine, **Mid** 25–70%, **Late** the final 30%, and **Horizontal** the entire trace. These controls provide a safe path toward measured curves; they do not make an unmeasured estimate “perfect.”

Every weapon with recoil output also has a saved **Per-weapon output strength** control. `1.00×` preserves the profile exactly; values below it reduce correction and values above it increase correction. This multiplier works in General, Pattern, and Experimental modes and also scales semi-automatic per-shot correction, so routine range calibration no longer requires editing profile JSON. Custom automatic profiles now generate their curve from their own RPM and magazine-size overrides instead of silently using the built-in timing.

The Calibration page can reset the reference sensitivity, ADS table, automatic
optic policy, and selected weapon strength together. **Undo reset** restores the
complete previous calibration snapshot. Resolution and FOV are explained rather
than collected because neither scales relative HID mouse reports in this design.

Attachment effects are applied by one shared formula rather than baked into unrelated weapon constants. Vertical Grip and the supplied Flash Hider reference each apply a `0.80` vertical multiplier; Compensator applies Ubisoft's documented `0.65` horizontal multiplier; Muzzle Brake applies Ubisoft's documented `0.50` multiplier to the first pattern point only. Per-shot scheduling remains `60,000,000 / RPM` microseconds—the supplied legacy table's `6000 / RPM` values are control-loop intervals, not physical shot intervals.

The supplied starting settings are saved as:

```text
DPI:                              1600
MouseYawSensitivity:              55
MousePitchSensitivity:            55
MouseSensitivityMultiplierUnit:   0.001000
ADS:                              1.0x 38, 2.5x 67, 3.5x 72, 8.0x 74
Optic selection:                  Automatic (attack 2.5x, defense 1.0x)
Defender 2.5x exceptions:         TCSG12, Tubarão AR-15.50, Aruni Mk 14 EBR
Semi-automatic rapid fire:        Enabled by default on supported weapons
Automatic barrel preference:      Flash hider on all supported guns
Automatic grip preference:        Vertical grip on every automatic profile
F2:                               Vertical grip + flash hider
Ela SCORPION EVO 3 A1:            Vertical grip + compensator barrel exception
Aruni Mk 14 EBR:                  Muzzle brake unavailable on defense
Dokkaebi Mk 14 EBR:               Muzzle brake retained on attack
Tubarão AR-15.50:                 Muzzle brake unavailable on defense
Maverick AR-15.50:                Muzzle brake retained on attack
```

The app sends the selected profile as a v4 transaction: begin plus expected hash, exact profile/pattern/sensitivity/rapid-fire acknowledgements, then a matching commit hash. Rejection, timeout, disconnect, or a mismatched hash restores the firmware's last-good configuration. Frames carry sync bytes, a sequence, length, and CRC-16 so a truncated/noisy CDC write cannot permanently desynchronize the parser. Invalid configuration sends `STOP`, disarms output, and explains the rejected field.

**Arm output** does not continuously move the pointer. RP2040 activation uses the desktop's foreground-gated M1+M2 monitor plus a 750 ms keepalive. RP2350 activation is different by design: the app grants a short foreground/armed lease, while the board reads raw M1+M2 from the downstream mouse itself. A raw release or mouse/interface disconnect stops output immediately; synthesized rapid-fire reports cannot feed back into this trigger. Physical movement, buttons, wheel, and pan remain additive and live.

Sensitivity is transferred exactly and read back exactly. The shared `0.05..8.0` range is only a broad finite wire-safety boundary, not the older narrow tuning clamp. On both boards, General mode retains independent-axis velocity smoothing (40-count maximum velocity and 0.85 friction), ±8% cadence variation, and adaptive 250/500 Hz idle/normal HID servicing. High activity uses the USB full-speed 1 ms endpoint (1 kHz target, typically about 900 Hz after host/controller overhead). RP2350 physical movement, wheel, pan, and button transitions are always promoted immediately to that 1 ms path, so adaptive idle pacing does not reduce pass-through responsiveness. Pattern mode instead uses exact RPM deadlines and distributes each shot's correction across 1 ms frames with fixed-point remainder preservation.

The Device page includes an explicit **Use simulator** option. It exercises the
same configuration calculation and binary protocol encoders as real firmware,
records acknowledgement/command metrics, and never emits HID input. **Validate
now** checks the active settings on demand, while **Copy diagnostic report**
copies a bounded in-memory report containing connection latency, command counts,
working-set memory, OCR timing events, and recent errors without screenshots.

Measured patterns can be added as versioned JSON packs under the app data
`measured-profile-packs` directory; see
[`docs/measured-profile-pack.example.json`](docs/measured-profile-pack.example.json).
Each entry requires capture provenance, game build, timestamp, RPM, and exact
weapon/grip/barrel/optic metadata. A pack applies only to that loadout; mismatched
loadouts fall back to the clearly labeled deterministic estimate.

Automatic optic selection uses `2.5x` for attackers and `1.0x` for defenders. Defender DMR exceptions use `2.5x`: TCSG12, Tubarão's AR-15.50, and Aruni's Mk 14 EBR. Turn off **Automatic** beside the optic selector to unlock a persistent manual override.

Mouse DPI is recorded for reference but is not used to scale generated HID reports. Display resolution and aspect ratio were removed from calibration because relative HID counts are not resolution-scaled. Siege FOV affects visual/ADS feel but is not an input to this implementation: the app uses the selected per-optic ADS value directly. At the default Siege multiplier of `0.02`, `55` with `0.001` is mathematically equivalent to `2.75`, not `5.5`; the UI displays this so the literal configuration is never silently reinterpreted.

Research references for the estimates are the current [all-weapons attachment guide](https://www.youtube.com/watch?v=4YhYKtgUrDY), a recent [F2 vertical-versus-angled-grip comparison](https://www.youtube.com/watch?v=ViHd3gEKbEY), Ubisoft's [multi-stage recoil description](https://www.ubisoft.com/en-us/game/rainbow-six/siege/news-updates/1k3EGuOGxKxe6mhOlFBhbj/weapon-recoil-overhaul), [input sensitivity formula](https://www.ubisoft.com/en-us/game/rainbow-six/siege/news-updates/6kY6b5JByBY3P6vQWWinla/fov-and-input-sensitivity), [Y9S1 grip modifiers](https://www.ubisoft.com/es-es/game/rainbow-six/siege/news-updates/3jBlCdtRBQx2sCjmY2umNu/y9s1-designers-notes), [Y11S3 Split Fire attachment restrictions](https://www.ubisoft.com/en-us/game/rainbow-six/siege/news-updates/seasons/splitfire), the supplied 2025 recoil/attachment tables, and the maintained [weapon-statistics dataset](https://github.com/hanslhansl/Rainbow-Six-Siege-Weapon-Statistics). The source hierarchy, current balance overrides, and calculation invariants are recorded in [`docs/RECOIL_DATA_SOURCES.md`](docs/RECOIL_DATA_SOURCES.md). Research was checked on 2026-09-19; re-check after game balance updates.

## Expected Windows enumeration

| Board state | What Windows should show | USB identity guidance |
|---|---|---|
| UF2 boot loader | `RPI-RP2` removable drive; no firmware COM/HID interfaces | Raspberry Pi VID `2E8A`, boot PID `0003` |
| Firmware running | USB Composite Device, `USB Serial Device (COMx)`, and HID-compliant mouse | Raspberry Pi VID `2E8A`; runtime PID can depend on core/interface configuration |

For RP2350, Windows sees only the RP2350 composite device; the downstream mouse
is hosted by PIO-USB and its standard inputs are forwarded through the RP2350
HID. Vendor-specific configuration interfaces, onboard profile utilities, RGB
control, and more than eight buttons are not USB-pass-through features. Configure
those directly before moving the mouse behind the proxy.

Do not hard-code one runtime PID or COM number. Use the present device's hardware ID, composite interfaces, and assigned `PortName`. Windows may assign a new COM number after changing the USB port, rebuilding with different USB descriptors, or clearing device history.

## More documentation

- [Installation](docs/INSTALLATION.md)
- [Quick start](docs/QUICKSTART.md)
- [Firmware build and USB details](RP2040_Firmware/README.md)
- [RP2350 mouse-proxy setup](docs/RP2350_MOUSE_PROXY.md)
- [Troubleshooting](docs/TROUBLESHOOTING.md)
- [Project summary](docs/PROJECT_SUMMARY.md)

## Run the core regression tests

The catalog, profile, settings, attachment, and protocol encoders have a
dependency-free test executable:

```powershell
dotnet run --project .\Tests\RainbowRecoil.CoreTests.csproj -c Release
```

Custom profiles are stored beside the app settings under LocalAppData. Modified
profiles are labeled in the selector and description. Both the app and profile
editor maintain a validated `.bak` copy; if the primary file becomes corrupt,
the app preserves it as `.corrupt` and restores the last good backup. To list or
edit the file interactively, run:

```powershell
py .\tools\profile_editor.py
```

This hardware/setup work is intentionally limited to board support, build reliability, USB transport, and documentation. It does not add or extend detection-evasion functionality.
