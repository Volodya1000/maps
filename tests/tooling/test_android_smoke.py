"""Exercise the smoke command's persisted outcome with a deterministic adb boundary."""

import contextlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools import android_smoke


class AndroidSmokeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.apk = self.root / "map.apk"
        self.apk.write_bytes(b"synthetic APK")
        self.package = "by.cablerouteplanner.preview.fixture"
        self.ready = True
        self.alive = True
        self.resumed = True
        self.crash = False
        self.logcat_returncode = 0
        self.system_logcat_returncode = 0
        self.app_capture_returncode = 0

    def adb(self, command, **kwargs):
        args = command[1:]
        stdout = ""
        returncode = 0
        if args[:2] == ["shell", "getprop"]:
            stdout = "15" if args[-1].endswith("release") else "x86_64"
        elif "resolve-activity" in args:
            stdout = self.package + "/MainActivity\n"
        elif args[:3] == ["shell", "am", "start"]:
            stdout = "Status: ok\n"
        elif args[:2] == ["shell", "pidof"]:
            stdout = "123" if self.alive else ""
        elif args[:3] == ["shell", "dumpsys", "activity"]:
            stdout = "mResumedActivity: " + (
                self.package if self.resumed else "other.app"
            )
        elif args[:2] == ["logcat", "-b"]:
            stdout = self.package + " FATAL EXCEPTION" if self.crash else ""
        elif args[:2] == ["logcat", "-d"]:
            own = (
                "I/app(123): CRP_OFFLINE_MAP_READY region=belarus features=3\n"
                if self.ready
                else ""
            )
            other = "I/other(987): CRP_OFFLINE_MAP_READY region=minsk features=1\n"
            stdout = own if "--pid=123" in args else other + own
            if "brief" in args:
                returncode = self.logcat_returncode
            elif "--pid=123" in args:
                returncode = self.app_capture_returncode
            else:
                returncode = self.system_logcat_returncode
        elif args[:2] == ["exec-out", "screencap"]:
            stdout = b"synthetic screenshot"
        return subprocess.CompletedProcess(
            command, returncode, stdout=stdout, stderr=""
        )

    def run_smoke(self):
        argv = [
            "android_smoke",
            "--apk",
            str(self.apk),
            "--package",
            self.package,
            "--output",
            str(self.root / "output"),
            "--launches",
            "2",
            "--settle-seconds",
            "1",
            "--wait-map-ready",
        ]
        with (
            patch.object(sys, "argv", argv),
            patch.object(android_smoke.subprocess, "run", side_effect=self.adb),
            patch.object(android_smoke.time, "sleep"),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            android_smoke.main()

    def report(self):
        return json.loads((self.root / "output/report.json").read_text())

    def test_two_ready_resumed_crash_free_launches_pass_and_preserve_evidence(self):
        self.run_smoke()
        report = self.report()
        self.assertTrue(report["success"])
        self.assertEqual([1, 2], [entry["attempt"] for entry in report["launches"]])
        for entry in report["launches"]:
            self.assertIn("region=belarus", entry["map_ready"])
            self.assertEqual("123", entry["pid"])
            self.assertTrue(
                (self.root / f"output/launch-{entry['attempt']}.png").is_file()
            )

    def test_readiness_from_another_process_cannot_pass(self):
        self.ready = False
        with self.assertRaises(RuntimeError):
            self.run_smoke()
        self.assertFalse(self.report()["success"])
        self.assertEqual([], self.report()["launches"])

    def test_partial_ready_logcat_with_nonzero_exit_persists_failure_details(self):
        self.logcat_returncode = 137
        with self.assertRaises(subprocess.CalledProcessError) as raised:
            self.run_smoke()
        self.assertEqual(137, raised.exception.returncode)
        report = self.report()
        self.assertFalse(report["success"])
        self.assertEqual([], report["launches"])
        self.assertEqual(137, report["adb_failure"]["returncode"])
        self.assertEqual("logcat", report["adb_failure"]["command"][1])
        self.assertIn(
            "CRP_OFFLINE_MAP_READY",
            (self.root / "output/adb-failure.stdout").read_text(),
        )

    def test_system_log_transport_failure_cannot_replace_required_app_evidence(self):
        self.system_logcat_returncode = 255
        self.run_smoke()
        report = self.report()
        self.assertTrue(report["success"])
        self.assertEqual(2, len(report["launches"]))
        for attempt in (1, 2):
            log = (self.root / f"output/launch-{attempt}.logcat.txt").read_text()
            self.assertIn("region=belarus", log)
            self.assertNotIn("I/other(987)", log)

    def test_application_log_transport_failure_fails_closed(self):
        self.app_capture_returncode = 255
        with self.assertRaises(subprocess.CalledProcessError):
            self.run_smoke()
        report = self.report()
        self.assertFalse(report["success"])
        self.assertEqual([], report["launches"])
        self.assertEqual(255, report["adb_failure"]["returncode"])

    def test_liveness_resumed_activity_and_crash_fail_closed(self):
        for field in ("alive", "resumed", "crash"):
            with self.subTest(field=field):
                self.alive, self.resumed, self.crash = True, True, False
                setattr(self, field, field == "crash")
                with self.assertRaises(RuntimeError):
                    self.run_smoke()
                self.assertFalse(self.report()["success"])
                self.assertEqual([], self.report()["launches"])

    def test_failed_readiness_preserves_screen_and_original_failure(self):
        self.ready = False
        with self.assertRaises(RuntimeError):
            self.run_smoke()
        self.assertEqual(
            (self.root / "output/failure.png").read_bytes(), b"synthetic screenshot"
        )
        self.assertFalse(self.report()["success"])
        self.assertEqual([], self.report()["launches"])

    def test_failure_capture_error_does_not_replace_readiness_failure(self):
        self.ready = False
        original_adb = self.adb

        def fail_capture(command, **kwargs):
            if "screencap" in command:
                raise subprocess.TimeoutExpired(command, 90)
            return original_adb(command, **kwargs)

        self.adb = fail_capture
        with self.assertRaises(RuntimeError):
            self.run_smoke()
        report = self.report()
        self.assertFalse(report["success"])
        self.assertEqual([], report["launches"])
        self.assertIn("capture_error", report)
