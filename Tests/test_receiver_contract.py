import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FW = ROOT / "RP2040_Firmware"


class ReceiverContractTests(unittest.TestCase):
    def test_both_proxy_targets_share_expanded_descriptor_capacity(self):
        config = (FW / "rainbow_recoil/zerosense_tusb_config.h").read_text()
        platform = (FW / "platformio.ini").read_text()
        self.assertIn('#include "tusb_config.h"', config)
        self.assertIn('#undef CFG_TUH_ENUMERATION_BUFSIZE', config)
        self.assertIn('#define CFG_TUH_ENUMERATION_BUFSIZE 1024', config)
        for target in ('waveshare_rp2350_usb_c', 'waveshare_rp2350_usb_a'):
            section = platform.split(f'[env:{target}]', 1)[1].split('[env:', 1)[0]
            self.assertIn('CFG_TUSB_CONFIG_FILE=', section)
            self.assertIn('zerosense_tusb_config.h', section)
        firmware = (FW / 'rainbow_recoil/rainbow_recoil.ino').read_text()
        self.assertIn('static_assert(CFG_TUH_ENUMERATION_BUFSIZE == 1024', firmware)

    def test_root_diagnostics_do_not_write_usb_pins(self):
        source = (FW / 'lib/PicoPIOUSB/src/pio_usb_host.c').read_text()
        status = source.split('uint32_t pio_usb_host_root_status(', 1)[1].split(
            'void __', 1)[0]
        self.assertIn('root_idx >= PIO_USB_ROOT_PORT_CNT', status)
        self.assertIn('if (!root->initialized) return 0;', status)
        self.assertIn('gpio_get_all()', status)
        self.assertIn('const uint32_t pins = ~gpio_get_all();', status)
        for mutation in ('gpio_put', 'gpio_set', 'gpio_init', 'gpio_pull'):
            self.assertNotIn(mutation, status)

    def test_rejected_boot_protocol_clears_pending_interface(self):
        source = (FW / 'rainbow_recoil/rainbow_recoil.ino').read_text()
        callback = source.split('void tuh_hid_set_protocol_complete_cb(', 1)[1].split(
            'void tuh_hid_umount_cb(', 1)[0]
        failure = callback.split('if (protocol != HID_PROTOCOL_BOOT)', 1)[1].split(
            'configure_boot_mouse_layout', 1)[0]
        self.assertIn('clear_host_mouse_interface(*mouseInterface);', failure)
        self.assertIn('MouseProxyEvent::Unsupported', failure)
        self.assertIn('return;', failure)

    def test_host_bus_line_is_logged_without_overwriting_connection_ui(self):
        source = (ROOT / 'WindowsApp/MainPage.xaml.cs').read_text()
        self.assertIn('message.StartsWith("HOST_BUS:", StringComparison.Ordinal)', source)


if __name__ == '__main__':
    unittest.main()
