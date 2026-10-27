"""Read the canonical application release version from Directory.Build.props."""

import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERSION_FILE = ROOT / "Directory.Build.props"
VERSION_PATTERN = re.compile(r"\d+\.\d+\.\d+")


def read_release_version(path: Path = VERSION_FILE) -> str:
    try:
        root = ElementTree.parse(path).getroot()
    except (OSError, ElementTree.ParseError) as error:
        raise ValueError(f"Cannot read release version from {path}: {error}") from error

    version = root.findtext(".//ReleaseVersion")
    if version is None or VERSION_PATTERN.fullmatch(version.strip()) is None:
        raise ValueError(f"{path} must define ReleaseVersion as major.minor.patch.")
    return version.strip()


def android_version_code(version: str) -> int:
    major, minor, patch = (int(part) for part in version.split("."))
    if minor >= 1000 or patch >= 1000:
        raise ValueError(
            "Android version code supports minor and patch values below 1000."
        )
    code = major * 1_000_000 + minor * 1_000 + patch
    if code < 1 or code > 2_100_000_000:
        raise ValueError("Android version code must be between 1 and 2100000000.")
    return code


if __name__ == "__main__":
    try:
        version = read_release_version()
        print(f"RELEASE_VERSION={version}")
        print(f"ANDROID_VERSION_CODE={android_version_code(version)}")
    except ValueError as error:
        sys.exit(str(error))
