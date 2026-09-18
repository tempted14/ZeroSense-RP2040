import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FIRMWARE = (
    ROOT / "RP2040_Firmware" / "rainbow_recoil" / "rainbow_recoil.ino"
).read_text()
CS_PROTOCOL = (ROOT / "WindowsApp" / "SerialProtocol.cs").read_text()


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


if __name__ == "__main__":
    unittest.main()
