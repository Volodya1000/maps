import json
import sys
import tempfile
import unittest
from pathlib import Path

from tools.benchmark_routes import measure, write_reports


class BenchmarkMeasurements(unittest.TestCase):
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
