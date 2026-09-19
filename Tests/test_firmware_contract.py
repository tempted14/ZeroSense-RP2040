import json
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FIRMWARE = (
    ROOT / "RP2040_Firmware" / "rainbow_recoil" / "rainbow_recoil.ino"
).read_text()
CS_PROTOCOL = (ROOT / "WindowsApp" / "SerialProtocol.cs").read_text()
PLATFORMIO = (ROOT / "RP2040_Firmware" / "platformio.ini").read_text()
RP2350_BOARD = json.loads((
    ROOT / "RP2040_Firmware" / "boards" / "waveshare_rp2350_usb_c.json"
).read_text())
FLASH_HELPER = (ROOT / "RP2040_Firmware" / "flash-firmware.bat").read_text()
CI_WORKFLOW = (ROOT / ".github" / "workflows" / "ci.yml").read_text()


def enum_values(source: str, prefix: str = "") -> dict[str, int]:
    pattern = rf"\b{prefix}([A-Za-z_]+)\s*=\s*0x([0-9A-Fa-f]+)"
    return {name.lower(): int(value, 16) for name, value in re.findall(pattern, source)}


class FirmwareContractTests(unittest.TestCase):
    def test_desktop_and_firmware_command_ids_match(self) -> None:
        firmware = enum_values(FIRMWARE, "CMD_")
        desktop = enum_values(CS_PROTOCOL)
        expected = {
            "ping", "start", "stop", "profile", "sensitivity",
            "pattern", "rapid_fire", "keepalive", "reset",
        }
        self.assertEqual(expected, firmware.keys())
        self.assertEqual(
            {name.replace("_", ""): value for name, value in firmware.items()},
            desktop,
        )

    def test_hid_service_is_reached_and_rate_limited(self) -> None:
        loop_position = FIRMWARE.index("void loop()")
        self.assertGreater(FIRMWARE.index("service_hid();", loop_position), loop_position)
        self.assertIn("now - lastHidReportAtUs", FIRMWARE)
        self.assertIn("usbHid.setPollInterval(POLL_HIGH_MS)", FIRMWARE)

    def test_fractional_movement_reaches_pending_reports(self) -> None:
        self.assertIn("pendingMouseX += queuedX;", FIRMWARE)
        self.assertIn("pendingMouseY += queuedY;", FIRMWARE)
        self.assertIn("pendingMouseX -= dx;", FIRMWARE)
        self.assertIn("pendingMouseY -= dy;", FIRMWARE)

    def test_rapid_fire_release_does_not_disable_the_mode(self) -> None:
        release = re.search(
            r"// Rapid button release handling(?P<body>.*?)"
            r"// Schedule the next shot",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(release)
        self.assertNotIn("rapidFireActive = false", release.group("body"))
        self.assertIn("set_rapid_button(false)", release.group("body"))

    def test_velocity_state_is_independent_per_axis(self) -> None:
        self.assertIn("float velocityX;", FIRMWARE)
        self.assertIn("float velocityY;", FIRMWARE)
        self.assertNotRegex(FIRMWARE, r"\bsim\.velocity\b")

    def test_zero_delta_reports_cannot_create_cursor_drift(self) -> None:
        self.assertIn(
            "std::clamp<int32_t>(value, -100, 100)",
            FIRMWARE,
        )
        self.assertNotIn("DELTA_NOISE_RANGE", FIRMWARE)

    def test_recoil_units_and_sensitivity_are_preserved(self) -> None:
        self.assertIn("const float scaledX = requested_dx * horizontalSensitivityFactor;", FIRMWARE)
        self.assertIn("const float scaledY = requested_dy * verticalSensitivityFactor;", FIRMWARE)
        self.assertIn("sim.velocityX = scaledX;", FIRMWARE)
        self.assertIn("sim.velocityY = scaledY;", FIRMWARE)
        self.assertIn("fractionalMouseX += sim.velocityX;", FIRMWARE)
        self.assertIn("fractionalMouseY += sim.velocityY;", FIRMWARE)
        self.assertNotIn("raw_dx / 256.0f", FIRMWARE)
        self.assertNotIn("raw_dy / 256.0f", FIRMWARE)
        self.assertNotIn("horizontal * 100.0f", FIRMWARE)
        self.assertNotIn("vertical * 100.0f", FIRMWARE)

    def test_pattern_points_bypass_smoothing_and_double_scaling(self) -> None:
        movement = re.search(
            r"static void generate_movement\(\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(movement)
        body = movement.group("body")
        self.assertNotIn("* horizontalSensitivityFactor", body)
        self.assertNotIn("* verticalSensitivityFactor", body)
        self.assertIn(
            "queue_mouse_movement(horizontal, vertical, activeMode == MODE_GENERAL)",
            body,
        )

    def test_pattern_and_rapid_fire_schedules_are_phase_locked(self) -> None:
        self.assertIn(
            "activeMode == MODE_WEAPON_PATTERN\n"
            "            ? base_interval\n"
            "            : get_jittered_interval(base_interval)",
            FIRMWARE,
        )
        self.assertIn("nextMovementAtUs += interval;", FIRMWARE)
        self.assertIn("const uint32_t shot_interval = base_interval;", FIRMWARE)
        self.assertIn("nextRapidShotAtUs += shot_interval;", FIRMWARE)

    def test_rp2350_target_is_separate_and_pinned(self) -> None:
        self.assertIn("[env:waveshare_rp2040_zero]", PLATFORMIO)
        self.assertIn("[env:waveshare_rp2350_usb_c]", PLATFORMIO)
        self.assertIn("-DZEROSENSE_RP2350_USB_C", PLATFORMIO)
        self.assertIn("board_build.f_cpu = 120000000L", PLATFORMIO)
        self.assertIn("Pico-PIO-USB.git#5a37a66", PLATFORMIO)
        self.assertEqual("rp2350", RP2350_BOARD["build"]["mcu"])
        self.assertEqual(2_097_152, RP2350_BOARD["upload"]["maximum_size"])

    def test_rp2350_uses_exact_female_port_pinout(self) -> None:
        self.assertIn("static constexpr uint8_t hostMouseDpPin = 12;", FIRMWARE)
        self.assertIn("configuration.pin_dp = hostMouseDpPin;", FIRMWARE)
        self.assertIn("configuration.pinout = PIO_USB_PINOUT_DPDM;", FIRMWARE)
        self.assertIn("F_CPU == 120000000L || F_CPU == 240000000L", FIRMWARE)

    def test_physical_input_is_additive_and_not_cleared_by_stop(self) -> None:
        self.assertIn(
            "hostMouseX.exchange(0, std::memory_order_acq_rel)",
            FIRMWARE,
        )
        self.assertIn("pendingMouseX + pendingPhysicalMouseX", FIRMWARE)
        self.assertIn("pendingMouseY + pendingPhysicalMouseY", FIRMWARE)
        reset = re.search(
            r"static void reset_movement_state\(\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(reset)
        self.assertNotIn("pendingPhysicalMouse", reset.group("body"))

    def test_proxy_preserves_buttons_wheel_pan_and_requeues_reports(self) -> None:
        self.assertIn("hostMouseButtons.store(buttons", FIRMWARE)
        self.assertIn("HostMouseFieldKind::Wheel", FIRMWARE)
        self.assertIn("HostMouseFieldKind::Pan", FIRMWARE)
        self.assertIn("usbHid.mouseReport(0, buttons, dx, dy, wheel, pan)", FIRMWARE)
        self.assertIn("(physical & ~MOUSE_BUTTON_LEFT)", FIRMWARE)
        self.assertIn("if (!rapidFireActive)", FIRMWARE)
        callback = re.search(
            r"void tuh_hid_report_received_cb\((?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(callback)
        self.assertIn("decode_host_mouse_report", callback.group("body"))
        self.assertIn("tuh_hid_receive_report", callback.group("body"))

    def test_proxy_reports_identity_and_mouse_health(self) -> None:
        self.assertIn("DEVICE:RP2350-USB-C:MOUSE-PROXY", FIRMWARE)
        self.assertIn("MOUSE:CONNECTED:VID=%04X:PID=%04X", FIRMWARE)
        self.assertIn("MOUSE:DISCONNECTED", FIRMWARE)
        self.assertIn("MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR", FIRMWARE)
        self.assertIn("MOUSE:HOST_ERROR", FIRMWARE)

    def test_build_flash_and_ci_routes_keep_targets_distinct(self) -> None:
        self.assertIn('if /i "%TARGET%"=="rp2040"', FLASH_HELPER)
        self.assertIn('else if /i "%TARGET%"=="rp2350"', FLASH_HELPER)
        self.assertIn("rainbow_recoil_rp2350_usb_c.uf2", FLASH_HELPER)
        self.assertIn("tools/verify_uf2.py rp2040", CI_WORKFLOW)
        self.assertIn("tools/verify_uf2.py rp2350", CI_WORKFLOW)


if __name__ == "__main__":
    unittest.main()
