import re
import json
import subprocess
import sys
import tempfile
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
        self.assertIn("attestations: write", publish)
        self.assertIn("artifact-metadata: write", publish)
        self.assertIn("id-token: write", publish)
        self.assertIn("actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6", publish)

        for path in (ROOT / ".github" / "workflows").glob("*.yml"):
            contents = path.read_text()
            self.assertNotRegex(contents, r"uses:\s+[^\s]+@v\d+")
            for reference in re.findall(r"uses:\s+[^@\s]+@([^\s#]+)", contents):
                self.assertRegex(reference, r"^[0-9a-f]{40}$")

    def test_starter_bundle_contains_app_firmware_and_quick_start(self) -> None:
        workflow = (ROOT / ".github" / "workflows" / "release.yml").read_text()
        self.assertIn('ZeroSense-$version-Windows-x64.zip', workflow)
        self.assertIn('ZeroSense-RP2040-Zero-$version.uf2', workflow)
        self.assertIn('ZeroSense-RP2350-USB-C-$version.uf2', workflow)
        self.assertIn('docs/START_HERE.txt', workflow)
        self.assertIn('tools/build_starter_bundle.py', workflow)
        self.assertLess(
            workflow.index("Build one-download starter bundle"),
            workflow.index("Refresh combined checksums"),
        )

        quick_start = (ROOT / "docs" / "START_HERE.txt").read_text()
        self.assertIn("only flash ONE firmware file", quick_start)
        self.assertIn("SHA256SUMS.txt", quick_start)
        self.assertIn("1 / Source", quick_start)
        bundler = (ROOT / "tools" / "build_starter_bundle.py").read_text()
        self.assertIn('f"ZeroSense-{version}-Starter-Bundle.zip"', bundler)

    def test_private_key_formats_are_ignored(self) -> None:
        ignore = (ROOT / ".gitignore").read_text().splitlines()
        for pattern in ("*.pfx", "*.p12", "*.pem", "*.key", ".env"):
            self.assertIn(pattern, ignore)

    def test_release_sbom_inventories_assets_with_checksums(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            assets = Path(temporary)
            (assets / "ZeroSense-1.4.0-Windows-x64.zip").write_bytes(b"app")
            (assets / "ZeroSense-RP2040-Zero-1.4.0.uf2").write_bytes(b"firmware")
            subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "tools" / "generate_release_sbom.py"),
                    "--assets",
                    str(assets),
                    "--version",
                    "1.4.0",
                ],
                check=True,
                capture_output=True,
                text=True,
            )
            document = json.loads(
                (assets / "ZeroSense-1.4.0.spdx.json").read_text(encoding="utf-8")
            )
            self.assertEqual("SPDX-2.3", document["spdxVersion"])
            self.assertEqual("1.4.0", document["packages"][0]["versionInfo"])
            self.assertEqual(2, len(document["files"]))
            for entry in document["files"]:
                algorithms = {checksum["algorithm"] for checksum in entry["checksums"]}
                self.assertEqual({"SHA1", "SHA256"}, algorithms)


if __name__ == "__main__":
    unittest.main()
