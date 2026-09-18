import math
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools import profile_editor


class ProfileEditorTests(unittest.TestCase):
    def test_rejects_nonfinite_compensation(self) -> None:
        profile = self.profile()
        with patch("builtins.input", return_value="nan"):
            with self.assertRaisesRegex(ValueError, "finite"):
                profile_editor.edit_profile(profile)

    def test_rejects_out_of_range_burst(self) -> None:
        profile = self.profile()
        with patch("builtins.input", side_effect=["1", "0", "101"]):
            with self.assertRaisesRegex(ValueError, "between 0 and 100"):
                profile_editor.edit_profile(profile)

    def test_failed_json_write_preserves_file_and_removes_temporary(self) -> None:
        with tempfile.TemporaryDirectory(prefix="zerosense-editor-") as directory:
            path = Path(directory) / "profiles.json"
            profile_editor.save_profiles(path, [self.profile()])
            original = path.read_text(encoding="utf-8")
            invalid = self.profile()
            invalid["verticalCompensation"] = math.nan
            with self.assertRaises(ValueError):
                profile_editor.save_profiles(path, [invalid])
            self.assertEqual(original, path.read_text(encoding="utf-8"))
            self.assertFalse(path.with_suffix(".json.tmp").exists())

    def test_replacing_profiles_creates_valid_backup(self) -> None:
        with tempfile.TemporaryDirectory(prefix="zerosense-editor-") as directory:
            path = Path(directory) / "profiles.json"
            original = self.profile()
            profile_editor.save_profiles(path, [original])
            changed = self.profile()
            changed["verticalCompensation"] = 1.5
            profile_editor.save_profiles(path, [changed])

            backup = path.with_suffix(".json.bak")
            self.assertTrue(backup.exists())
            self.assertEqual([original], profile_editor.load_profiles(backup))

    @staticmethod
    def profile() -> dict[str, object]:
        return {
            "name": "test",
            "verticalCompensation": 1.0,
            "horizontalCompensation": 0.0,
            "burstProgression": 0,
        }


if __name__ == "__main__":
    unittest.main()
