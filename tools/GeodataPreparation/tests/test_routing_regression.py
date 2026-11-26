"""Frozen routing evidence must be complete, reproducible and independently valid."""

import copy
import importlib
import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from shapely.geometry import LineString, Polygon

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))


class RoutingRegressionTests(unittest.TestCase):
    def setUp(self):
        self.assertIsNotNone(importlib.util.find_spec("routing_regression"))
        self.runner = importlib.import_module("routing_regression")
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.folder = Path(self.temporary.name)
        self.connection = sqlite3.connect(self.folder / "geodata.sqlite")
        self.addCleanup(self.connection.close)
        self.connection.executescript(
            "CREATE TABLE features(id,kind,wkb,source_id,approval_id);"
            "CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
            "CREATE TABLE underpass_roads(candidate_id,road_id);"
            "CREATE TABLE underpass_topology(candidate_id,network_id,node_ids,entrance_node_ids);"
        )
        self.scenario = {
            "id": "frozen-case",
            "points": [
                {"Longitude": -0.0001, "Latitude": 0},
                {"Longitude": 0.0001, "Latitude": 0},
            ],
            "routingOptions": {"CellSizeMetres": 25, "MaxNodes": 100000},
            "databaseSha256": "historical-database",
        }
        self.case = {
            "id": "frozen-case",
            "group": "frozen",
            "scenario": self.scenario,
            "expected": "unknown",
        }
        self.coverage = Polygon(
            [(-0.001, -0.001), (0.001, -0.001), (0.001, 0.001), (-0.001, 0.001)]
        )
        self.success = {
            "status": "success",
            "alternatives": [
                {"name": "route", "geometry": self.scenario["points"], "metrics": {}}
            ],
            "diagnostics": {
                "droppedEvents": 0,
                "events": [
                    {
                        "eventId": 5,
                        "fields": {
                            "CalculationId": "test",
                            "CellSizeMetres": 25,
                            "RequestedCellSizeMetres": 25,
                            "NodeCount": 123,
                            "Columns": 12,
                            "Rows": 12,
                            "West": -1,
                            "South": -1,
                            "East": 1,
                            "North": 1,
                            "PortalCount": 0,
                        },
                    },
                    {
                        "eventId": 6,
                        "fields": {
                            "CalculationId": "test",
                            "Profile": "test",
                            "LegIndex": 0,
                            "StartConnections": 2,
                            "DestinationConnections": 2,
                            "PortalNodeCount": 0,
                            "PortalGridConnections": 0,
                            "PortalEdges": 0,
                            "RejectedConnections": 0,
                            "DirectConnection": True,
                        },
                    },
                    {
                        "eventId": 7,
                        "fields": {
                            "CalculationId": "test",
                            "Profile": "test",
                            "LegIndex": 0,
                            "Reason": None,
                            "Cancelled": False,
                            "ExpandedNodes": 1,
                            "EdgeEvaluations": 2,
                            "DirectRoute": True,
                        },
                    },
                ],
            },
        }
        self.no_route = copy.deepcopy(self.success) | {
            "status": "no_route",
            "alternatives": [],
            "error": {
                "reason": "NoRouteInSearchArea",
                "searchFailures": [
                    {"profile": "test", "reason": "NoRouteInSearchArea"}
                ],
            },
        }
        self.no_route["diagnostics"]["events"][-1]["fields"].update(
            Reason="NoRouteInSearchArea", DirectRoute=False
        )

    def add_feature(
        self, feature_id, kind, geometry, network_id=None, nodes=None, entrances=()
    ):
        self.connection.execute(
            "INSERT INTO features VALUES(?,?,?,?,NULL)",
            (feature_id, kind, geometry.wkb, f"way/{feature_id}"),
        )
        west, south, east, north = geometry.bounds
        self.connection.execute(
            "INSERT INTO feature_index VALUES(?,?,?,?,?)",
            (feature_id, west, east, south, north),
        )
        if network_id is not None:
            self.connection.execute(
                "INSERT INTO underpass_topology VALUES(?,?,?,?)",
                (feature_id, network_id, json.dumps(nodes), json.dumps(entrances)),
            )
            self.connection.execute(
                "INSERT INTO underpass_roads VALUES(?,1)", (feature_id,)
            )
        self.connection.commit()

    def invoke(self, result=None, **process_overrides):
        payload = self.success if result is None else result

        def process(command, timeout_seconds, environment):
            Path(command[-1]).write_text(json.dumps(payload), encoding="utf-8")
            return {
                "returncode": 0,
                "timeout": False,
                "elapsed_wall_ms": 12,
                "stdout": "raw stdout\n",
                "stderr": "raw stderr\n",
            } | process_overrides

        return self.runner.run_case(
            self.case,
            self.connection,
            self.coverage,
            self.folder,
            self.folder / "verify.dll",
            self.folder / "results",
            default_underpasses=False,
            command_runner=process,
        )

    def test_modern_manifest_records_actual_database_and_source_provenance(self):
        manifest = {
            "schema_version": 3,
            "database_sha256": self.runner.digest(self.folder / "geodata.sqlite"),
            "sources": [{"sha256": "a" * 64}],
        }
        (self.folder / "manifest.json").write_text(json.dumps(manifest))
        metadata = self.runner.data_provenance(self.folder)
        self.assertEqual(metadata["databaseSha256"], manifest["database_sha256"])
        self.assertEqual(metadata["sourceSha256"], "a" * 64)
        self.assertEqual(metadata["schemaVersion"], 3)

    def test_manifest_checksum_mismatch_is_rejected(self):
        (self.folder / "manifest.json").write_text(
            json.dumps(
                {
                    "schema_version": 3,
                    "database_sha256": "0" * 64,
                    "sources": [{"sha256": "a" * 64}],
                }
            )
        )
        with self.assertRaises(ValueError):
            self.runner.data_provenance(self.folder)

    def test_receipt_from_stale_converter_recipe_is_rejected(self):
        manifest = {
            "schema_version": 3,
            "database_sha256": self.runner.digest(self.folder / "geodata.sqlite"),
            "sources": [{"sha256": "a" * 64}],
        }
        (self.folder / "manifest.json").write_text(json.dumps(manifest))
        receipt = {
            "key": "stale-recipe",
            "source_sha256": "a" * 64,
            "files": {
                "manifest.json": self.runner.digest(self.folder / "manifest.json")
            },
        }
        (self.folder / "package.json").write_text(json.dumps(receipt))
        with self.assertRaises(ValueError):
            self.runner.data_provenance(
                self.folder, expected_package_key="current-recipe"
            )

    def test_frozen_inputs_are_not_rewritten_and_known_controls_are_explicit(self):
        corpus = ROOT / "samples/routing-crossing-mass-v1.json"
        before = corpus.read_bytes()
        cases = self.runner.load_cases(ROOT, corpus)
        frozen = [case["scenario"] for case in cases if case["group"] == "frozen"]
        self.assertEqual(frozen, json.loads(before))
        self.assertEqual(len(frozen), 88)
        self.assertEqual(corpus.read_bytes(), before)
        controls = [case for case in cases if case["group"] == "control"]
        self.assertEqual(len(controls), 6)
        self.assertEqual(sum(case["expected"] == "no_route" for case in controls), 2)
        self.assertEqual(sum(case["expected"] == "success" for case in controls), 4)
        self.assertEqual(
            sum(
                case["expected"] == "success"
                for case in cases
                if case["group"] == "frozen"
            ),
            22,
        )

    def test_success_retains_raw_output_diagnostics_and_original_options(self):
        original = copy.deepcopy(self.scenario)
        record = self.invoke()
        saved = json.loads(
            (self.folder / "results/frozen/frozen-case/scenario.json").read_text()
        )
        raw = json.loads(
            (self.folder / "results/frozen/frozen-case/result.json").read_text()
        )
        self.assertEqual(saved["points"], original["points"])
        self.assertEqual(saved["routingOptions"], original["routingOptions"])
        self.assertEqual(saved["databaseSha256"], "historical-database")
        self.assertTrue(saved["includeDiagnostics"])
        self.assertEqual(self.scenario, original)
        self.assertEqual(raw, self.success)
        self.assertEqual(record["diagnostics"], self.success["diagnostics"])
        self.assertEqual(record["effectiveUnderpasses"], False)
        self.assertEqual(record["auditStatus"], "valid")
        self.assertTrue(record["gatePassed"])
        self.assertEqual(
            (self.folder / "results/frozen/frozen-case/stdout.log").read_text(),
            "raw stdout\n",
        )

    def test_partial_success_does_not_override_failed_process(self):
        record = self.invoke(returncode=9)
        self.assertEqual(record["classification"], "technical_failure")
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["failureReason"], "process_failure")
        self.assertEqual(record["process"]["returncode"], 9)
        self.assertEqual(
            json.loads(
                (self.folder / "results/frozen/frozen-case/result.json").read_text()
            ),
            self.success,
        )

    def test_timeout_does_not_override_partial_success(self):
        record = self.invoke(timeout=True, returncode=None)
        self.assertEqual(record["failureReason"], "process_timeout")
        self.assertFalse(record["gatePassed"])

    def test_absent_result_fails_and_stale_success_is_removed(self):
        self.invoke()

        def no_output(command, timeout_seconds, environment):
            return dict(
                returncode=0, timeout=False, stdout="", stderr="", elapsed_wall_ms=1
            )

        record = self.runner.run_case(
            self.case,
            self.connection,
            self.coverage,
            self.folder,
            self.folder / "verify.dll",
            self.folder / "results",
            default_underpasses=False,
            command_runner=no_output,
        )
        self.assertEqual(record["failureReason"], "missing_result")
        self.assertFalse(record["gatePassed"])
        self.assertFalse(
            (self.folder / "results/frozen/frozen-case/result.json").exists()
        )

    def test_malformed_and_inconsistent_results_fail_integrity(self):
        for payload in (
            {},
            {"status": "success", "alternatives": []},
            {"status": "no_route", "alternatives": [{}]},
            {"status": "resource_limit", "alternatives": []},
        ):
            record = self.invoke(payload)
            self.assertEqual(record["classification"], "technical_failure")
            self.assertFalse(record["gatePassed"])

    def test_no_route_without_structured_reason_is_incomplete_evidence(self):
        record = self.invoke({"status": "no_route", "alternatives": []})
        self.assertEqual(record["classification"], "technical_failure")
        self.assertEqual(record["failureReason"], "inconsistent_result")
        self.assertFalse(record["gatePassed"])

    def test_exploratory_no_route_stays_unknown_with_diagnostics(self):
        self.add_feature(1, "Road", LineString([(0, -0.0001), (0, 0.0001)]))
        payload = self.no_route
        record = self.invoke(payload)
        self.assertEqual(record["classification"], "unknown")
        self.assertTrue(record["gatePassed"])
        self.assertEqual(record["diagnostics"], payload["diagnostics"])
        self.assertEqual(record["straightLineDiagnostic"]["status"], "violation")
        self.assertEqual(
            record["straightLineDiagnostic"]["findings"][0]["sourceId"], "way/1"
        )

    def test_independently_valid_direct_witness_rejects_exploratory_no_route(self):
        record = self.invoke(self.no_route)
        self.assertEqual(record["straightLineDiagnostic"]["status"], "valid")
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["failureReason"], "valid_direct_witness_missed")

    def test_known_positive_no_route_fails_acceptance(self):
        self.case["expected"] = "success"
        record = self.invoke(self.no_route)
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["failureReason"], "known_positive_missing")

    def test_flag_off_control_rejects_unexpected_success(self):
        self.case["expected"] = "no_route"
        record = self.invoke()
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["failureReason"], "negative_control_returned_route")

    def test_actual_endpoints_and_coverage_are_checked(self):
        payload = copy.deepcopy(self.success)
        payload["alternatives"][0]["geometry"][1]["Longitude"] = 0.002
        record = self.invoke(payload)
        reasons = {finding["reason"] for finding in record["audits"][0]["findings"]}
        self.assertIn("endpoint_mismatch", reasons)
        self.assertIn("outside_coverage", reasons)
        self.assertFalse(record["gatePassed"])

    def add_split_underpass(self, second_network=10):
        self.add_feature(1, "Road", LineString([(0, -0.0001), (0, 0.0001)]))
        self.add_feature(
            2,
            "PedestrianUnderpassCandidate",
            LineString([(-0.0001, 0), (0.000005, 0)]),
            10,
            nodes=[101, 102],
            entrances=[101],
        )
        self.add_feature(
            3,
            "PedestrianUnderpassCandidate",
            LineString([(0.000005, 0), (0.0001, 0)]),
            second_network,
            nodes=[102, 103],
            entrances=[103],
        )
        self.scenario["routingOptions"]["AllowExperimentalPedestrianUnderpasses"] = True

    def test_explicit_source_topology_allows_contact_across_split_ways(self):
        self.add_split_underpass()
        self.case.update(expected="success", requireUnderpass=True, requireNetwork=True)
        record = self.invoke()
        self.assertTrue(record["gatePassed"])
        self.assertEqual(record["audits"][0]["status"], "valid")
        self.assertEqual(record["audits"][0]["findings"][0]["underpassIds"], [10])
        self.assertEqual(record["flagOffAudits"][0]["status"], "violation")
        self.assertTrue(record["audits"][0]["entireRouteInsideMappedNetwork1m"])

    def test_touching_geometry_without_shared_source_network_is_rejected(self):
        self.add_split_underpass(second_network=11)
        record = self.invoke()
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["auditStatus"], "violation")

    def test_underpass_positive_requires_independent_underpass_use(self):
        self.case.update(expected="success", requireUnderpass=True)
        record = self.invoke()
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["failureReason"], "underpass_control_not_exercised")

    def test_unknown_route_audit_fails_gate(self):
        self.add_feature(1, "Road", LineString([(-0.0001, 0), (0.0001, 0)]))
        record = self.invoke()
        self.assertEqual(record["auditStatus"], "unknown")
        self.assertFalse(record["gatePassed"])

    def test_report_persists_counts_failures_and_historical_comparison(self):
        first = self.invoke()
        second = first | {
            "id": "negative",
            "status": "error",
            "gatePassed": False,
            "classification": "technical_failure",
            "failureReason": "process_failure",
        }
        report = {
            "provenance": {"databaseSha256": "a" * 64},
            "results": [first, second],
        }
        self.runner.write_reports(report, self.folder / "report")
        saved = json.loads((self.folder / "report/results.json").read_text())
        markdown = (self.folder / "report/summary.md").read_text()
        self.assertEqual(saved["counts"]["technical_failure"], 1)
        self.assertFalse(saved["gatePassed"])
        self.assertIn("process_failure", markdown)
        self.assertIn("unknown", markdown)
        self.assertEqual(
            saved["results"][0]["diagnostics"], self.success["diagnostics"]
        )

    def test_same_package_baseline_loss_fails_current_gate_and_records_transition(self):
        baseline = self.invoke()
        current = baseline | {
            "status": "no_route",
            "classification": "unknown",
            "audits": [],
            "auditStatus": "unknown",
        }
        transitions = self.runner.compare_baseline([current], [baseline])
        self.assertEqual(
            transitions,
            [
                {
                    "id": "frozen-case",
                    "baselineStatus": "success",
                    "currentStatus": "no_route",
                    "baselineAuditStatus": "valid",
                    "currentAuditStatus": "unknown",
                    "lostValidRoute": True,
                }
            ],
        )
        self.assertFalse(current["gatePassed"])
        self.assertEqual(current["failureReason"], "baseline_valid_route_lost")

    def test_baseline_inventory_mismatch_is_rejected(self):
        with self.assertRaises(ValueError):
            self.runner.compare_baseline([self.invoke()], [])

    def test_baseline_technical_failure_is_not_silently_accepted(self):
        current = self.invoke()
        report = {
            "results": [current],
            "baselineResults": [current | {"gatePassed": False}],
        }
        self.runner.write_reports(report, self.folder / "report")
        saved = json.loads((self.folder / "report/results.json").read_text())
        self.assertFalse(saved["gatePassed"])

    def test_incomplete_corpus_report_cannot_pass(self):
        report = {
            "results": [self.invoke()],
            "expectedFrozenCount": 88,
            "expectedControlCount": 6,
        }
        self.runner.write_reports(report, self.folder / "report")
        saved = json.loads((self.folder / "report/results.json").read_text())
        self.assertFalse(saved["gatePassed"])

    def test_default_flag_provenance_uses_actual_application_source(self):
        options = ROOT / "src/CableRoutePlanner.Application/RoutingOptions.cs"
        self.assertFalse(self.runner.underpass_default(options))

    def test_cli_setup_failure_writes_machine_and_markdown_evidence(self):
        output = self.folder / "cli"
        process = subprocess.run(
            [
                sys.executable,
                str(ROOT / "tools/routing_regression.py"),
                "--data",
                str(self.folder / "missing-data"),
                "--verify-dll",
                str(self.folder / "missing.dll"),
                "--output",
                str(output),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(process.returncode, 1)
        report = json.loads((output / "results.json").read_text())
        self.assertFalse(report["gatePassed"])
        self.assertEqual(report["fatalError"]["type"], "FileNotFoundError")
        self.assertIn("Gate: **FAIL**", (output / "summary.md").read_text())

    def test_invalid_raw_json_is_retained_as_failed_evidence(self):
        def invalid_json(command, timeout_seconds, environment):
            Path(command[-1]).write_text("{partial", encoding="utf-8")
            return dict(
                returncode=0, timeout=False, stdout="", stderr="", elapsed_wall_ms=1
            )

        record = self.runner.run_case(
            self.case,
            self.connection,
            self.coverage,
            self.folder,
            self.folder / "verify.dll",
            self.folder / "invalid-results",
            default_underpasses=False,
            command_runner=invalid_json,
        )
        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["evidenceError"]["type"], "JSONDecodeError")
        self.assertEqual(
            (
                self.folder / "invalid-results/frozen/frozen-case/result.json"
            ).read_text(),
            "{partial",
        )

    def test_current_result_requires_complete_structured_search_diagnostics(self):
        self.add_feature(1, "Road", LineString([(0, -0.0001), (0, 0.0001)]))
        for diagnostics in (
            None,
            {},
            {"events": [], "droppedEvents": 0},
            self.success["diagnostics"] | {"droppedEvents": 1},
        ):
            record = self.invoke(self.no_route | {"diagnostics": diagnostics})
            self.assertFalse(record["gatePassed"])
            self.assertEqual(record["classification"], "technical_failure")

    def test_legacy_baseline_keeps_typed_failure_without_diagnostics(self):
        self.case["group"] = "baseline"
        self.add_feature(1, "Road", LineString([(0, -0.0001), (0, 0.0001)]))
        record = self.invoke(self.no_route | {"diagnostics": None})
        self.assertTrue(record["gatePassed"])
        self.assertEqual(record["classification"], "unknown")

    def test_computed_polyline_length_enforces_inclusive_limit(self):
        length = self.runner.distance((-0.0001, 0), (0.0001, 0))
        self.scenario["routingOptions"]["MaxRouteLengthMetres"] = length
        self.assertTrue(self.invoke()["gatePassed"])
        self.scenario["routingOptions"]["MaxRouteLengthMetres"] = length - 0.001
        record = self.invoke()
        self.assertFalse(record["gatePassed"])
        self.assertIn(
            "route_length_limit",
            {finding["reason"] for finding in record["audits"][0]["findings"]},
        )

    def test_indirect_control_requires_actual_bridge_witness(self):
        self.case.update(expected="success", requireBridgeSource="way/9")
        self.assertFalse(self.invoke()["gatePassed"])
        self.add_feature(1, "Road", LineString([(0, -0.0001), (0, 0.0001)]))
        self.add_feature(
            9, "BridgeCandidate", LineString([(-0.00005, 0), (0.00005, 0)])
        )
        record = self.invoke()
        self.assertTrue(record["gatePassed"])
        self.assertIn("way/9", record["audits"][0]["bridgeSourceIds"])

    def test_same_component_does_not_allow_transfer_at_unshared_colocated_nodes(self):
        unit = 1 / 111195.0802335
        self.scenario["points"] = [
            {"Longitude": -20 * unit, "Latitude": 0},
            {"Longitude": 0, "Latitude": -20 * unit},
        ]
        self.scenario["routingOptions"]["AllowExperimentalPedestrianUnderpasses"] = True
        self.case.update(expected="success", requireUnderpass=True, requireNetwork=True)
        self.add_feature(
            1, "Road", LineString([(-10 * unit, -30 * unit), (-10 * unit, 30 * unit)])
        )
        for feature_id, coords, nodes, entrances in (
            (2, [(-20, 0), (0, 0), (20, 0)], [1, 2, 3], [1]),
            (3, [(0, -20), (0, 0), (0, 20)], [4, 5, 6], [4]),
            (4, [(20, 0), (20, 20), (0, 20)], [3, 7, 6], []),
        ):
            self.add_feature(
                feature_id,
                "PedestrianUnderpassCandidate",
                LineString([(x * unit, y * unit) for x, y in coords]),
                10,
                nodes=nodes,
                entrances=entrances,
            )
        payload = copy.deepcopy(self.success)
        payload["alternatives"][0]["geometry"] = [
            self.scenario["points"][0],
            {"Longitude": 0, "Latitude": 0},
            self.scenario["points"][1],
        ]
        record = self.invoke(payload)
        self.assertFalse(record["gatePassed"])
        self.assertIn(
            "underpass_topology_jump",
            {finding["reason"] for finding in record["audits"][0]["findings"]},
        )
        payload["alternatives"][0]["geometry"] = [
            {"Longitude": x * unit, "Latitude": y * unit}
            for x, y in [(-20, 0), (0, 0), (20, 0), (20, 20), (0, 20), (0, 0), (0, -20)]
        ]
        self.assertTrue(self.invoke(payload)["gatePassed"])

    def test_fixed_baseline_direct_witness_does_not_fail_comparison(self):
        current = self.invoke()
        baseline = current | {
            "group": "baseline",
            "status": "no_route",
            "auditStatus": "unknown",
            "gatePassed": False,
            "classification": "missed_valid_path",
            "failureReason": "valid_direct_witness_missed",
        }
        report = {"results": [current], "baselineResults": [baseline]}
        self.runner.write_reports(report, self.folder / "comparison")
        self.assertTrue(
            json.loads((self.folder / "comparison/results.json").read_text())[
                "gatePassed"
            ]
        )

    def test_length_rejection_after_successful_search_keeps_complete_diagnostics(self):
        self.scenario["routingOptions"]["MaxRouteLengthMetres"] = 10
        payload = copy.deepcopy(self.success) | {
            "status": "no_route",
            "alternatives": [],
            "error": {
                "reason": "RouteLengthLimitExceeded",
                "searchFailures": [
                    {"profile": "test", "reason": "RouteLengthLimitExceeded"}
                ],
            },
        }
        record = self.invoke(payload)
        self.assertTrue(record["gatePassed"])
        self.assertEqual(record["classification"], "unknown")

    def test_post_run_snapshot_rejects_changed_binary_or_data(self):
        binary = self.folder / "verify.dll"
        binary.write_bytes(b"initial")
        paths = {"binary": binary, "database": self.folder / "geodata.sqlite"}
        before = self.runner.evidence_snapshot(paths)
        self.runner.validate_snapshot(paths, before)
        binary.write_bytes(b"different")
        with self.assertRaises(ValueError):
            self.runner.validate_snapshot(paths, before)

    def test_complete_results_cannot_pass_before_provenance_revalidation(self):
        report = {
            "results": [self.invoke()],
            "expectedFrozenCount": 1,
            "expectedControlCount": 0,
        }
        self.runner.write_reports(report, self.folder / "final-gate")
        self.assertFalse(report["gatePassed"])
        report["provenanceRevalidated"] = {"unchanged": True}
        self.runner.write_reports(report, self.folder / "final-gate")
        self.assertTrue(report["gatePassed"])

    def test_known_recovered_cases_are_positive_without_changing_frozen_inputs(self):
        corpus = ROOT / "samples/routing-crossing-mass-v1.json"
        before = corpus.read_bytes()

        cases = self.runner.load_cases(ROOT, corpus)

        recovered = {
            "mass-naliboki-bridge-02",
            "mass-gomel-bridge-01",
            "mass-gomel-bridge-03",
            "mass-gomel-bridge-05",
            "mass-naliboki-multi-06",
            "mass-smolevichi-no_candidate-11",
            "mass-pukhovichi-multi-07",
        }
        selected = [case for case in cases if case["id"] in recovered]
        self.assertEqual(len(selected), 7)
        self.assertTrue(all(case["expected"] == "success" for case in selected))
        self.assertTrue(
            all(case.get("knownWitnessCatalogueSha256") for case in selected)
        )
        self.assertEqual(corpus.read_bytes(), before)

    def test_mixed_recovery_calculation_ids_fail_raw_result_integrity(self):
        payload = copy.deepcopy(self.success)
        payload["diagnostics"]["events"][0]["fields"]["Attempt"] = 0
        final = copy.deepcopy(payload["diagnostics"]["events"][0])
        final["fields"].update(Attempt=1, CalculationId="different")
        payload["diagnostics"]["events"].append(final)

        record = self.invoke(payload)

        self.assertFalse(record["gatePassed"])
        self.assertEqual(record["failureReason"], "incomplete_diagnostics")

    def test_successful_final_attempt_and_typed_surface_diagnostics_are_complete(self):
        payload = copy.deepcopy(self.success)
        events = payload["diagnostics"]["events"]
        events[0]["fields"]["Attempt"] = 0
        events[-1]["fields"].update(Reason="NoRouteInSearchArea", DirectRoute=False)
        final = copy.deepcopy(self.success["diagnostics"]["events"])
        final[0]["fields"].update(
            Attempt=1, RequestedCellSizeMetres=5, CellSizeMetres=5
        )
        events.extend(final)
        events.append(
            {
                "eventId": 8,
                "fields": {
                    "CalculationId": "test",
                    "Attempt": 1,
                    "ConnectionRadiusCells": 32,
                    "SurfaceRecovery": True,
                },
            }
        )
        events.append(
            {
                "eventId": 9,
                "fields": {
                    "CalculationId": "test",
                    "Profile": "test",
                    "LegIndex": 0,
                    "NodeCount": 10,
                    "ExpandedNodes": 3,
                    "InputVertexCount": 12,
                    "ResourceLimited": False,
                    "Found": True,
                },
            }
        )

        self.assertTrue(self.invoke(payload)["gatePassed"])
        for key, value in (("NodeCount", -1), ("Found", "true")):
            malformed = copy.deepcopy(payload)
            malformed["diagnostics"]["events"][-1]["fields"][key] = value
            self.assertEqual(
                self.invoke(malformed)["failureReason"], "incomplete_diagnostics"
            )

    def test_last_mesh_without_a_completed_search_cannot_borrow_previous_outcome(self):
        payload = copy.deepcopy(self.success)
        payload["diagnostics"]["events"][0]["fields"]["Attempt"] = 0
        final = copy.deepcopy(payload["diagnostics"]["events"][0])
        final["fields"]["Attempt"] = 1
        payload["diagnostics"]["events"].append(final)

        self.assertEqual(
            self.invoke(payload)["failureReason"], "incomplete_diagnostics"
        )


if __name__ == "__main__":
    unittest.main()
