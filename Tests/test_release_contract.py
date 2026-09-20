import re
import unittest
import xml.etree.ElementTree as element_tree
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class ReleaseContractTests(unittest.TestCase):
    def test_version_metadata_matches(self) -> None:
        properties = element_tree.parse(ROOT / "Directory.Build.props").getroot()
        version = properties.findtext("./PropertyGroup/Version")
        self.assertRegex(version or "", r"^\d+\.\d+\.\d+$")

        manifest = element_tree.parse(ROOT / "WindowsApp" / "app.manifest").getroot()
        namespace = {"asm": "urn:schemas-microsoft-com:asm.v1"}
        identity = manifest.find("asm:assemblyIdentity", namespace)
        self.assertIsNotNone(identity)
        self.assertEqual(f"{version}.0", identity.attrib["version"])

        installer = (ROOT / "installer" / "ZeroSense.iss").read_text()
        fallback = re.search(r'#define AppVersion "([^"]+)"', installer)
        self.assertIsNotNone(fallback)
        self.assertEqual(version, fallback.group(1))
        self.assertTrue((ROOT / "docs" / f"RELEASE_NOTES_{version}.md").is_file())

    def test_release_permissions_and_notes_are_scoped(self) -> None:
        workflow = (ROOT / ".github" / "workflows" / "release.yml").read_text()
        self.assertIn("permissions:\n  contents: read", workflow)
        publish = workflow.split("  publish:", 1)[1]
        self.assertIn("permissions:\n      contents: write", publish)
        self.assertIn('RELEASE_NOTES_${GITHUB_REF_NAME#v}.md', workflow)
        self.assertNotIn("RELEASE_NOTES_1.3.0.md", workflow)

    def test_private_key_formats_are_ignored(self) -> None:
        ignore = (ROOT / ".gitignore").read_text().splitlines()
        for pattern in ("*.pfx", "*.p12", "*.pem", "*.key", ".env"):
            self.assertIn(pattern, ignore)


if __name__ == "__main__":
    unittest.main()
