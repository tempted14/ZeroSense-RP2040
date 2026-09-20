#!/usr/bin/env python3
"""Build the single-download ZeroSense starter bundle."""

from __future__ import annotations

import argparse
import shutil
import stat
from pathlib import Path, PurePosixPath
from zipfile import ZIP_DEFLATED, BadZipFile, ZipFile


def _safe_app_entry(name: str) -> PurePosixPath:
    normalized = name.replace("\\", "/")
    path = PurePosixPath(normalized)
    if (
        not normalized
        or path.is_absolute()
        or ".." in path.parts
        or any(not part or part == "." or ":" in part for part in path.parts)
    ):
        raise ValueError(f"Unsafe portable-app archive entry: {name!r}")
    return path


def build_starter_bundle(assets: Path, version: str, quick_start: Path) -> Path:
    if not version or any(character in version for character in "/\\"):
        raise ValueError("Version must be a non-empty filename-safe value")

    portable = assets / f"ZeroSense-{version}-Windows-x64.zip"
    rp2040 = assets / f"ZeroSense-RP2040-Zero-{version}.uf2"
    rp2350 = assets / f"ZeroSense-RP2350-USB-C-{version}.uf2"
    required = (portable, rp2040, rp2350, quick_start)
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise FileNotFoundError("Missing starter-bundle input(s): " + ", ".join(missing))

    output = assets / f"ZeroSense-{version}-Starter-Bundle.zip"
    seen = {"start_here.txt"}
    try:
        with ZipFile(portable, "r") as source, ZipFile(output, "w", ZIP_DEFLATED) as bundle:
            bundle.writestr("START_HERE.txt", quick_start.read_bytes())
            for info in source.infolist():
                if info.is_dir():
                    continue
                mode = info.external_attr >> 16
                if stat.S_ISLNK(mode):
                    raise ValueError(f"Symbolic links are not allowed in the portable app: {info.filename!r}")
                relative = _safe_app_entry(info.filename)
                destination = PurePosixPath("ZeroSense") / relative
                collision_key = destination.as_posix().casefold()
                if collision_key in seen:
                    raise ValueError(f"Duplicate starter-bundle entry: {destination}")
                seen.add(collision_key)
                with source.open(info, "r") as source_file, bundle.open(
                    destination.as_posix(), "w"
                ) as destination_file:
                    shutil.copyfileobj(source_file, destination_file)

            for firmware in (rp2040, rp2350):
                destination = f"Firmware/{firmware.name}"
                bundle.write(firmware, destination)
    except BadZipFile as error:
        output.unlink(missing_ok=True)
        raise ValueError(f"Portable app is not a valid ZIP: {portable}") from error
    except Exception:
        output.unlink(missing_ok=True)
        raise

    return output


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--quick-start", type=Path, required=True)
    arguments = parser.parse_args()
    output = build_starter_bundle(arguments.assets, arguments.version, arguments.quick_start)
    print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
