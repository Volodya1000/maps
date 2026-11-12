import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

MODULE = Path(__file__).resolve().parents[2] / "ci/routing_pilot_ci.py"
SPEC = importlib.util.spec_from_file_location("routing_pilot_ci", MODULE)
ci = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ci)


class RoutingPilotCiTests(unittest.TestCase):
    def test_extract_database_rejects_sha_mismatch(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory)
            (data / "manifest.json").write_text(
                json.dumps({"database_sha256": "0" * 64}), encoding="utf-8"
            )
            import gzip

            with gzip.open(data / "geodata.sqlite.gz", "wb") as stream:
                stream.write(b"database")

            with self.assertRaisesRegex(RuntimeError, "database SHA-256 mismatch"):
                ci.extract_database(data)

    def test_report_pilot_rejects_missing_report(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(
                RuntimeError, "Harness did not produce a report"
            ):
                ci.report_pilot(
                    Path(directory) / "report.json", Path(directory) / "summary.md"
                )

    def test_report_pilot_writes_summary_and_rejects_failed_acceptance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "report.json").write_text(
                json.dumps(
                    {
                        "acceptance": {
                            "harnessIntegrity": "fail",
                            "pilotAccepted": False,
                            "successfulRoutesWithAllowedRoadContact": 0,
                            "unmetRequirements": ["route"],
                            "unverifiedRequirements": ["coverage"],
                        }
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(RuntimeError, "Harness incomplete"):
                ci.report_pilot(root / "report.json", root / "summary.md")
            self.assertIn(
                "Harness integrity: **fail**", (root / "summary.md").read_text()
            )

    def test_verify_indirect_rejects_missing_report(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(
                RuntimeError, "Indirect crossing study did not produce a report"
            ):
                ci.verify_indirect(Path(directory) / "report.json")

    def test_verify_indirect_rejects_invalid_route_audit(self):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "report.json"
            report.write_text(
                json.dumps(
                    {
                        "assessment": {"classification": "valid_route_found"},
                        "invocation": {
                            "userPointCount": 2,
                            "witnessVerticesPassedAsUserPoints": False,
                        },
                        "routeAudits": [{"status": "violation"}],
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaisesRegex(
                RuntimeError, "unaudited or invalid alternative"
            ):
                ci.verify_indirect(report)
