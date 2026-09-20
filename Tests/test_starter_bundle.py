import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

from tools.build_starter_bundle import build_starter_bundle


class StarterBundleTests(unittest.TestCase):
    def test_bundle_has_guidance_app_and_both_firmware_images(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            version = "9.8.7"
            with ZipFile(root / f"ZeroSense-{version}-Windows-x64.zip", "w") as app:
                app.writestr("zerosense.exe", b"test executable")
                app.writestr("docs/TROUBLESHOOTING.md", b"test guidance")
            (root / f"ZeroSense-RP2040-Zero-{version}.uf2").write_bytes(b"rp2040")
            (root / f"ZeroSense-RP2350-USB-C-{version}.uf2").write_bytes(b"rp2350")
            quick_start = root / "START_HERE.txt"
            quick_start.write_text("quick start", encoding="utf-8")

            output = build_starter_bundle(root, version, quick_start)

            with ZipFile(output) as bundle:
                self.assertEqual(
                    {
                        "START_HERE.txt",
                        "ZeroSense/zerosense.exe",
                        "ZeroSense/docs/TROUBLESHOOTING.md",
                        f"Firmware/ZeroSense-RP2040-Zero-{version}.uf2",
                        f"Firmware/ZeroSense-RP2350-USB-C-{version}.uf2",
                    },
                    set(bundle.namelist()),
                )

    def test_bundle_rejects_parent_path_from_portable_archive(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            version = "9.8.7"
            with ZipFile(root / f"ZeroSense-{version}-Windows-x64.zip", "w") as app:
                app.writestr("../outside.exe", b"unsafe")
            (root / f"ZeroSense-RP2040-Zero-{version}.uf2").write_bytes(b"rp2040")
            (root / f"ZeroSense-RP2350-USB-C-{version}.uf2").write_bytes(b"rp2350")
            quick_start = root / "START_HERE.txt"
            quick_start.write_text("quick start", encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "Unsafe portable-app archive entry"):
                build_starter_bundle(root, version, quick_start)
            self.assertFalse((root / f"ZeroSense-{version}-Starter-Bundle.zip").exists())


if __name__ == "__main__":
    unittest.main()
