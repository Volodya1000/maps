"""Test the smoke report contract using a controlled adb transport."""

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools import android_smoke


class AndroidSmokeMetricsTests(unittest.TestCase):
    def run_smoke(self, root, *, alive=True):
        apk = root / "map.apk"
        apk.write_bytes(b"exact distributable APK")
        package = "by.test.preview"
        component = package + "/MainActivity"

        def transport(command, **options):
            arguments = command[1:]
            if arguments == ["shell", "getprop", "ro.build.version.release"]:
                output = "15"
            elif arguments == ["shell", "getprop", "ro.product.cpu.abi"]:
                output = "x86_64"
            elif arguments[:4] == ["shell", "cmd", "package", "resolve-activity"]:
                output = component
            elif arguments == ["shell", "pidof", package]:
                output = "1234" if alive else ""
            elif arguments == ["logcat", "-d", "-v", "brief"]:
                output = "CRP_OFFLINE_MAP_READY"
            elif arguments == ["shell", "dumpsys", "activity", "activities"]:
                output = "mResumedActivity: " + component
            elif arguments == ["exec-out", "screencap", "-p"]:
                output = b"image"
            elif arguments in (
                ["wait-for-device"],
                ["install", "-r", str(apk)],
                ["shell", "am", "force-stop", package],
                ["logcat", "-c"],
                ["shell", "am", "start", "-W", "-n", component],
                ["logcat", "-d", "-v", "threadtime"],
                ["logcat", "-b", "crash", "-d"],
            ):
                output = ""
            else:
                raise AssertionError(arguments)
            return subprocess.CompletedProcess(command, 0, stdout=output, stderr="")

        args = [
            "android_smoke",
            "--apk",
            str(apk),
            "--package",
            package,
            "--output",
            str(root / "report"),
            "--launches",
            "2",
            "--settle-seconds",
            "1",
            "--wait-map-ready",
        ]
        with (
            patch.object(sys, "argv", args),
            patch.object(android_smoke.subprocess, "run", side_effect=transport),
            patch.object(android_smoke.time, "sleep"),
        ):
            android_smoke.main()

    def test_success_report_binds_install_and_map_timings_to_exact_apk(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.run_smoke(root)
            report = json.loads((root / "report/report.json").read_text())
            self.assertTrue(report["success"])
            self.assertEqual(
                hashlib.sha256(b"exact distributable APK").hexdigest(), report["sha256"]
            )
            self.assertEqual(len(b"exact distributable APK"), report["apk_bytes"])
            self.assertGreaterEqual(report["install_seconds"], 0)
            self.assertEqual(2, len(report["launches"]))
            for launch in report["launches"]:
                self.assertGreaterEqual(launch["activity_start_seconds"], 0)
                self.assertGreaterEqual(
                    launch["map_ready_seconds"], launch["activity_start_seconds"]
                )
                self.assertEqual("CRP_OFFLINE_MAP_READY", launch["map_ready"])

    def test_failed_launch_preserves_install_evidence_without_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaises(RuntimeError):
                self.run_smoke(root, alive=False)
            report = json.loads((root / "report/report.json").read_text())
            self.assertFalse(report["success"])
            self.assertEqual([], report["launches"])
            self.assertGreaterEqual(report["install_seconds"], 0)
