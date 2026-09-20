#!/usr/bin/env python3
"""Generate an SPDX 2.3 inventory for ZeroSense release assets."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path


def digest(path: Path, algorithm: str) -> str:
    value = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def spdx_id(name: str) -> str:
    safe = re.sub(r"[^A-Za-z0-9.-]+", "-", name).strip("-")
    return f"SPDXRef-File-{safe}"


def build_document(assets: Path, version: str) -> dict[str, object]:
    files = sorted(
        path for path in assets.iterdir()
        if path.is_file()
        and path.name != "SHA256SUMS.txt"
        and not path.name.endswith(".spdx.json")
    )
    if not files:
        raise ValueError("No release assets were found for the SBOM.")

    sha1_values = sorted(digest(path, "sha1") for path in files)
    verification_code = hashlib.sha1("".join(sha1_values).encode("ascii")).hexdigest()
    created = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace(
        "+00:00", "Z"
    )
    package_id = "SPDXRef-Package-ZeroSense"
    file_entries = []
    relationships = [
        {
            "spdxElementId": "SPDXRef-DOCUMENT",
            "relationshipType": "DESCRIBES",
            "relatedSpdxElement": package_id,
        }
    ]
    for path in files:
        file_id = spdx_id(path.name)
        file_entries.append(
            {
                "SPDXID": file_id,
                "fileName": f"./{path.name}",
                "checksums": [
                    {"algorithm": "SHA1", "checksumValue": digest(path, "sha1")},
                    {"algorithm": "SHA256", "checksumValue": digest(path, "sha256")},
                ],
                "licenseConcluded": "NOASSERTION",
                "copyrightText": "NOASSERTION",
            }
        )
        relationships.append(
            {
                "spdxElementId": package_id,
                "relationshipType": "CONTAINS",
                "relatedSpdxElement": file_id,
            }
        )

    return {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"ZeroSense-{version}-release-assets",
        "documentNamespace": (
            "https://github.com/tempted14/ZeroSense-RP2040/"
            f"releases/tag/v{version}/sbom"
        ),
        "creationInfo": {
            "created": created,
            "creators": ["Tool: ZeroSense release SBOM generator"],
        },
        "packages": [
            {
                "SPDXID": package_id,
                "name": "ZeroSense",
                "versionInfo": version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": True,
                "packageVerificationCode": {
                    "packageVerificationCodeValue": verification_code
                },
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": "NOASSERTION",
                "copyrightText": "NOASSERTION",
            }
        ],
        "files": file_entries,
        "relationships": relationships,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--version", required=True)
    arguments = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", arguments.version):
        parser.error("--version must be a semantic version such as 1.4.0")
    if not arguments.assets.is_dir():
        parser.error("--assets must identify an existing directory")

    destination = arguments.assets / f"ZeroSense-{arguments.version}.spdx.json"
    destination.write_text(
        json.dumps(build_document(arguments.assets, arguments.version), indent=2) + "\n",
        encoding="utf-8",
    )
    print(destination)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
