import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from shapely.geometry import LineString, mapping

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
import routing_crossing_pilot as pilot
import routing_mass_experiment as mass
import routing_mass_report as report


class RoutingModeContractTests(unittest.TestCase):
    def test_current_options_explicitly_enable_underpasses(self):
        self.assertIn("AllowExperimentalPedestrianUnderpasses", pilot.OPTIONS)
        self.assertIs(pilot.OPTIONS["AllowExperimentalPedestrianUnderpasses"], True)

    def test_historical_and_current_scenarios_keep_frozen_input_unchanged(self):
        self.assertTrue(hasattr(pilot, "scenario_for_mode"))
        snapshot = {"id": "frozen", "routingOptions": {"CellSizeMetres": 50}}
        original = copy.deepcopy(snapshot)
        historical = pilot.scenario_for_mode(snapshot, "historical")
        current = pilot.scenario_for_mode(snapshot, "current")
        self.assertEqual(snapshot, original)
        self.assertEqual(
            historical["routingOptions"],
            {
                "CellSizeMetres": 50,
                "MaxNodes": 100000,
                "ReserveFraction": 0.1,
                "PreferOverheadGasCrossings": True,
                "MaxRouteLengthMetres": 1000000,
                "AllowExperimentalPedestrianUnderpasses": False,
            },
        )
        self.assertEqual(
            current["routingOptions"],
            historical["routingOptions"]
            | {
                "AllowExperimentalPedestrianUnderpasses": True,
            },
        )
        self.assertEqual(current["routingMode"], "current")
        self.assertEqual(historical["routingMode"], "historical")

    def test_explicit_boolean_overrides_are_preserved(self):
        case = {
            "routingOptions": {
                "AllowExperimentalPedestrianUnderpasses": False,
                "PreferOverheadGasCrossings": False,
            }
        }
        effective = pilot.scenario_for_mode(case, "current")["routingOptions"]
        self.assertIs(effective["AllowExperimentalPedestrianUnderpasses"], False)
        self.assertIs(effective["PreferOverheadGasCrossings"], False)

    def test_invalid_json_option_types_are_rejected(self):
        for key, value in (
            ("MaxNodes", True),
            ("MaxNodes", 100.5),
            ("CellSizeMetres", "25"),
            ("ReserveFraction", True),
            ("AllowExperimentalPedestrianUnderpasses", "false"),
        ):
            with self.subTest(field=key), self.assertRaises(TypeError):
                pilot.scenario_for_mode({"routingOptions": {key: value}}, "current")

    def test_saved_json_has_utf8_and_platform_independent_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "contract.json"
            pilot.save_json(path, {"name": "Пример ≈"})
            self.assertEqual(path.read_bytes(), '{\n  "name": "Пример ≈"\n}\n'.encode())

    def test_underpass_links_survive_saved_geojson(self):
        self.assertTrue(hasattr(pilot, "audit_features"))
        raw = [
            {
                "type": "Feature",
                "properties": {
                    "featureId": 7,
                    "kind": "PedestrianUnderpassCandidate",
                    "underpassRoadIds": [3, 4],
                },
                "geometry": mapping(LineString([(-5, 0), (5, 0)])),
            }
        ]
        feature = pilot.audit_features(raw)[0]
        self.assertEqual(feature["underpass_road_ids"], [3, 4])
        self.assertEqual(list(feature["geometry"].coords), [(-5, 0), (5, 0)])


class UnderpassHarnessContractTests(unittest.TestCase):
    def scenario_and_result(self):
        case = {
            "id": "underpass",
            "routingOptions": {
                "AllowExperimentalPedestrianUnderpasses": True,
            },
        }
        finding = {
            "reason": "road_contact",
            "roadId": 1,
            "allowed": True,
            "coordinate": [0.0, 0.0],
            "bridgeIds": [],
            "gasIds": [],
            "underpassIds": [2],
        }
        result = {
            "id": case["id"],
            "status": "success",
            "alternatives": [
                {
                    "name": "short",
                    "geometry": [
                        {"Latitude": 0, "Longitude": -1},
                        {"Latitude": 0, "Longitude": 1},
                    ],
                }
            ],
            "audit": {
                "status": "valid",
                "alternatives": [
                    {"status": "valid", "findings": [finding]},
                ],
            },
        }
        return case, result, finding

    def test_enabled_underpass_evidence_passes_integrity(self):
        case, result, _ = self.scenario_and_result()
        self.assertTrue(pilot.harness_integrity([case], [result]))

    def test_disabled_underpass_evidence_does_not_pass_integrity(self):
        case, result, _ = self.scenario_and_result()
        case["routingOptions"]["AllowExperimentalPedestrianUnderpasses"] = False
        self.assertFalse(pilot.harness_integrity([case], [result]))

    def test_malformed_underpass_ids_cannot_certify_crossing(self):
        for ids in (["2"], [True], [None]):
            with self.subTest(ids=repr(ids)):
                case, result, finding = self.scenario_and_result()
                finding["bridgeIds"] = [3]
                finding["underpassIds"] = ids
                self.assertFalse(pilot.harness_integrity([case], [result]))

    def test_missing_underpass_link_is_unknown(self):
        route = LineString([(-10, 0), (10, 0)])
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -10), (0, 10)])}
        span = {
            "id": 2,
            "kind": "PedestrianUnderpassCandidate",
            "geometry": LineString([(-5, 0), (5, 0)]),
        }
        checks = pilot.audit(route, [road, span], allow_underpasses=True)
        self.assertEqual(checks["status"], "unknown")
        self.assertIsNone(checks["findings"][0]["allowed"])
        self.assertEqual(checks["findings"][0]["missingUnderpassLinkIds"], [2])

    def test_unreadable_underpass_links_are_unknown(self):
        route = LineString([(-10, 0), (10, 0)])
        road = {"id": 1, "kind": "Road", "geometry": LineString([(0, -10), (0, 10)])}
        for links in (None, "1", [True], [1, "2"]):
            with self.subTest(evidence=repr(links)):
                span = {
                    "id": 2,
                    "kind": "PedestrianUnderpassCandidate",
                    "geometry": LineString([(-5, 0), (5, 0)]),
                    "underpass_road_ids": links,
                }
                self.assertEqual(
                    pilot.audit(route, [road, span], allow_underpasses=True)["status"],
                    "unknown",
                )


class SharedContactContractTests(unittest.TestCase):
    def test_common_metric_geometries_have_independent_audit_outcomes(self):
        path = (
            Path(__file__).resolve().parents[3]
            / "samples/routing-contact-contract.json"
        )
        contract = json.loads(path.read_text(encoding="utf-8"))
        for case in contract["cases"]:
            with self.subTest(case=case["id"]):
                features = [
                    {
                        "id": contract["roadId"],
                        "kind": "Road",
                        "geometry": LineString(case["road"]),
                    }
                ]
                if candidate := case.get("candidate"):
                    feature = {
                        "id": candidate["id"],
                        "kind": candidate["kind"],
                        "geometry": LineString(candidate["coordinates"]),
                    }
                    if "underpassRoadIds" in candidate:
                        feature["underpass_road_ids"] = candidate["underpassRoadIds"]
                    features.append(feature)
                checks = pilot.audit(
                    LineString(case["route"]),
                    features,
                    allow_underpasses=case["allowUnderpasses"],
                )
                self.assertEqual(checks["status"], case["auditStatus"])
                if case["auditStatus"] == "valid":
                    self.assertTrue(checks["findings"][0]["allowed"])
                    self.assertIn(
                        candidate["id"],
                        checks["findings"][0][
                            {
                                "BridgeCandidate": "bridgeIds",
                                "GasPipelineCrossingCandidate": "gasIds",
                                "PedestrianUnderpassCandidate": "underpassIds",
                            }[candidate["kind"]]
                        ],
                    )


class PilotRunnerIntegrationTests(unittest.TestCase):
    def test_runner_passes_explicit_options_and_preserved_links_to_independent_audit(
        self,
    ):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            folder = output / "underpass"
            folder.mkdir()
            verify = output / "verify.dll"
            verify.write_bytes(b"fixture-process")
            coordinates = [(27.4998, 53.9), (27.5002, 53.9)]
            case = {
                "id": "underpass",
                "category": "A",
                "quality": "exploratory",
                "points": [{"Longitude": x, "Latitude": y} for x, y in coordinates],
                "routingOptions": {},
                "distanceMetres": 26.2,
                "databaseSha256": "fixture",
                "auditBounds": [27.49, 53.89, 27.51, 53.91],
                "targetRoad": {"sourceId": "fixture/road", "featureId": 1},
                "candidateIds": [2],
            }
            frozen = json.dumps([case])
            (output / "scenarios.json").write_text(frozen, encoding="utf-8")
            (folder / "scenario.json").write_text(json.dumps(case), encoding="utf-8")
            (output / "generation.json").write_text(
                '{"rejected": {}}', encoding="utf-8"
            )
            input_features = [
                {
                    "type": "Feature",
                    "properties": {"featureId": 1, "kind": "Road"},
                    "geometry": mapping(LineString([(27.5, 53.8998), (27.5, 53.9002)])),
                },
                {
                    "type": "Feature",
                    "properties": {
                        "featureId": 2,
                        "kind": "PedestrianUnderpassCandidate",
                        "underpassRoadIds": [1],
                    },
                    "geometry": mapping(LineString([(27.4999, 53.9), (27.5001, 53.9)])),
                },
            ]
            (folder / "input.geojson").write_text(
                json.dumps({"features": input_features}), encoding="utf-8"
            )

            def verify_process(command, timeout, env):
                execution = json.loads(Path(command[-2]).read_text(encoding="utf-8"))
                self.assertEqual(len(execution["routingOptions"]), 6)
                self.assertNotEqual(Path(command[-2]), folder / "scenario.json")
                Path(command[-1]).write_text(
                    json.dumps(
                        {
                            "status": "success",
                            "alternatives": [
                                {
                                    "name": "short",
                                    "geometry": case["points"],
                                    "metrics": {},
                                }
                            ],
                        }
                    ),
                    encoding="utf-8",
                )
                return {
                    "timeout": False,
                    "returncode": 0,
                    "stdout": "Проверка ≈ ✅",
                    "stderr": "Диагностика ⚠",
                    "elapsed_wall_ms": 1,
                }

            (output / "samples").mkdir()
            (output / "samples/routing-crossing-mass-v1.json").write_text(
                frozen, encoding="utf-8"
            )
            for entry in (pilot, mass):
                for mode, expected in (
                    ("current", "valid"),
                    ("historical", "violation"),
                ):
                    with (
                        self.subTest(tool=entry.__name__, mode=mode),
                        patch.object(pilot, "run_bounded_command", verify_process),
                        patch.object(mass, "ROOT", output),
                        patch.object(
                            sys,
                            "argv",
                            [
                                entry.__name__,
                                "run",
                                "--data",
                                str(output),
                                "--output",
                                str(output),
                                "--verify-dll",
                                str(verify),
                                "--routing-mode",
                                mode,
                            ],
                        ),
                    ):
                        entry.main()
                        self.assertEqual(
                            (folder / "stdout.log").read_text(encoding="utf-8"),
                            "Проверка ≈ ✅",
                        )
                        self.assertEqual(
                            (folder / "stderr.log").read_text(encoding="utf-8"),
                            "Диагностика ⚠",
                        )
                        self.assertIn(
                            "≈", (folder / "map.svg").read_text(encoding="utf-8")
                        )
                        result = json.loads(
                            (folder / "result.json").read_text(encoding="utf-8")
                        )
                        self.assertEqual(result["audit"]["status"], expected)
                        self.assertEqual(result["routingMode"], mode)
                        self.assertIs(
                            result["routingOptions"][
                                "AllowExperimentalPedestrianUnderpasses"
                            ],
                            mode == "current",
                        )
                        self.assertEqual(
                            (output / "scenarios.json").read_text(encoding="utf-8"),
                            frozen,
                        )
                        self.assertEqual(
                            json.loads(
                                (folder / "scenario.json").read_text(encoding="utf-8")
                            ),
                            case,
                        )


class HistoricalReportContractTests(unittest.TestCase):
    def test_inventory_counts_are_reported_as_baseline(self):
        self.assertTrue(hasattr(report, "inventory_summary"))
        text = report.inventory_summary(
            {
                "preparedFeatureCounts": {
                    "BridgeCandidate": 9,
                    "OverheadGasPipeline": 4,
                    "GasPipelineCrossingCandidate": 2,
                    "PedestrianUnderpassCandidate": 6,
                }
            }
        )
        self.assertIn("9 BridgeCandidate", text)
        self.assertIn("4 OverheadGasPipeline", text)
        self.assertIn("2 GasPipelineCrossingCandidate", text)
        self.assertIn("6 PedestrianUnderpassCandidate", text)
        self.assertIn("исторического baseline", text)
        self.assertNotIn("отдельной поддержки тоннелей нет", text)

    def test_mass_report_uses_executed_underpass_options_and_labels_mode(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            run = root / "run"
            folder = run / "underpass"
            folder.mkdir(parents=True)
            (root / "samples").mkdir()
            (root / "data").mkdir()
            points = [
                {"Longitude": 27.4998, "Latitude": 53.9},
                {"Longitude": 27.5002, "Latitude": 53.9},
            ]
            case = {
                "id": "underpass",
                "points": points,
                "routingOptions": {},
                "databaseSha256": "database",
                "area": "fixture",
                "category": "A",
                "targetRoad": {"featureId": 1},
                "candidateIds": [2],
            }
            frozen = json.dumps([case])
            (run / "scenarios.json").write_text(frozen, encoding="utf-8")
            (root / "samples/routing-crossing-mass-v1.json").write_text(
                frozen, encoding="utf-8"
            )
            (root / "data/belarus-coverage.json").write_text(
                json.dumps(
                    [[27.4, 53.8], [27.6, 53.8], [27.6, 54], [27.4, 54], [27.4, 53.8]]
                ),
                encoding="utf-8",
            )
            line = LineString([(p["Longitude"], p["Latitude"]) for p in points])
            (folder / "witness.geojson").write_text(
                json.dumps({"features": [{"geometry": mapping(line)}]}),
                encoding="utf-8",
            )
            raw = [
                {
                    "properties": {"featureId": 1, "kind": "Road"},
                    "geometry": mapping(LineString([(27.5, 53.8998), (27.5, 53.9002)])),
                },
                {
                    "properties": {
                        "featureId": 2,
                        "kind": "PedestrianUnderpassCandidate",
                        "underpassRoadIds": [1],
                    },
                    "geometry": mapping(LineString([(27.4999, 53.9), (27.5001, 53.9)])),
                },
            ]
            (folder / "input.geojson").write_text(
                json.dumps({"features": raw}), encoding="utf-8"
            )
            inventory = root / "inventory.json"
            inventory.write_text(
                json.dumps(
                    {
                        "databaseSha256": "database",
                        "sourceSha256": "source",
                        "preparedFeatureCounts": {
                            "Road": 1,
                            "PedestrianUnderpassCandidate": 1,
                        },
                        "osmWays": {},
                        "importedByCategoryAndKind": {},
                    }
                ),
                encoding="utf-8",
            )
            for mode, expected in (("current", "valid"), ("historical", "violation")):
                with self.subTest(mode=mode):
                    options = pilot.scenario_for_mode(case, mode)["routingOptions"]
                    result = {
                        "id": "underpass",
                        "databaseSha256": "database",
                        "appCommit": "commit",
                        "verifyDllSha256": "dll",
                        "routingMode": mode,
                        "routingOptions": options,
                        "status": "no_route",
                        "alternatives": [],
                        "audit": {"status": "unknown", "alternatives": []},
                    }
                    (folder / "result.json").write_text(
                        json.dumps(result), encoding="utf-8"
                    )
                    output = root / mode
                    with patch.object(report, "ROOT", root):
                        report.summarize(run, inventory, output)
                    summary = json.loads(
                        (output / "results.json").read_text(encoding="utf-8")
                    )
                    self.assertEqual(summary["results"][0]["witnessStatus"], expected)
                    self.assertEqual(summary["routingMode"], mode)
                    self.assertEqual(summary["results"][0]["routingOptions"], options)
                    self.assertEqual(summary["results"][0]["routingMode"], mode)
                    text = (output / "report.md").read_text(encoding="utf-8")
                    self.assertIn(f"--routing-mode {mode}", text)
                    self.assertIn(f"routingMode={mode}", text)
                    if mode == "current":
                        self.assertNotIn("21 неуспеха", text)
                        self.assertNotIn("16 неуспехов", text)
                        self.assertNotIn("production-код не менялся", text)
                    self.assertEqual(
                        (run / "scenarios.json").read_text(encoding="utf-8"), frozen
                    )

            second = case | {"id": "second"}
            second_folder = run / second["id"]
            second_folder.mkdir()
            second_result = result | {"id": second["id"], "routingMode": "current"}
            (second_folder / "result.json").write_text(
                json.dumps(second_result), encoding="utf-8"
            )
            (second_folder / "input.geojson").write_text(
                json.dumps({"features": raw}), encoding="utf-8"
            )
            two_cases = json.dumps([case, second])
            (run / "scenarios.json").write_text(two_cases, encoding="utf-8")
            (root / "samples/routing-crossing-mass-v1.json").write_text(
                two_cases, encoding="utf-8"
            )
            with patch.object(report, "ROOT", root), self.assertRaises(ValueError):
                report.summarize(run, inventory, root / "mixed")
            self.assertFalse((root / "mixed/results.json").exists())


if __name__ == "__main__":
    unittest.main()
