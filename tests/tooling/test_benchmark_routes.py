import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools.benchmark_routes import measure, run_bounded_command, write_reports


class BenchmarkMeasurements(unittest.TestCase):
    def test_bounded_timeout_preserves_bytes_text_and_missing_streams(self):
        streams = (
            (b"partial \xff\r\n", "partial \ufffd\r\n"),
            (
                "\u043c\u0430\u0440\u0448\u0440\u0443\u0442\r\n",
                "\u043c\u0430\u0440\u0448\u0440\u0443\u0442\r\n",
            ),
            (None, ""),
        )
        for stdout, expected_stdout in streams:
            for stderr, expected_stderr in streams:
                with self.subTest(
                    stdout_type=type(stdout).__name__, stderr_type=type(stderr).__name__
                ):
                    timeout = subprocess.TimeoutExpired(
                        ["verify"], 120, output=stdout, stderr=stderr
                    )
                    with patch(
                        "tools.benchmark_routes.subprocess.run", side_effect=timeout
                    ):
                        result = run_bounded_command(["verify"], 120)

                    self.assertTrue(result["timeout"])
                    self.assertIsNone(result["returncode"])
                    self.assertEqual(result["stdout"], expected_stdout)
                    self.assertEqual(result["stderr"], expected_stderr)
                    self.assertIsInstance(result["elapsed_wall_ms"], int)
                    self.assertGreaterEqual(result["elapsed_wall_ms"], 0)

    def test_measure_timeout_preserves_bytes_text_and_missing_streams(self):
        streams = (
            (b"partial \xff\r\n", "partial \ufffd\r\n"),
            (
                "\u043c\u0430\u0440\u0448\u0440\u0443\u0442\r\n",
                "\u043c\u0430\u0440\u0448\u0440\u0443\u0442\r\n",
            ),
            (None, ""),
        )
        for stdout, expected_stdout in streams:
            for stderr, expected_stderr in streams:
                with self.subTest(
                    stdout_type=type(stdout).__name__, stderr_type=type(stderr).__name__
                ):
                    timeout = subprocess.TimeoutExpired(
                        ["verify"], 120, output=stdout, stderr=stderr
                    )
                    with patch(
                        "tools.benchmark_routes.subprocess.run", side_effect=timeout
                    ):
                        result = measure(["verify"], Path("unused"), 120)

                    self.assertEqual(result["status"], "timeout")
                    self.assertIsNone(result["exit_code"])
                    self.assertEqual(result["metrics"], [])
                    self.assertEqual(result["stdout"], expected_stdout)
                    self.assertEqual(result["stderr"], expected_stderr)

    def test_timeout_keeps_raw_runner_output_and_existing_measurement_tail_limit(self):
        stdout = "out" * 2500
        stderr = b"err" * 2500
        timeout = subprocess.TimeoutExpired(
            ["verify"], 120, output=stdout, stderr=stderr
        )
        with patch("tools.benchmark_routes.subprocess.run", side_effect=timeout):
            raw = run_bounded_command(["verify"], 120)
            measurement = measure(["verify"], Path("unused"), 120)

        self.assertEqual(raw["stdout"], stdout)
        self.assertEqual(raw["stderr"], "err" * 2500)
        self.assertEqual(measurement["stdout"], stdout[-6000:])
        self.assertEqual(measurement["stderr"], ("err" * 2500)[-6000:])

    def test_success_reads_route_metrics_from_actual_output(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "route.json"
            payload = {"features": [{"properties": {"metrics": {"LengthMetres": 123}}}]}
            script = "from pathlib import Path; Path(__import__('sys').argv[1]).write_text(__import__('sys').argv[2])"
            result = measure(
                [sys.executable, "-c", script, str(output), json.dumps(payload)],
                output,
                5,
            )
            self.assertEqual("completed", result["status"])
            self.assertEqual([{"LengthMetres": 123}], result["metrics"])
            self.assertEqual(0, result["exit_code"])

    def test_empty_output_cannot_be_reported_as_completed_route(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "route.json"
            output.write_text('{"features": []}')
            result = measure([sys.executable, "-c", "pass"], output, 5)
            self.assertEqual("no_routes", result["status"])
            self.assertEqual([], result["metrics"])

    def test_readable_report_keeps_timeout_distinct_from_success(self):
        report = {
            "results": [
                dict(
                    id="local", status="completed", elapsed_wall_ms=1500, metrics=[{}]
                ),
                dict(
                    id="intercity", status="timeout", elapsed_wall_ms=120000, metrics=[]
                ),
            ]
        }
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            write_reports(report, output)
            self.assertEqual(report, json.loads((output / "report.json").read_text()))
            text = (output / "report.md").read_text()
            self.assertIn("| local | completed | 1.5 | 1 |", text)
            self.assertIn("| intercity | timeout | 120.0 | 0 |", text)

    def test_failed_process_cannot_be_reported_as_a_successful_route(self):
        result = measure(
            [sys.executable, "-c", "raise SystemExit(7)"], Path("unused"), 5
        )
        self.assertEqual("failed", result["status"])
        self.assertEqual(7, result["exit_code"])
        self.assertEqual([], result["metrics"])

    def test_timeout_is_reported_and_does_not_hang_the_remaining_measurements(self):
        result = measure(
            [sys.executable, "-c", "import time; time.sleep(10)"], Path("unused"), 0.1
        )
        self.assertEqual("timeout", result["status"])
        self.assertIsNone(result["exit_code"])
        self.assertEqual([], result["metrics"])
