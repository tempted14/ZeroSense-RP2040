# ZeroSense Engineering Handoff

This package is an editable source handoff, not merely a compiled release. It
contains the Windows application, RP2040 firmware, automated tests, utilities,
and documentation. Prebuilt Windows and UF2 files are distributed as GitHub
release assets rather than committed to source control.

## Start here

1. Read `README.md`, then `docs/QUICKSTART.md` and `docs/INSTALLATION.md`.
2. Open `WindowsApp/RainbowRecoil.sln` in Visual Studio 2022, or use the .NET 8
   CLI from the repository root.
3. Run `build.ps1` to execute the core tests and publish the Windows x64 app.
4. Build firmware with `RP2040_Firmware/build.bat`.
5. Flash `RP2040_Firmware/rainbow_recoil.uf2` in RP2040 BOOTSEL mode.

## Repository map

- `WindowsApp/`: WinUI 3 desktop client, device transport, detection, overlay,
  settings, calibration, recoil/profile calculations, and simulator.
- `RP2040_Firmware/`: Arduino-Pico/TinyUSB firmware and generated UF2.
- `Tests/`: dependency-free .NET regression suite, OCR probe, Python profile
  editor tests, and firmware/desktop contract checks.
- `tools/`: editable JSON profile utility.
- `docs/`: installation, troubleshooting, quick-start, and architecture notes.

## Build and test

From the project root:

```powershell
.\build.ps1
dotnet run --project .\Tests\RainbowRecoil.CoreTests.csproj --configuration Release
cmd /c .\RP2040_Firmware\build.bat
```

The last verified state on 2026-09-18 was:

- Core regression tests: 25/25 passed.
- Python profile-editor and firmware-contract tests: 10/10 passed.
- Windows self-contained x64 publish: succeeded with no compiler warnings.
- RP2040 firmware: succeeded; 17,608 bytes RAM (6.7%) and 70,516 bytes flash
  (3.4%).
- Published application startup: responsive with title `ZeroSense`.

## Principal architecture boundaries

- `MainPage.xaml` and `MainPage.xaml.cs`: UI composition and lifecycle
  coordination.
- `IRecoilDeviceConnection`: boundary between UI/configuration logic and device
  transports.
- `SerialConnection`: acknowledged CDC transport for physical firmware.
- `SimulatedRecoilDeviceConnection`: no-HID board-free integration harness.
- `SerialProtocol`: pure binary protocol encoder.
- `ConfigurationValidator`: fail-closed validation before transmission.
- `WeaponProfiles`, `WeaponPatternCatalog`, `RecoilAttachmentModel`, and
  `ExperimentalRecoilModel`: profile and output calculation.
- `OperatorDetectionService`, `WeaponDetectionService`, and
  `DetectionDebouncer`: screen OCR and safe consecutive-match application.
- `SettingsManager`: normalized atomic persistence and schema migration.
- `DiagnosticLog`: bounded in-memory diagnostic reporting without screenshots or
  raw OCR text.

See `docs/PROJECT_SUMMARY.md` for the detailed data flow and firmware protocol.

## Generated directories intentionally excluded

The repository omits regenerable caches and distributable binaries:

- `.pio/`
- `.platformio-core/`
- `.tools/`
- `.nuget-packages/`
- `bin/`
- `obj/`
- `WindowsApp/artifacts/`
- `dist/`
- `__pycache__/`

Release assets contain the published Windows application and the generated UF2.
Dependency caches are restored automatically by the documented build commands.

## Known verification limits

- Physical RP2040 HID movement, rapid fire, and live disconnect behavior require
  an attached board for final verification.
- End-to-end behavior in a running Siege session was not verified in the audit
  environment.
- OCR matching was tested against the supplied loadout capture and synthetic
  noisy inputs; additional UI scales and exclusive-fullscreen capture still need
  machine-specific verification.
- The current USB descriptors are manufacturer `RP2040`, product
  `RP2040 USB Mouse`, and HID interface `USB Mouse`, while retaining the board's
  legitimate Raspberry Pi USB identity.

## Editing guidance

- Preserve protocol compatibility unless both `SerialProtocol.cs` and firmware
  parsing are updated together.
- Add pure behavior to the core test project whenever possible.
- Keep arming session-only and retain STOP-on-error/disconnect/focus-loss safety.
- Do not persist screenshots or raw OCR text in diagnostic exports.
- Re-run the full root build and firmware build before distributing changes.
