"""Read the canonical application release version from Directory.Build.props."""

from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ElementTree

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


if __name__ == "__main__":
    try:
        print(f"RELEASE_VERSION={read_release_version()}")
    except ValueError as error:
        sys.exit(str(error))
