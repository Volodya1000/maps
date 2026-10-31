"""Shared local hooks / CI commands. A missing prerequisite is an error, never a skip."""

import argparse
import hashlib
import json
import os
import platform
import shutil
import socket
import subprocess
import sys
import time
import tomllib
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"
SOLUTION = "CableRoutePlanner.Desktop.slnf"


def run(
    *command: str,
    cwd: Path = ROOT,
    timeout: int = 600,
    env: dict[str, str] | None = None,
) -> None:
    print("+ " + " ".join(command), flush=True)
    subprocess.run(command, cwd=cwd, check=True, timeout=timeout, env=env)


def executable(name: str) -> str:
    result = shutil.which(name)
    if not result:
        raise ValueError(
            f"Install {name} before running this check; see docs/development.md."
        )
    return result


def lint() -> None:
    run(sys.executable, "tools/check_architecture.py")
    run(sys.executable, "tools/check_quality.py")
    uv = executable("uv")
    run(uv, "run", "--locked", "ruff", "check", ".")
    run(uv, "run", "--locked", "ruff", "format", "--check", ".")
    dotnet = executable("dotnet")
    run(dotnet, "sln", "CableRoutePlanner.sln", "list")
    run(
        uv,
        "run",
        "--locked",
        "pre-commit",
        "run",
        "actionlint",
        "--hook-stage",
        "manual",
        "--all-files",
    )
    tracked_files = [
        name
        for name in subprocess.check_output(
            ["git", "ls-files", "-z"], cwd=ROOT, text=True
        ).split("\0")
        if name
    ]
    yaml_files = [
        name for name in tracked_files if Path(name).suffix in (".yml", ".yaml")
    ]
    run(uv, "run", "--locked", "yamllint", *yaml_files)
    for name in tracked_files:
        path = ROOT / name
        if not path.is_file():
            continue
        if path.suffix == ".json":
            json.loads(path.read_text(encoding="utf-8-sig"))
        elif path.suffix == ".toml":
            tomllib.loads(path.read_text(encoding="utf-8"))
        elif path.suffix in (".yml", ".yaml"):
            yaml.safe_load(path.read_text(encoding="utf-8"))
    run(executable("uv"), "run", "--locked", "pre-commit", "validate-config")
    run(
        executable("uv"),
        "run",
        "--locked",
        "python",
        "-m",
        "unittest",
        "discover",
        "-s",
        "tests/tooling",
        "-v",
    )
    run("git", "diff", "--check")


def desktop() -> None:
    dotnet = executable("dotnet")
    run(dotnet, "restore", SOLUTION, "--locked-mode")
    run(dotnet, "tool", "restore")
    run(dotnet, "format", "whitespace", SOLUTION, "--no-restore", "--verify-no-changes")
    run(
        dotnet,
        "format",
        "style",
        SOLUTION,
        "--no-restore",
        "--verify-no-changes",
        "--severity",
        "warn",
    )
    run(
        dotnet,
        "format",
        "analyzers",
        SOLUTION,
        "--no-restore",
        "--verify-no-changes",
        "--severity",
        "warn",
    )
    run(dotnet, "build", SOLUTION, "--no-restore", "-c", "Release", "-warnaserror")
    run(
        dotnet,
        "test",
        SOLUTION,
        "--no-restore",
        "--no-build",
        "-c",
        "Release",
        "--blame-hang-timeout",
        "30s",
        "--logger",
        "trx",
        "--results-directory",
        "artifacts/tests",
        "--collect:XPlat Code Coverage",
        "--settings",
        "coverage.runsettings",
        timeout=180,
    )
    run(sys.executable, "tools/check_coverage.py", "artifacts/tests")
    run(
        dotnet,
        "tool",
        "run",
        "reportgenerator",
        "-reports:artifacts/tests/**/coverage.cobertura.xml",
        "-targetdir:artifacts/coverage-report",
        "-reporttypes:Html",
    )
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        report = subprocess.check_output(
            [sys.executable, "tools/check_coverage.py", "artifacts/tests"],
            cwd=ROOT,
            text=True,
        )
        Path(summary).write_text(
            "## C# code coverage\n\n```text\n" + report + "```\n", encoding="utf-8"
        )
    if platform.system() == "Linux":
        run(
            executable("strace"),
            "-f",
            "-e",
            "trace=connect,sendto",
            "-o",
            "artifacts/runtime.strace",
            dotnet,
            "tools/CableRoutePlanner.Verify/bin/Release/net8.0/CableRoutePlanner.Verify.dll",
            "samples/real-geodata-fixtures/minsk",
            "artifacts/route.geojson",
            # Keep the smoke route on a mapped bridge.  The previous points
            # crossed a road without an approved bridge or gas-pipeline
            # candidate and therefore became invalid when road crossings were
            # made subject to the same terrain rules as all other obstacles.
            "53.9409,27.4977",
            "53.9424,27.4991",
        )
        run(
            sys.executable,
            "tools/check_network.py",
            "artifacts/runtime.strace",
            "--output",
            "artifacts/network-report.json",
        )
    elif platform.system() == "Windows":
        run(
            dotnet,
            "publish",
            "src/CableRoutePlanner.Desktop",
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:NuGetLockFilePath=obj/publish.win-x64.lock.json",
            "-o",
            "artifacts/win-x64",
        )


def data() -> None:
    uv = executable("uv")
    run(uv, "sync", "--project", "tools/GeodataPreparation", "--locked")
    run(
        uv,
        "run",
        "--project",
        "tools/GeodataPreparation",
        "--locked",
        "python",
        "-m",
        "unittest",
        "discover",
        "-s",
        "tools/GeodataPreparation/tests",
        "-v",
    )
    run(
        uv,
        "run",
        "--project",
        "tools/GeodataPreparation",
        "--locked",
        "python",
        "tools/GeodataPreparation/rebuild_sample.py",
        "--output",
        "artifacts/rebuilt-minsk",
    )


def terrain() -> None:
    directory = ROOT / "spikes/MapLibre"
    npm = executable("npm")
    run(npm, "ci", "--ignore-scripts", cwd=directory)
    run(npm, "run", "lint:css", cwd=directory)
    install = [executable("npx"), "playwright", "install"]
    if os.environ.get("PLAYWRIGHT_INSTALL_WITH_DEPS") == "1":
        install.append("--with-deps")
    run(*install, "chromium", cwd=directory)
    # Detect an active listener without treating stale TIME_WAIT sockets as occupied.
    with socket.socket() as probe:
        probe.settimeout(0.1)
        if probe.connect_ex(("127.0.0.1", 8765)) == 0:
            raise ValueError("Port 8765 is already served by another process.")
    with (ARTIFACTS / "terrain-server.log").open("w", encoding="utf-8") as log:
        server = subprocess.Popen(
            [sys.executable, "serve.py"],
            cwd=directory,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
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
            terrain_env = os.environ.copy()
            terrain_env["TERRAIN_EVIDENCE_DIR"] = str(ARTIFACTS / "terrain-evidence")
            run(npm, "test", cwd=directory, env=terrain_env)
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
        raise ValueError(
            "Set ANDROID_HOME to Android SDK with API/build-tools 34.0.0; install JDK 17 and the .NET Android workload."
        )
    run(dotnet, "restore", "src/CableRoutePlanner.Android", "--locked-mode")
    run(
        dotnet,
        "format",
        "style",
        "src/CableRoutePlanner.Android",
        "--no-restore",
        "--verify-no-changes",
        "--severity",
        "warn",
    )
    version_values = subprocess.check_output(
        [sys.executable, "tools/release_version.py"], cwd=ROOT, text=True
    )
    version = next(
        line.split("=", 1)[1]
        for line in version_values.splitlines()
        if line.startswith("RELEASE_VERSION=")
    )
    version_code = next(
        line.split("=", 1)[1]
        for line in version_values.splitlines()
        if line.startswith("ANDROID_VERSION_CODE=")
    )
    configured_version_code = os.environ.get("ANDROID_VERSION_CODE")
    if configured_version_code and configured_version_code != version_code:
        raise ValueError(
            f"ANDROID_VERSION_CODE={configured_version_code} does not match release version {version_code}."
        )
    publish = [
        dotnet,
        "publish",
        "src/CableRoutePlanner.Android",
        "-c",
        "Release",
        f"-p:AndroidSdkDirectory={sdk}",
        f"-p:AndroidVersionCode={version_code}",
    ]
    application_id = os.environ.get("ANDROID_APPLICATION_ID")
    if application_id:
        publish.append(f"-p:ApplicationId={application_id}")
    keystore = os.environ.get("ANDROID_SIGNING_KEYSTORE")
    if keystore:
        publish.extend(
            (
                "-p:AndroidKeyStore=true",
                f"-p:AndroidSigningKeyStore={keystore}",
                f"-p:AndroidSigningKeyAlias={os.environ.get('ANDROID_SIGNING_KEY_ALIAS', 'androiddebugkey')}",
                f"-p:AndroidSigningKeyPass={os.environ.get('ANDROID_SIGNING_KEY_PASSWORD', 'android')}",
                f"-p:AndroidSigningStorePass={os.environ.get('ANDROID_SIGNING_STORE_PASSWORD', 'android')}",
            )
        )
    run(*publish)
    publish_directory = (
        ROOT / "src/CableRoutePlanner.Android/bin/Release/net8.0-android/publish"
    )
    apk_candidates = sorted(publish_directory.glob("*-Signed.apk"))
    if len(apk_candidates) != 1:
        available = sorted(path.name for path in publish_directory.glob("*.apk"))
        raise ValueError(
            f"Expected one signed APK in {publish_directory}, found {len(apk_candidates)}; "
            f"APK files present: {available}"
        )
    apk = apk_candidates[0]
    aapt = (
        Path(sdk) / "build-tools/34.0.0" / ("aapt.exe" if os.name == "nt" else "aapt")
    )
    permissions = subprocess.check_output(
        [str(aapt), "dump", "permissions", str(apk)], text=True
    )
    (ARTIFACTS / "permissions.txt").write_text(permissions, encoding="utf-8")
    if "android.permission.INTERNET" in permissions:
        raise ValueError("Release APK unexpectedly requests INTERNET.")
    apk_entries = subprocess.check_output([str(aapt), "list", str(apk)], text=True)
    for required_asset in ("assets/Data/manifest.json", "assets/Data/geodata.sqlite"):
        if required_asset not in apk_entries.splitlines():
            raise ValueError(
                f"APK is missing required offline map asset {required_asset}."
            )
    badging = subprocess.check_output(
        [str(aapt), "dump", "badging", str(apk)], text=True
    )
    package_line = next(
        (line for line in badging.splitlines() if line.startswith("package:")), ""
    )
    for expected in (
        f"name='{application_id or 'by.cablerouteplanner.app'}'",
        f"versionCode='{version_code}'",
        f"versionName='{version}'",
    ):
        if expected not in package_line:
            raise ValueError(
                f"APK badging does not contain expected value {expected}: {package_line}"
            )
    downloadable_apk = ARTIFACTS / "CableRoutePlanner-CI-Minsk.apk"
    shutil.copy2(apk, downloadable_apk)
    with downloadable_apk.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    (ARTIFACTS / "apk.sha256").write_text(
        f"{digest}  {downloadable_apk.name}\n",
        encoding="utf-8",
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "suite", choices=["lint", "desktop", "data", "terrain", "android", "all"]
    )
    options = parser.parse_args()
    ARTIFACTS.mkdir(exist_ok=True)
    os.environ.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
    os.environ.setdefault("DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE", "true")
    try:
        suites = (
            [lint, desktop, data, terrain, android]
            if options.suite == "all"
            else [globals()[options.suite]]
        )
        for suite in suites:
            suite()
    except (ValueError, OSError, subprocess.SubprocessError, TimeoutError) as error:
        parser.exit(1, f"Local/CI check failed: {error}\n")
