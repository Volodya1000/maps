"""Install the verified full Belarus map from the published offline APK, without Android."""

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from urllib.request import urlopen
import zipfile

ROOT = Path(__file__).resolve().parents[1]
APK_NAME = "CableRoutePlanner-0.2.0-Belarus.apk"
APK_URL = f"https://github.com/Volodya1000/maps/releases/download/v0.2.0-preview/{APK_NAME}"
APK_SHA256 = "faf72011923e156acd343d193465fe0dcb742b405cffa6379b188d6509df77c0"
MAX_DATABASE_BYTES = 4 * 1024**3
LICENSE_FILES = ("NOTICE.txt", "licenses/NOTICE.txt", "licenses/ODbL-1.0.html", "licenses/DbCL-1.0.html")


def digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def download(cache: Path) -> Path:
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / APK_NAME
    if target.exists():
        if digest(target) != APK_SHA256:
            raise ValueError(f"Cached APK checksum mismatch: {target}. The file was not overwritten.")
        return target
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=cache, prefix="belarus-", suffix=".partial", delete=False) as output:
            temporary = Path(output.name)
            with urlopen(APK_URL, timeout=60) as source:
                shutil.copyfileobj(source, output, length=1024 * 1024)
        if digest(temporary) != APK_SHA256:
            raise ValueError("Downloaded APK checksum mismatch.")
        temporary.replace(target)
        return target
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def validate_manifest(manifest: dict) -> None:
    if (manifest.get("region_id") != "belarus" or manifest.get("schema_version") != 2
            or manifest.get("coordinate_system") != "EPSG:4326"):
        raise ValueError("The package is not the supported full Belarus offline map.")
    checksum = manifest.get("database_sha256", "")
    if len(checksum) != 64 or any(character not in "0123456789abcdef" for character in checksum.lower()):
        raise ValueError("The map manifest has an invalid database checksum.")


def install(apk: Path, destination: Path, expected_apk_sha256: str = APK_SHA256) -> Path:
    destination = destination.resolve()
    if digest(apk) != expected_apk_sha256:
        raise ValueError("APK checksum mismatch. No map files were installed.")
    with zipfile.ZipFile(apk) as archive:
        manifest_bytes = archive.read("assets/Data/manifest.json")
    manifest = json.loads(manifest_bytes)
    validate_manifest(manifest)
    if destination.exists():
        if not destination.is_dir():
            raise FileExistsError(f"Destination already exists: {destination}")
        manifest_path = destination / "manifest.json"
        database = destination / "geodata.sqlite"
        if manifest_path.is_file() and database.is_file():
            installed_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            validate_manifest(installed_manifest)
            if (installed_manifest["database_sha256"].lower() == manifest["database_sha256"].lower()
                    and digest(database) == manifest["database_sha256"].lower()):
                return destination
        raise FileExistsError(f"Destination contains incomplete or different data and was not changed: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if shutil.disk_usage(destination.parent).free < MAX_DATABASE_BYTES:
        raise OSError("At least 4 GiB of free disk space is required for the extracted map.")
    with tempfile.TemporaryDirectory(prefix=".belarus-", dir=destination.parent) as staging:
        package = Path(staging) / "package"
        package.mkdir()
        with zipfile.ZipFile(apk) as archive:
            checksum = hashlib.sha256()
            size = 0
            with archive.open("assets/Data/geodata.sqlite.gz") as compressed:
                with gzip.GzipFile(fileobj=compressed) as source, (package / "geodata.sqlite").open("wb") as output:
                    while chunk := source.read(1024 * 1024):
                        size += len(chunk)
                        if size > MAX_DATABASE_BYTES:
                            raise ValueError("Database exceeds the supported package size.")
                        checksum.update(chunk)
                        output.write(chunk)
            if checksum.hexdigest() != manifest["database_sha256"].lower():
                raise ValueError("Extracted database checksum mismatch. No package was installed.")
            (package / "manifest.json").write_bytes(manifest_bytes)
            # Explicit whitelist: ZIP entries cannot choose output paths or escape the package.
            for name in LICENSE_FILES:
                path = package / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(archive.read(f"assets/Data/{name}"))
        package.rename(destination)
    return destination


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, help="Use a downloaded APK; the pinned SHA-256 is still verified.")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/belarus")
    parser.add_argument("--verify", action="store_true", help="Run the application repository probes for all regional centres.")
    parser.add_argument("--run", action="store_true", help="Start Desktop with this full map, without a sample-data fallback.")
    options = parser.parse_args()
    try:
        print("Preparing full Belarus OSM map (about 1.2 GB download / 2.6 GB extracted).", flush=True)
        apk = options.apk or download(ROOT / "artifacts/downloads")
        directory = install(apk, options.output)
        print(f"Verified map: {directory}\nCABLEROUTE_DATA={directory}", flush=True)
        if options.verify:
            subprocess.run(["dotnet", "run", "--project", "tools/CableRoutePlanner.Verify", "-c", "Release",
                            "--", "--inspect", str(directory), str(directory.parent / "belarus-package-check.json")],
                           cwd=ROOT, check=True)
        if options.run:
            environment = dict(os.environ, CABLEROUTE_DATA=str(directory))
            subprocess.run(["dotnet", "run", "--project", "src/CableRoutePlanner.Desktop"],
                           cwd=ROOT, env=environment, check=True)
    except (OSError, ValueError, KeyError, zipfile.BadZipFile, subprocess.SubprocessError) as error:
        parser.exit(1, f"Belarus map installation failed: {error}\n")


if __name__ == "__main__":
    main()
