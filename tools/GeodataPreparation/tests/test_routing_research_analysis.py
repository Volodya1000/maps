"""Research conclusions require independent evidence, never optimistic labels."""

import copy
import importlib
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))


class ResearchAnalysisTests(unittest.TestCase):
    def setUp(self):
        self.analysis = importlib.import_module("routing_research_analysis")
        self.scenario = {
            "points": [
                {"Longitude": 27, "Latitude": 54},
                {"Longitude": 27.01, "Latitude": 54},
            ],
            "routingOptions": {"MaxRouteLengthMetres": 10000},
        }
        self.record = {
            "id": "case",
            "status": "no_route",
            "process": {"timeout": False, "returncode": 0},
            "error": {
                "reason": "NoRouteInSearchArea",
                "searchFailures": [{"profile": "p", "reason": "NoRouteInSearchArea"}],
            },
            "diagnostics": {
                "droppedEvents": 0,
                "events": [
                    {
                        "eventId": 5,
                        "fields": {
                            "West": 26.99,
                            "South": 53.99,
                            "East": 27.02,
                            "North": 54.01,
                            "CellSizeMetres": 25,
                        },
                    }
                ],
            },
            "straightLineDiagnostic": {
                "status": "violation",
                "findings": [{"reason": "road_contact", "allowed": False}],
            },
        }

    def test_blocked_direct_segment_does_not_prove_refusal_or_algorithm_defect(self):
        result = self.analysis.classify(self.record, self.scenario)
        self.assertEqual("indeterminate", result["outcome"])
        groups = self.analysis.causes(self.record, self.scenario, result)
        self.assertTrue(any(g["code"] == "direct_road_barrier" for g in groups))
        self.assertFalse(
            any(
                g["owner"] == "algorithm"
                and g["confidence"] == "confirmed"
                and g["rootCause"]
                for g in groups
            )
        )

    def test_valid_direct_witness_inside_actual_search_proves_miss(self):
        self.record["straightLineDiagnostic"].update(status="valid")
        self.assertEqual(
            "confirmed_missed_route",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )

    def test_valid_witness_outside_search_or_beyond_length_limit_is_not_a_miss(self):
        self.record["straightLineDiagnostic"]["status"] = "valid"
        for mutate in ("bounds", "length"):
            record, scenario = copy.deepcopy(self.record), copy.deepcopy(self.scenario)
            if mutate == "bounds":
                record["diagnostics"]["events"][0]["fields"]["East"] = 27.001
            else:
                scenario["routingOptions"]["MaxRouteLengthMetres"] = 1
            self.assertNotEqual(
                "confirmed_missed_route",
                self.analysis.classify(record, scenario)["outcome"],
            )

    def test_unknown_success_audit_remains_indeterminate(self):
        self.record.update(
            status="success",
            audits=[{"status": "unknown"}],
            auditStatus="unknown",
            failureReason="route_audit_not_valid",
        )
        self.assertEqual(
            "indeterminate",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )

    def test_all_success_alternatives_are_checked_and_violation_takes_precedence(self):
        self.record.update(status="success", audits=[{"status": "valid"}])
        self.assertEqual(
            "valid_route", self.analysis.classify(self.record, self.scenario)["outcome"]
        )
        self.record["audits"].append({"status": "violation"})
        self.assertEqual(
            "invalid_route",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )

    def test_resource_timeout_and_process_crash_are_distinct(self):
        self.record["process"]["timeout"] = True
        self.assertEqual(
            "resource_limit",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )
        self.record["process"].update(timeout=False, returncode=-9)
        self.assertEqual(
            "technical_error",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )
        self.record.update(status="resource_limit", error={"reason": "ResourceLimit"})
        self.record["process"]["returncode"] = 0
        self.assertEqual(
            "resource_limit",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )

    def test_resource_status_cannot_conceal_corrupt_evidence(self):
        self.record.update(
            status="resource_limit",
            error={"reason": "ResourceLimit"},
            failureReason="evidence_error",
        )
        self.assertEqual(
            "technical_error",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )
        self.record["failureReason"] = "inconsistent_result"
        self.assertEqual(
            "resource_limit",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )

    def test_proven_missed_path_takes_precedence_over_router_fallback_limit(self):
        self.record["straightLineDiagnostic"]["status"] = "valid"
        self.record["diagnostics"]["events"].append(
            {"eventId": 9, "fields": {"ResourceLimited": True}}
        )
        result = self.analysis.classify(self.record, self.scenario)
        self.assertEqual("confirmed_missed_route", result["outcome"])
        groups = self.analysis.causes(self.record, self.scenario, result)
        self.assertTrue(
            any(
                group["owner"] == "resource"
                and group["code"] == "router_surface_budget_exhausted"
                for group in groups
            )
        )

    def test_blocked_input_requires_matching_independent_point_proof(self):
        self.record.update(
            status="invalid_input",
            error={"reason": "BlockedTerrain", "terrainKind": "Building"},
            failureReason="inconsistent_result",
        )
        self.assertEqual(
            "technical_error",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )
        self.record["inputRefusalProof"] = {
            "reason": "BlockedTerrain",
            "terrainKind": "Building",
            "kind": "blocked_endpoint",
        }
        result = self.analysis.classify(self.record, self.scenario)
        self.assertEqual(
            ("justified_refusal", "blocked_endpoint"),
            (result["outcome"], result["proof"]),
        )
        self.record["inputRefusalProof"]["terrainKind"] = "Marsh"
        self.assertEqual(
            "technical_error",
            self.analysis.classify(self.record, self.scenario)["outcome"],
        )

    def test_length_lower_bound_can_justify_refusal(self):
        self.scenario["routingOptions"]["MaxRouteLengthMetres"] = 10
        result = self.analysis.classify(self.record, self.scenario)
        self.assertEqual("justified_refusal", result["outcome"])
        self.assertEqual("geodesic_lower_bound", result["proof"])

    def test_incomplete_or_malformed_evidence_is_technical(self):
        for failure in (
            "missing_result",
            "incomplete_diagnostics",
            "evidence_error",
            "inconsistent_result",
        ):
            with self.subTest(failure=failure):
                self.record["failureReason"] = failure
                self.assertEqual(
                    "technical_error",
                    self.analysis.classify(self.record, self.scenario)["outcome"],
                )

    def test_multiple_barrier_groups_are_observations_not_proven_failure_causes(self):
        self.record["straightLineDiagnostic"]["findings"] += [
            {"reason": "water_without_opening"},
            {"reason": "blocked_terrain"},
        ]
        groups = self.analysis.causes(
            self.record, self.scenario, {"outcome": "indeterminate"}
        )
        codes = {g["code"] for g in groups}
        self.assertTrue(
            {
                "direct_road_barrier",
                "direct_water_barrier",
                "direct_blocked_terrain",
            }.issubset(codes)
        )
        self.assertTrue(
            all(not g["rootCause"] for g in groups if g["scope"] == "straight_line")
        )

    def test_railway_is_allowed_and_restricted_area_is_only_model_limitation(self):
        groups = self.analysis.causes(
            self.record,
            self.scenario,
            {"outcome": "indeterminate"},
            categories=["railway", "restricted"],
        )
        self.assertFalse(any("rail" in g["code"] for g in groups))
        restricted = next(
            g for g in groups if g["code"] == "restricted_area_unmodelled"
        )
        self.assertEqual(
            ("model", "confirmed", False),
            (restricted["owner"], restricted["confidence"], restricted["rootCause"]),
        )

    def test_holdout_has_aggregate_counts_but_no_development_representatives(self):
        records = [
            self.compact("dev", "development"),
            self.compact("secret", "holdout"),
        ]
        report = self.analysis.aggregate(records)
        self.assertEqual(2, report["scenarioCount"])
        self.assertEqual(1, report["bySplit"]["holdout"]["indeterminate"])
        self.assertEqual(["dev"], report["groups"][0]["representatives"])
        self.assertEqual(1, report["groups"][0]["developmentCount"])
        self.assertEqual(1, report["groups"][0]["holdoutCount"])

    def compact(self, case_id, split):
        return {
            "id": case_id,
            "split": split,
            "region": "minsk",
            "categories": ["road"],
            "distanceClass": "local",
            "complexity": "single",
            "outcome": "indeterminate",
            "wallMilliseconds": 10,
            "peakRssBytes": 100,
            "causes": [
                {
                    "code": "direct_road_barrier",
                    "owner": "model",
                    "confidence": "confirmed",
                    "rootCause": False,
                    "scope": "straight_line",
                }
            ],
            "alternatives": [],
            "fallback": {"meshAttempts": 1, "surfaceAttempts": 0, "surfaceFound": 0},
        }

    def test_missing_memory_is_null_not_zero_and_p95_is_interpolated(self):
        records = [
            self.compact("one", "development"),
            self.compact("two", "development"),
        ]
        records[0]["wallMilliseconds"] = 100
        records[1].update(wallMilliseconds=200, peakRssBytes=None)
        report = self.analysis.aggregate(records)
        self.assertEqual(195, report["performance"]["wallP95Milliseconds"])
        self.assertEqual(1, report["performance"]["memoryMeasuredCount"])
        self.assertEqual(1, report["performance"]["memoryUnavailableCount"])

    def test_duplicate_inventory_is_rejected(self):
        with self.assertRaises(ValueError):
            self.analysis.aggregate([self.compact("same", "development")] * 2)

    def test_markdown_keeps_quality_failure_visible_when_evidence_gate_passes(self):
        report = {
            "gatePassed": True,
            "qualityGatePassed": False,
            "summary": self.analysis.aggregate([]),
        }
        rendered = self.analysis.markdown(report)
        self.assertIn("Evidence gate: **PASS**", rendered)
        self.assertIn("Quality gate: **FAIL**", rendered)

    def test_comparison_marks_cross_platform_timing_unverified(self):
        provenance = {
            "corpusSha256": "a",
            "databaseSha256": "b",
            "workers": 2,
            "timeoutSeconds": 120,
            "managedHeapBytes": 1,
            "probeTimeoutSeconds": 30,
            "evaluateHoldout": False,
            "effectiveOptionsSha256": "o",
            "assessmentSha256": "assessment",
            "platform": "linux",
            "processor": "cpu",
            "logicalCpuCount": 2,
        }
        head = {
            "provenance": provenance,
            "records": [self.compact("one", "development")],
        }
        baseline = copy.deepcopy(head)
        baseline["records"][0]["wallMilliseconds"] = 20
        comparison = self.analysis.compare(head, baseline)
        self.assertTrue(comparison["performanceComparable"])
        self.assertEqual(0.5, comparison["wallP95Ratio"])
        baseline["provenance"]["platform"] = "windows"
        comparison = self.analysis.compare(head, baseline)
        self.assertFalse(comparison["performanceComparable"])
        self.assertIsNone(comparison["wallP95Ratio"])

    def test_comparison_rejects_different_inputs_or_execution_budgets(self):
        provenance = {
            "corpusSha256": "a",
            "databaseSha256": "b",
            "workers": 2,
            "timeoutSeconds": 120,
            "managedHeapBytes": 1536,
            "probeTimeoutSeconds": 30,
            "evaluateHoldout": False,
            "effectiveOptionsSha256": "options",
            "assessmentSha256": "assessment",
        }
        head = {
            "provenance": provenance,
            "records": [self.compact("one", "development")],
        }
        baseline = copy.deepcopy(head)
        for field in provenance:
            other = copy.deepcopy(baseline)
            other["provenance"][field] = "changed"
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.analysis.compare(head, other)
        comparison = self.analysis.compare(head, baseline)
        self.assertEqual(0, comparison["lostValidRoutes"])
