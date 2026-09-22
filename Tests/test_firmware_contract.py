import json
import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
FIRMWARE = (
    ROOT / "RP2040_Firmware" / "rainbow_recoil" / "rainbow_recoil.ino"
).read_text()
HID_DECODER = (
    ROOT / "RP2040_Firmware" / "rainbow_recoil" / "hid_report_decoder.h"
).read_text()
CORRECTION_SCHEDULER = (
    ROOT / "RP2040_Firmware" / "rainbow_recoil" / "correction_scheduler.h"
).read_text()
DELTA_NOISE = (
    ROOT / "RP2040_Firmware" / "rainbow_recoil" / "delta_noise.h"
).read_text()
CS_PROTOCOL = (ROOT / "WindowsApp" / "SerialProtocol.cs").read_text()
CS_CONNECTION = (ROOT / "WindowsApp" / "SerialConnection.cs").read_text()
PLATFORMIO = (ROOT / "RP2040_Firmware" / "platformio.ini").read_text()
PIO_HOST = (ROOT / "RP2040_Firmware" / "lib" / "PicoPIOUSB" / "src" / "pio_usb.c").read_text()
PIO_PROVENANCE = (ROOT / "RP2040_Firmware" / "lib" / "README.md").read_text()
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
        desktop = enum_values(CS_PROTOCOL.split("private enum CommandType", 1)[1])
        expected = {
            "ping", "start", "stop", "profile", "sensitivity",
            "pattern", "rapid_fire", "keepalive", "arm_lease",
            "config_begin", "config_commit", "config_abort", "status",
            "general_settings", "reset",
        }
        self.assertEqual(expected, firmware.keys())
        expected_desktop_names = {
            "ping": "ping", "start": "start", "stop": "stop",
            "profile": "profile", "sensitivity": "sensitivity",
            "pattern": "pattern", "rapid_fire": "rapidfire",
            "keepalive": "keepalive", "arm_lease": "armlease",
            "config_begin": "configurationbegin",
            "config_commit": "configurationcommit",
            "config_abort": "configurationabort", "status": "status",
            "general_settings": "generalsettings",
            "reset": "reset",
        }
        self.assertEqual(
            {expected_desktop_names[name]: value for name, value in firmware.items()},
            desktop,
        )

    def test_exact_profile_readback_uses_utf8(self) -> None:
        self.assertIn("Encoding = new UTF8Encoding(false, true)", CS_CONNECTION)

    def test_firmware_build_retains_float_readback_support(self) -> None:
        self.assertEqual(2, PLATFORMIO.count("-Wl,-u,_printf_float"))
        self.assertIn("PROFILE:%s:MODE=%s:V=%.3f:H=%.3f", FIRMWARE)
        self.assertIn("SENSITIVITY:H=%.3f:V=%.3f", FIRMWARE)

    def test_hid_service_is_reached_and_rate_limited(self) -> None:
        loop_position = FIRMWARE.index("void loop()")
        self.assertGreater(FIRMWARE.index("service_hid();", loop_position), loop_position)
        self.assertIn("now - lastHidReportAtUs", FIRMWARE)
        self.assertIn("usbHid.setPollInterval(1)", FIRMWARE)
        self.assertIn("METRICS:HID_SENT=%lu:HID_BUSY=%lu:MAX_QUEUE=%lu:", FIRMWARE)
        self.assertIn("HOST_DECODE_ERRORS=%lu:HOST_SATURATIONS=%lu:USB_STOPS=%lu:", FIRMWARE)
        self.assertIn("CORRECTION_LATE=%lu:MAX_CORRECTION_LATE_US=%lu:QUEUE=%lu:", FIRMWARE)
        self.assertIn("hostDecodeErrors.fetch_add", FIRMWARE)
        self.assertIn("hostAccumulatorSaturations.fetch_add", FIRMWARE)
        self.assertIn("HOST_RECOVERIES=%lu", FIRMWARE)

    def test_upstream_disconnect_immediately_clears_generated_output(self) -> None:
        fail_safe = re.search(
            r"static void service_upstream_usb_fail_safe\(\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(fail_safe)
        body = fail_safe.group("body")
        self.assertIn("TinyUSBDevice.mounted()", body)
        self.assertIn("rp2350ArmLeaseEnabled = false", body)
        self.assertIn("stop_output();", body)
        loop_position = FIRMWARE.index("void loop()")
        fail_safe_call = FIRMWARE.index("service_upstream_usb_fail_safe();", loop_position)
        movement_call = FIRMWARE.index("service_movement();", loop_position)
        self.assertLess(fail_safe_call, movement_call)

    def test_fractional_movement_reaches_pending_reports(self) -> None:
        self.assertIn("pendingMouseX += queuedX;", FIRMWARE)
        self.assertIn("pendingMouseY += queuedY;", FIRMWARE)
        self.assertIn("pendingMouseX -= baseDx;", FIRMWARE)
        self.assertIn("pendingMouseY -= baseDy;", FIRMWARE)

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
            "std::clamp<int64_t>(value, -100, 100)",
            FIRMWARE,
        )
        self.assertNotIn("DELTA_NOISE_RANGE", FIRMWARE)

    def test_recoil_units_and_sensitivity_are_preserved(self) -> None:
        self.assertIn("const float scaledX = requested_dx * horizontalSensitivityFactor;", FIRMWARE)
        self.assertIn("const float scaledY = requested_dy * verticalSensitivityFactor;", FIRMWARE)
        self.assertIn("sim.velocityX = scaledX;", FIRMWARE)
        self.assertIn("sim.velocityY = scaledY;", FIRMWARE)
        self.assertIn("fractionalMouseX += sim.velocityX * timeScale;", FIRMWARE)
        self.assertIn("fractionalMouseY += sim.velocityY * timeScale;", FIRMWARE)
        self.assertIn("sim.maxVelocity, horizontalSensitivityFactor", FIRMWARE)
        self.assertIn("sim.maxVelocity, verticalSensitivityFactor", FIRMWARE)
        self.assertIn("sim.acceleration, horizontalSensitivityFactor) * timeScale", FIRMWARE)
        self.assertIn("sim.acceleration, verticalSensitivityFactor) * timeScale", FIRMWARE)
        self.assertIn("frictionForInterval", FIRMWARE)
        self.assertNotIn("raw_dx / 256.0f", FIRMWARE)
        self.assertNotIn("raw_dy / 256.0f", FIRMWARE)
        self.assertNotIn("horizontal * 100.0f", FIRMWARE)
        self.assertNotIn("vertical * 100.0f", FIRMWARE)

    def test_pattern_points_bypass_smoothing_and_double_scaling(self) -> None:
        movement = re.search(
            r"static void generate_movement\(uint32_t shotIntervalUs\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(movement)
        body = movement.group("body")
        self.assertNotIn("* horizontalSensitivityFactor", body)
        self.assertNotIn("* verticalSensitivityFactor", body)
        self.assertIn(
            "schedule_pattern_correction(horizontal, vertical, shotIntervalUs)",
            body,
        )
        self.assertIn("activeMode == MODE_WEAPON_PATTERN || rapidFireActive", body)
        self.assertIn("queue_mouse_movement(horizontal, vertical, true, shotIntervalUs)", body)
        self.assertIn("service_scheduled_correction();", FIRMWARE)

    def test_rapid_fire_recoil_uses_smooth_per_shot_scheduler(self) -> None:
        movement = re.search(
            r"static void generate_movement\(uint32_t shotIntervalUs\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(movement)
        body = movement.group("body")
        self.assertIn("activeMode == MODE_WEAPON_PATTERN || rapidFireActive", body)
        self.assertIn(
            "schedule_pattern_correction(horizontal, vertical, shotIntervalUs)",
            body,
        )
        self.assertIn("shotIntervalScale(shotIntervalUs)", body)
        self.assertIn("intervalUs + FrameIntervalUs - 1U", CORRECTION_SCHEDULER)

    def test_pattern_and_rapid_fire_schedules_are_phase_locked(self) -> None:
        self.assertIn(
            "next_rpm_interval(activeRoundsPerMinute, movementIntervalRemainder)",
            FIRMWARE,
        )
        self.assertIn("nextMovementAtUs += scheduledInterval;", FIRMWARE)
        self.assertIn(
            "next_rpm_interval(\n"
            "            rapidFireRoundsPerMinute,\n"
            "            rapidIntervalRemainder)",
            FIRMWARE,
        )
        self.assertIn("nextRapidShotAtUs += shot_interval;", FIRMWARE)
        self.assertIn("remainderAccumulator += 60000000UL % roundsPerMinute", FIRMWARE)
        self.assertIn("framesForInterval(shotIntervalUs)", CORRECTION_SCHEDULER)
        self.assertNotIn("nextCorrectionFrameAtUs = now + 1000U", FIRMWARE)

    def test_pattern_completion_flushes_without_erasing_final_delta(self) -> None:
        completion = re.search(
            r"static void complete_pattern_output\(\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(completion)
        body = completion.group("body")
        self.assertIn("round_q16_to_integer(correctionScheduler.fractionXQ16)", body)
        self.assertIn("round_q16_to_integer(correctionScheduler.fractionYQ16)", body)
        self.assertNotIn("reset_movement_state", body)
        self.assertNotIn("pendingMouseX = 0", body)
        self.assertNotIn("pendingMouseY = 0", body)
        self.assertIn("complete_pattern_output();", FIRMWARE)

    def test_rapid_fire_cannot_double_run_pattern_compensation(self) -> None:
        movement_service = re.search(
            r"static void service_movement\(\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(movement_service)
        self.assertIn("!fireActive || rapidFireActive", movement_service.group("body"))
        self.assertIn("enabled && activeMode == MODE_WEAPON_PATTERN", FIRMWARE)
        self.assertIn("ERROR:RAPID_FIRE:MODE", FIRMWARE)

    def test_requested_smoothing_jitter_polling_and_sensitivity_contract(self) -> None:
        self.assertIn("1.2f,", FIRMWARE)
        self.assertIn("40.0f,", FIRMWARE)
        self.assertIn("0.85f,", FIRMWARE)
        self.assertIn("generalTimingJitterEnabled = false", FIRMWARE)
        self.assertIn("TIMING_JITTER_PCT = 8.0f", FIRMWARE)
        self.assertIn("if (!generalTimingJitterEnabled)", FIRMWARE)
        self.assertIn("CMD_GENERAL_SETTINGS = 0xFD", FIRMWARE)
        self.assertIn("GENERAL_SETTINGS:TIMING_VARIANCE=%s", FIRMWARE)
        self.assertIn(":DELTA_NOISE=%s", FIRMWARE)
        self.assertIn("hash_byte(hash, generalTimingJitterEnabled ? 1 : 0)", FIRMWARE)
        self.assertIn("hash_byte(hash, deltaNoiseEnabled ? 1 : 0)", FIRMWARE)
        self.assertIn("pendingMouseX -= baseDx", FIRMWARE)
        self.assertIn("pendingMouseY -= baseDy", FIRMWARE)
        self.assertIn("state.balance != 0", DELTA_NOISE)
        self.assertIn("state.balance + appliedDelta", DELTA_NOISE)
        self.assertIn("now - lastMovementIntegrationAtUs", FIRMWARE)
        self.assertIn("generate_movement(integrationInterval);", FIRMWARE)
        self.assertIn("POLL_IDLE_US   = 4000", FIRMWARE)
        self.assertIn("POLL_NORMAL_US = 2000", FIRMWARE)
        self.assertIn("POLL_HIGH_US   = 1000", FIRMWARE)
        self.assertIn("physical_activity = pendingPhysicalMouseX != 0", FIRMWARE)
        self.assertIn("generated_activity || physical_activity", FIRMWARE)
        self.assertNotIn("PROXY_POLL_US", FIRMWARE)
        self.assertIn("minSensitivityFactor = 0.05f", FIRMWARE)
        self.assertIn("maxSensitivityFactor = 16.0f", FIRMWARE)
        self.assertIn("maxPatternRoundsPerMinute = 2000", FIRMWARE)
        self.assertNotIn("std::clamp(horizontal, minSensitivityFactor", FIRMWARE)

    def test_rp2350_local_activation_and_transaction_recovery_are_present(self) -> None:
        self.assertIn("service_rp2350_local_activation();", FIRMWARE)
        self.assertIn("MOUSE_BUTTON_LEFT | MOUSE_BUTTON_RIGHT", FIRMWARE)
        self.assertIn("hostMouseFaultPending.exchange(false", FIRMWARE)
        self.assertIn("rollback_configuration_transaction", FIRMWARE)
        self.assertIn("current_configuration_hash()", FIRMWARE)
        self.assertIn("ERROR:FRAME:CRC", FIRMWARE)
        self.assertIn("ERROR:FRAME:TIMEOUT", FIRMWARE)
        self.assertIn("reset_parser_preserving_magic(byte)", FIRMWARE)

    def test_rp2350_target_is_separate_and_pinned(self) -> None:
        self.assertIn("[env:waveshare_rp2040_zero]", PLATFORMIO)
        self.assertIn("[env:waveshare_rp2350_usb_c]", PLATFORMIO)
        self.assertIn("-DZEROSENSE_RP2350_USB_C", PLATFORMIO)
        self.assertIn("board_build.f_cpu = 120000000L", PLATFORMIO)
        self.assertIn("lib_ignore = Pico PIO USB", PLATFORMIO)
        self.assertIn("5a37a66dc5d3fbe0ef3cdbeda923a757440f984f", PIO_PROVENANCE)
        self.assertEqual("rp2350", RP2350_BOARD["build"]["mcu"])
        self.assertEqual(2_097_152, RP2350_BOARD["upload"]["maximum_size"])

    def test_rp2350_uses_exact_female_port_pinout(self) -> None:
        self.assertIn("static constexpr uint8_t hostMouseDpPin = 12;", FIRMWARE)
        self.assertIn("configuration.pin_dp = hostMouseDpPin;", FIRMWARE)
        self.assertIn("configuration.pinout = PIO_USB_PINOUT_DPDM;", FIRMWARE)
        self.assertIn("F_CPU == 120000000L || F_CPU == 240000000L", FIRMWARE)

    def test_pio_host_eop_wait_has_no_program_counter_spin(self) -> None:
        transfer = PIO_HOST.split("void __not_in_flash_func(pio_usb_bus_usb_transfer)", 1)[1]
        transfer = transfer.split("void __no_inline_not_in_flash_func(pio_usb_bus_send_token)", 1)[0]
        self.assertNotIn("*pc < PIO_USB_TX_ENCODED_DATA_COMP", transfer)
        self.assertNotIn("*pc <= PIO_USB_TX_ENCODED_DATA_COMP", transfer)
        self.assertIn("busy_wait_at_least_cycles(4u * bit_cycles)", transfer)

    def test_physical_input_is_additive_and_not_cleared_by_stop(self) -> None:
        self.assertIn(
            "hostMouseX.exchange(0, std::memory_order_acq_rel)",
            FIRMWARE,
        )
        self.assertIn(
            "static_cast<int64_t>(pendingMouseX) + pendingPhysicalMouseX",
            FIRMWARE,
        )
        self.assertIn(
            "static_cast<int64_t>(pendingMouseY) + pendingPhysicalMouseY",
            FIRMWARE,
        )
        reset = re.search(
            r"static void reset_movement_state\(\)\s*\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(reset)
        self.assertNotIn("pendingPhysicalMouse", reset.group("body"))
        self.assertIn("deltaNoiseX = {};", reset.group("body"))
        self.assertIn("deltaNoiseY = {};", reset.group("body"))

    def test_proxy_preserves_buttons_wheel_pan_and_requeues_reports(self) -> None:
        self.assertIn("hostMouseButtons.store(buttons", FIRMWARE)
        self.assertIn("FieldKind::Wheel", HID_DECODER)
        self.assertIn("FieldKind::Pan", HID_DECODER)
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
        self.assertIn("queue_host_mouse_report", callback.group("body"))
        self.assertIn("usbHost.task(1)", FIRMWARE)
        self.assertNotIn("usbHost.task();", FIRMWARE)
        self.assertIn("service_host_mouse_receive_recovery();", FIRMWARE)
        self.assertIn("tuh_hid_receive_ready", FIRMWARE)
        recovery = re.search(
            r"static void service_host_mouse_receive_recovery\(\)\s*"
            r"\{(?P<body>.*?)\n\}",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(recovery)
        self.assertNotIn("!tuh_mounted", recovery.group("body"))
        self.assertNotIn("clear_host_mouse_interface", recovery.group("body"))
        self.assertNotIn("memset", recovery.group("body"))
        self.assertIn("retryDue", recovery.group("body"))
        self.assertIn("mouseInterface.connected = false", recovery.group("body"))
        self.assertIn("hostMouseFaultPending.store(true", recovery.group("body"))

    def test_serial_backpressure_cannot_block_hid_loop(self) -> None:
        self.assertNotRegex(FIRMWARE, r"Serial\.(?:print|printf|println|write|flush)\(")
        self.assertIn("protocolOutput.drain(tud_cdc_write_available(), 128,", FIRMWARE)
        self.assertIn("while (budget-- > 0 && Serial.available() > 0)", FIRMWARE)
        self.assertIn("service_hid();\n    service_protocol_output();", FIRMWARE)

    def test_host_diagnostics_distinguish_loop_and_report_stalls(self) -> None:
        for metric in ("HOST_QUEUE_FAILURES", "HOST_UNMOUNTS", "HOST_TASK_AGE_MS",
                       "CDC_DROPPED"):
            self.assertIn(metric + "=%lu", FIRMWARE)
        self.assertIn("hostTaskLastAtMs.store(millis()", FIRMWARE)

    def test_proxy_reports_identity_and_mouse_health(self) -> None:
        self.assertIn("DEVICE:RP2350-USB-C:MOUSE-PROXY", FIRMWARE)
        self.assertIn("MOUSE:CONNECTED:VID=%04X:PID=%04X", FIRMWARE)
        self.assertIn("MOUSE:DISCONNECTED", FIRMWARE)
        self.assertIn("MOUSE:UNSUPPORTED:HID_REPORT_DESCRIPTOR", FIRMWARE)
        self.assertIn("MOUSE:HOST_ERROR", FIRMWARE)
        status_case = re.search(
            r"case CMD_STATUS:(?P<body>.*?)\n\s*break;",
            FIRMWARE,
            re.DOTALL,
        )
        self.assertIsNotNone(status_case)
        self.assertIn("print_hardware_identity();", status_case.group("body"))

    def test_build_flash_and_ci_routes_keep_targets_distinct(self) -> None:
        self.assertIn('if /i "%TARGET%"=="rp2040"', FLASH_HELPER)
        self.assertIn('else if /i "%TARGET%"=="rp2350"', FLASH_HELPER)
        self.assertIn("rainbow_recoil_rp2350_usb_c.uf2", FLASH_HELPER)
        self.assertIn("tools/verify_uf2.py rp2040", CI_WORKFLOW)
        self.assertIn("tools/verify_uf2.py rp2350", CI_WORKFLOW)


if __name__ == "__main__":
    unittest.main()
