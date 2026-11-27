import importlib
import io
import subprocess
import sys
import unittest
from contextlib import redirect_stdout
from unittest.mock import patch

from tools import check_coverage

with patch.dict(sys.modules, {"check_coverage": check_coverage}):
    check = importlib.import_module("tools.check")


class DotnetFormatTests(unittest.TestCase):
    def setUp(self):
        self.command = (
            "C:/Program Files/dotnet/dotnet.EXE",
            "format",
            "style",
            "CableRoutePlanner.Desktop.slnf",
            "--no-restore",
            "--verify-no-changes",
            "--severity",
            "warn",
        )
        self.environment = {"PATH": "sdk", "DOTNET_ROOT": "sdk"}

    def invoke(self, outcomes):
        remaining = iter(outcomes)

        def formatter(command, *, cwd, check, timeout, env):
            self.assertEqual(tuple(command), self.command)
            self.assertEqual(env, self.environment)
            self.assertTrue(check)
            outcome = next(remaining)
            if outcome is not None:
                raise outcome
            print("Formatter verification completed.")

        output = io.StringIO()
        with (
            redirect_stdout(output),
            patch.object(check.subprocess, "run", side_effect=formatter),
        ):
            check.run_dotnet_format(
                self.command[0], *self.command[2:], env=self.environment
            )
        return output.getvalue()

    def failure(self, code):
        return subprocess.CalledProcessError(code, self.command)

    def test_successful_verification_finishes_without_retry(self):
        output = self.invoke([None])
        self.assertIn("Formatter verification completed.", output)

    def test_cli_probe_failure_retries_until_verification_succeeds(self):
        output = self.invoke([self.failure(4), self.failure(4), None])
        self.assertIn("Formatter verification completed.", output)

    def test_persistent_cli_probe_failure_preserves_final_failure(self):
        failure = self.failure(4)
        with self.assertRaises(subprocess.CalledProcessError) as caught:
            self.invoke([self.failure(4), self.failure(4), failure, None])
        self.assertIs(caught.exception, failure)

    def test_formatting_violation_fails_without_retry(self):
        failure = self.failure(2)
        with self.assertRaises(subprocess.CalledProcessError) as caught:
            self.invoke([failure, None])
        self.assertIs(caught.exception, failure)

    def test_other_formatter_exit_fails_without_retry(self):
        for code in (1, 3, 5):
            with self.subTest(code=code):
                failure = self.failure(code)
                with self.assertRaises(subprocess.CalledProcessError) as caught:
                    self.invoke([failure, None])
                self.assertIs(caught.exception, failure)

    def test_formatting_violation_after_cli_probe_retry_is_preserved(self):
        failure = self.failure(2)
        with self.assertRaises(subprocess.CalledProcessError) as caught:
            self.invoke([self.failure(4), failure, None])
        self.assertIs(caught.exception, failure)

    def test_process_start_failure_fails_without_retry(self):
        failure = FileNotFoundError(2, "dotnet", self.command[0])
        with self.assertRaises(FileNotFoundError) as caught:
            self.invoke([failure, None])
        self.assertIs(caught.exception, failure)

    def test_desktop_combined_formatter_recovers_cli_probe_before_build(self):
        expected = (
            self.command[0],
            "format",
            check.SOLUTION,
            "--no-restore",
            "--verify-no-changes",
            "--severity",
            "warn",
        )
        probe_failed = False
        reached_build = OSError()

        def process(command, **options):
            nonlocal probe_failed
            if command[1] == "format":
                self.assertEqual(tuple(command), expected)
                if not probe_failed:
                    probe_failed = True
                    raise subprocess.CalledProcessError(4, command)
            if command[1] == "build":
                raise reached_build
            return subprocess.CompletedProcess(command, 0)

        with (
            patch.object(check, "executable", return_value=self.command[0]),
            patch.object(check.subprocess, "run", side_effect=process),
            redirect_stdout(io.StringIO()),
            self.assertRaises(OSError) as raised,
        ):
            check.desktop()
        self.assertIs(raised.exception, reached_build)


if __name__ == "__main__":
    unittest.main()
