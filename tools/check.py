"""Shared local hooks / CI commands. A missing prerequisite is an error, never a skip."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import subprocess
import sys
import time
import tomllib

import yaml

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"
SOLUTION = "CableRoutePlanner.Desktop.slnf"


def run(*command: str, cwd: Path = ROOT, timeout: int = 600) -> None:
    print("+ " + " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True, timeout=timeout)


def executable(name: str) -> str:
    result = shutil.which(name)
    if not result:
        raise ValueError(f"Install {name} before running this check; see docs/development.md.")
    return result


def lint() -> None:
    run(executable("uv"), "run", "--locked", "ruff", "check", ".")
    for name in subprocess.check_output(["git", "ls-files", "-z"], cwd=ROOT, text=True).split("\0"):
        path = ROOT / name
        if not name or not path.is_file():
            continue
        if path.suffix == ".json":
            json.loads(path.read_text(encoding="utf-8-sig"))
        elif path.suffix == ".toml":
            tomllib.loads(path.read_text(encoding="utf-8"))
        elif path.suffix in (".yml", ".yaml"):
            yaml.safe_load(path.read_text(encoding="utf-8"))
    run(executable("uv"), "run", "--locked", "pre-commit", "validate-config")
    run(executable("uv"), "run", "--locked", "python", "-m", "unittest", "discover", "-s", "tests/tooling", "-v")
    run("git", "diff", "--check")


def desktop() -> None:
    dotnet = executable("dotnet")
    run(dotnet, "restore", SOLUTION, "--locked-mode")
    run(dotnet, "format", "whitespace", SOLUTION, "--no-restore", "--verify-no-changes")
    run(dotnet, "build", SOLUTION, "--no-restore", "-c", "Release", "-warnaserror")
    run(dotnet, "test", SOLUTION, "--no-restore", "--no-build", "-c", "Release",
        "--blame-hang-timeout", "30s", "--logger", "trx", "--results-directory", "artifacts/tests", timeout=180)
    if platform.system() == "Linux":
        run(executable("strace"), "-f", "-e", "trace=connect,sendto", "-o", "artifacts/runtime.strace",
            dotnet, "tools/CableRoutePlanner.Verify/bin/Release/net8.0/CableRoutePlanner.Verify.dll",
            "samples/real-geodata-fixtures/minsk", "artifacts/route.geojson", "53.9490,27.5180", "53.9500,27.5190")
        run(sys.executable, "tools/check_network.py", "artifacts/runtime.strace", "--output", "artifacts/network-report.json")
    elif platform.system() == "Windows":
        run(dotnet, "publish", "src/CableRoutePlanner.Desktop", "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "-o", "artifacts/win-x64")


def data() -> None:
    uv = executable("uv")
    run(uv, "sync", "--project", "tools/GeodataPreparation", "--locked")
    run(uv, "run", "--project", "tools/GeodataPreparation", "--locked", "python",
        "tools/GeodataPreparation/rebuild_sample.py", "--output", "artifacts/rebuilt-minsk")


def terrain() -> None:
    directory = ROOT / "spikes/MapLibre"
    npm = executable("npm")
    run(npm, "ci", cwd=directory)
    install = [executable("npx"), "playwright", "install"]
    if os.environ.get("PLAYWRIGHT_INSTALL_WITH_DEPS") == "1":
        install.append("--with-deps")
    run(*install, "chromium", cwd=directory)
    # Do not accidentally test against a pre-existing server from another checkout.
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 8765))
    with (ARTIFACTS / "terrain-server.log").open("w", encoding="utf-8") as log:
        server = subprocess.Popen([sys.executable, "serve.py"], cwd=directory, stdout=log, stderr=subprocess.STDOUT)
        try:
            for _ in range(100):
                if server.poll() is not None:
                    raise ValueError("Terrain server exited before becoming ready.")
                try:
                    with socket.create_connection(("127.0.0.1", 8765), timeout=0.1):
                        break
                except OSError:
                    time.sleep(0.05)
            else:
                raise TimeoutError("Terrain server did not become ready.")
            run(npm, "test", cwd=directory)
        finally:
            server.terminate()
            try:
                server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()


def android() -> None:
    dotnet = executable("dotnet")
    sdk = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    if not sdk:
        raise ValueError("Set ANDROID_HOME to Android SDK with API/build-tools 34.0.0; install JDK 17 and the .NET Android workload.")
    run(dotnet, "restore", "src/CableRoutePlanner.Android", "--locked-mode")
    run(dotnet, "publish", "src/CableRoutePlanner.Android", "-c", "Release", f"-p:AndroidSdkDirectory={sdk}")
    apk = ROOT / "src/CableRoutePlanner.Android/bin/Release/net8.0-android/publish/by.cablerouteplanner.app-Signed.apk"
    aapt = Path(sdk) / "build-tools/34.0.0" / ("aapt.exe" if os.name == "nt" else "aapt")
    permissions = subprocess.check_output([str(aapt), "dump", "permissions", str(apk)], text=True)
    (ARTIFACTS / "permissions.txt").write_text(permissions, encoding="utf-8")
    if "android.permission.INTERNET" in permissions:
        raise ValueError("Release APK unexpectedly requests INTERNET.")
    with apk.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    (ARTIFACTS / "apk.sha256").write_text(f"{digest}  {apk.name}\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("suite", choices=["lint", "desktop", "data", "terrain", "android", "all"])
    options = parser.parse_args()
    ARTIFACTS.mkdir(exist_ok=True)
    os.environ.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    os.environ.setdefault("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", "true")
    try:
        suites = [lint, desktop, data, terrain, android] if options.suite == "all" else [globals()[options.suite]]
        for suite in suites:
            suite()
    except (ValueError, OSError, subprocess.SubprocessError, TimeoutError) as error:
        parser.exit(1, f"Local/CI check failed: {error}\n")
