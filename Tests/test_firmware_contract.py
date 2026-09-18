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
            r"// Schedule next shot",
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
        zero_guard = re.search(
            r"static int8_t report_delta_with_noise\(int32_t value\)\s*\{"
            r"(?P<body>.*?)const int32_t direction",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(zero_guard)
        self.assertIn("if (value == 0)", zero_guard.group("body"))
        self.assertIn("return 0;", zero_guard.group("body"))


if __name__ == "__main__":
    unittest.main()
