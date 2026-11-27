"""Outcome-blind corpus sampling and frozen manifest contracts."""

import copy
import importlib
import json
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from shapely import to_wkb
from shapely.geometry import LineString, Point, Polygon, box

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))

corpus = importlib.import_module("routing_research_corpus")


class RoutingResearchCorpusTests(unittest.TestCase):
    def test_categories_require_osm_tags_and_do_not_prohibit_railways(self):
        examples = [
            ({"landuse": "forest"}, "forest"),
            ({"landuse": "farmland"}, "field"),
            ({"natural": "grassland"}, "countryside"),
            ({"highway": "trunk"}, "road-major"),
            ({"highway": "secondary"}, "road-secondary"),
            ({"highway": "residential"}, "road-local"),
            ({"junction": "roundabout"}, "roundabout"),
            ({"highway": "motorway_link"}, "interchange"),
            ({"railway": "rail"}, "railway"),
            ({"railway": "station"}, "station"),
            ({"highway": "footway", "tunnel": "yes"}, "underpass"),
            ({"bridge": "yes", "highway": "primary"}, "bridge"),
            ({"waterway": "river"}, "water"),
            ({"natural": "wetland"}, "marsh"),
            ({"access": "private"}, "restricted"),
        ]
        for tags, expected in examples:
            with self.subTest(expected=expected):
                self.assertIn(expected, corpus.categories_for_tags(tags))
        self.assertEqual([], corpus.categories_for_tags({"name": "forest station"}))
        self.assertNotIn("underpass", corpus.categories_for_tags({"tunnel": "no"}))
        self.assertNotIn("bridge", corpus.categories_for_tags({"bridge": "no"}))
        self.assertNotIn("restricted", corpus.categories_for_tags({"military": "no"}))
        self.assertNotIn(
            "interchange", corpus.categories_for_tags({"highway": "tertiary_link"})
        )

    def test_final_station_and_underpass_labels_reject_yards_and_building_passages(
        self,
    ):
        self.assertNotIn("station", corpus.semantic_categories({"railway": "yard"}))
        for railway in ("station", "halt"):
            self.assertIn("station", corpus.semantic_categories({"railway": railway}))
        for tunnel in ("building_passage", "culvert", "no"):
            self.assertNotIn(
                "underpass",
                corpus.semantic_categories({"highway": "footway", "tunnel": tunnel}),
            )
        self.assertIn(
            "underpass",
            corpus.semantic_categories({"highway": "footway", "tunnel": "yes"}),
        )
        self.assertIn(
            "underpass",
            corpus.semantic_categories(
                {
                    "highway": "footway",
                    "tunnel": "building_passage",
                    "location": "underground",
                }
            ),
        )

    def test_region_uses_polygon_including_holes_and_full_corridor(self):
        regions = [{"region": "BY-HM", "geometry": box(27, 53, 28, 54)}]
        self.assertEqual(
            "BY-HM",
            corpus.containing_region(regions, LineString([(27.1, 53.1), (27.2, 53.2)]))[
                "region"
            ],
        )
        self.assertIsNone(
            corpus.containing_region(regions, LineString([(27.1, 53.1), (28.1, 53.2)]))
        )
        regions[0]["geometry"] = regions[0]["geometry"].difference(
            box(27.4, 53.4, 27.6, 53.6)
        )
        self.assertIsNone(
            corpus.containing_region(regions, LineString([(27.3, 53.5), (27.7, 53.5)]))
        )

    def test_selection_is_order_independent_and_rejects_shared_targets_and_space(self):
        candidates = [
            {
                "sourceId": f"way/{number}",
                "region": "BY-HM",
                "categories": ["forest"],
                "line": LineString([(number * 10000, 0), (number * 10000 + 300, 0)]),
            }
            for number in range(1, 12)
        ]
        candidates += [
            dict(candidates[0]),
            dict(
                candidates[1],
                sourceId="way/near",
                line=LineString([(20001, 0), (20301, 0)]),
            ),
        ]
        quota = {"BY-HM": {"development": 4, "holdout": 2}}
        selected, _ = corpus.select_candidates(candidates, [], quota, seed=12)
        reverse, _ = corpus.select_candidates(
            list(reversed(candidates)), [], quota, seed=12
        )
        self.assertEqual(
            [(c["sourceId"], c["split"]) for c in selected],
            [(c["sourceId"], c["split"]) for c in reverse],
        )
        self.assertEqual(6, len(selected))
        self.assertEqual(6, len({c["sourceId"] for c in selected}))
        for index, first in enumerate(selected):
            for second in selected[index + 1 :]:
                minimum = (
                    1000
                    if "holdout" in (first["split"], second["split"])
                    and first["split"] != second["split"]
                    else 250
                )
                self.assertGreaterEqual(first["line"].distance(second["line"]), minimum)

    def test_unavailable_quota_fails_with_structured_shortfall(self):
        with self.assertRaises(corpus.QuotaShortfall) as failure:
            corpus.select_candidates(
                [], [], {"BY-HM": {"development": 1, "holdout": 1}}, seed=12
            )
        self.assertEqual(
            {"BY-HM/development": 1, "BY-HM/holdout": 1}, failure.exception.shortfalls
        )

    def test_legacy_crossing_is_rejected_even_with_distant_endpoints(self):
        candidates = [
            {
                "sourceId": "way/crossing",
                "region": "BY-HM",
                "categories": ["forest"],
                "line": LineString([(-2000, 0), (2000, 0)]),
            }
        ]
        with self.assertRaises(corpus.QuotaShortfall):
            corpus.select_candidates(
                candidates,
                [LineString([(0, -2000), (0, 2000)])],
                {"BY-HM": {"development": 1, "holdout": 0}},
                seed=12,
            )

    def test_distance_diversity_survives_category_priority(self):
        candidates = [
            {
                "sourceId": f"way/{index}",
                "region": "BY-HM",
                "categories": ["forest"],
                "distanceClass": distance,
                "line": LineString([(index * 10000, 0), (index * 10000 + length, 0)]),
            }
            for index in range(12)
            for distance, length in corpus.DISTANCES.items()
        ]
        selected, _ = corpus.select_candidates(
            candidates, [], {"BY-HM": {"development": 10, "holdout": 0}}, seed=12
        )
        self.assertEqual(
            set(corpus.DISTANCES),
            {candidate["distanceClass"] for candidate in selected},
        )

    def test_density_and_narrow_gap_are_measured_from_neighbour_geometries(self):
        with closing(sqlite3.connect(":memory:")) as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
            )
            target = corpus.unproject(box(0, 0, 10, 10))
            for index in range(12):
                shape = corpus.unproject(box(15 + index * 9, 0, 20 + index * 9, 10))
                west, south, east, north = shape.bounds
                connection.execute(
                    "INSERT INTO features VALUES(?,?,?,?)",
                    (index, "Building", to_wkb(shape), f"way/{index}"),
                )
                connection.execute(
                    "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                    (index, west, east, south, north),
                )
            categories, center, evidence = corpus.building_context(
                connection, {"sourceId": "way/target", "geometry": target}
            )
        self.assertEqual(["dense-buildings", "narrow-gap"], categories)
        self.assertEqual(13, evidence["buildingCountWithin150Metres"])
        self.assertAlmostEqual(5, evidence["gapMetres"])
        self.assertEqual("way/0", evidence["gapNeighbourSourceId"])
        self.assertAlmostEqual(12.5, center.x)

    def test_underpass_follows_passage_and_road_case_crosses_road(self):
        raw = [
            {
                "sourceId": "way/1",
                "tags": {"highway": "footway", "tunnel": "yes"},
                "geometry": LineString([(27.499, 53.9), (27.501, 53.9)]),
            }
        ]
        regions = [
            {
                "region": "BY-HM",
                "sourceId": "relation/59195",
                "tags": {"admin_level": "4", "ISO3166-2": "BY-HM"},
                "geometry": box(27, 53, 28, 54),
            }
        ]
        with tempfile.TemporaryDirectory() as folder:
            database = Path(folder) / "geodata.sqlite"
            with closing(sqlite3.connect(database)) as connection:
                connection.executescript(
                    "CREATE TABLE features(id,kind,wkb,source_id,approval_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                )
                obstacle = box(27.4999, 53.8999, 27.5001, 53.9001)
                west, south, east, north = obstacle.bounds
                connection.execute(
                    "INSERT INTO features VALUES(1,'Building',?,'way/2',NULL)",
                    (to_wkb(obstacle),),
                )
                connection.execute(
                    "INSERT INTO feature_index VALUES(1,?,?,?,?)",
                    (west, east, south, north),
                )
                connection.commit()
            passages, _ = corpus.make_candidates(raw, regions, database)
            raw[0]["tags"] = {"highway": "primary"}
            roads, _ = corpus.make_candidates(raw, regions, database)
            excluded, rejected = corpus.make_candidates(
                raw, regions, database, coverage=box(27.4999, 53.8999, 27.5001, 53.9001)
            )
        self.assertEqual(3, len(passages))
        self.assertEqual(3, len(roads))
        self.assertEqual([], excluded)
        self.assertEqual(3, rejected["outside-package-endpoint"])
        for candidate in passages:
            self.assertAlmostEqual(
                candidate["line"].coords[0][1], candidate["line"].coords[1][1]
            )
        for candidate in roads:
            self.assertAlmostEqual(
                candidate["line"].coords[0][0], candidate["line"].coords[1][0]
            )

    def test_endpoint_screening_uses_only_placement_and_conservative_river_margin(self):
        with closing(sqlite3.connect(":memory:")) as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id,approval_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
            )
            geometry = corpus.unproject(box(0, 0, 10, 10))
            west, south, east, north = geometry.bounds
            for kind in ("Building", "Lake", "Marsh", "River"):
                with self.subTest(kind=kind):
                    connection.execute("DELETE FROM features")
                    connection.execute("DELETE FROM feature_index")
                    connection.execute(
                        "INSERT INTO features VALUES(1,?,?,?,NULL)",
                        (kind, to_wkb(geometry), "way/1"),
                    )
                    connection.execute(
                        "INSERT INTO feature_index VALUES(1,?,?,?,?)",
                        (west, east, south, north),
                    )
                    self.assertEqual(
                        kind,
                        corpus.endpoint_blocker(
                            connection, corpus.unproject(Point(5, 5))
                        ),
                    )
                    self.assertIsNone(
                        corpus.endpoint_blocker(
                            connection, corpus.unproject(Point(100, 5))
                        )
                    )
            connection.execute("DELETE FROM features")
            connection.execute("DELETE FROM feature_index")
            river = corpus.unproject(LineString([(0, 0), (10, 0)]))
            west, south, east, north = river.bounds
            connection.execute(
                "INSERT INTO features VALUES(1,'River',?,'way/river',NULL)",
                (to_wkb(river),),
            )
            connection.execute(
                "INSERT INTO feature_index VALUES(1,?,?,?,?)",
                (west, east, south, north),
            )
            for metres, expected in ((1.999, "River"), (2, "River"), (2.001, None)):
                self.assertEqual(
                    expected,
                    corpus.endpoint_blocker(
                        connection, corpus.unproject(Point(5, metres))
                    ),
                )

    def test_cache_protocol_rejects_stale_extractor_or_source(self):
        cache = {
            "sourceSha256": "a" * 64,
            "extractorProtocol": corpus.EXTRACTOR_PROTOCOL,
            "seed": corpus.SEED,
            "tagKeys": list(corpus.TAG_KEYS),
        }
        corpus.validate_candidate_cache(cache, "a" * 64)
        for field, value in (
            ("extractorProtocol", "older"),
            ("sourceSha256", "b" * 64),
            ("seed", 1),
            ("tagKeys", []),
        ):
            with self.subTest(field=field):
                broken = dict(cache, **{field: value})
                with self.assertRaises(ValueError):
                    corpus.validate_candidate_cache(broken, "a" * 64)

    def test_context_cache_preserves_geometry_and_rejects_changed_inputs(self):
        metadata = {
            "databaseSha256": "a" * 64,
            "coverageSha256": "b" * 64,
            "sourceCacheSha256": "c" * 64,
            "contextSignature": corpus.context_signature(),
        }
        candidates = [{"sourceId": "way/1", "line": LineString([(1, 2), (3, 4)])}]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "context.jsonl.gz"
            corpus.write_context_cache(
                path, candidates, metadata, {"outside-package-endpoint": 2}
            )
            loaded, rejected = corpus.load_context_cache(path, metadata)
            self.assertTrue(candidates[0]["line"].equals_exact(loaded[0]["line"], 0))
            self.assertEqual("way/1", loaded[0]["sourceId"])
            self.assertEqual({"outside-package-endpoint": 2}, rejected)
            with self.assertRaises(ValueError):
                corpus.load_context_cache(path, dict(metadata, databaseSha256="d" * 64))

    def test_multi_requires_distinct_obstacle_kinds_and_sources(self):
        with closing(sqlite3.connect(":memory:")) as connection:
            connection.executescript(
                "CREATE TABLE features(id,kind,wkb,source_id,approval_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
            )
            line = LineString([(-0.001, 0), (0.001, 0)])
            for index, kind in enumerate(("Field", "Forest")):
                geometry = box(-0.001, -0.001, 0.001, 0.001)
                connection.execute(
                    "INSERT INTO features VALUES(?,?,?,?,NULL)",
                    (index, kind, to_wkb(geometry), f"way/{index}"),
                )
                connection.execute(
                    "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                    (index, -0.001, 0.001, -0.001, 0.001),
                )
            self.assertFalse(
                corpus.obstacle_context(connection, line)["isMultiObstacle"]
            )
            for index, kind in enumerate(("Road", "River"), start=2):
                geometry = LineString([(0, -0.001), (0, 0.001)])
                connection.execute(
                    "INSERT INTO features VALUES(?,?,?,?,NULL)",
                    (index, kind, to_wkb(geometry), f"way/{index}"),
                )
                connection.execute(
                    "INSERT INTO feature_index VALUES(?,?,?,?,?)",
                    (index, 0, 0, -0.001, 0.001),
                )
            context = corpus.obstacle_context(connection, line)
            self.assertTrue(context["isMultiObstacle"])
            self.assertEqual(["River", "Road"], context["obstacleKinds"])
            self.assertEqual(2, context["distinctObstacleSourceCount"])
            self.assertEqual(
                {"River": ["way/3"], "Road": ["way/2"]},
                context["obstacleSourceExamples"],
            )

    def test_restricted_area_labels_do_not_count_private_roads_or_tag_combinations(
        self,
    ):
        regions = [
            {
                "region": "BY-HM",
                "sourceId": "relation/59195",
                "tags": {"admin_level": "4", "ISO3166-2": "BY-HM"},
                "geometry": box(27, 53, 28, 54),
            }
        ]
        with tempfile.TemporaryDirectory() as folder:
            database = Path(folder) / "geodata.sqlite"
            with closing(sqlite3.connect(database)) as connection:
                connection.executescript(
                    "CREATE TABLE features(id,kind,wkb,source_id,approval_id); CREATE TABLE feature_index(id,minx,maxx,miny,maxy);"
                )
            roads, _ = corpus.make_candidates(
                [
                    {
                        "sourceId": "way/1",
                        "tags": {"highway": "primary", "access": "private"},
                        "geometry": LineString([(27.499, 53.9), (27.501, 53.9)]),
                    }
                ],
                regions,
                database,
            )
            areas, _ = corpus.make_candidates(
                [
                    {
                        "sourceId": "way/2",
                        "tags": {
                            "landuse": "farmland",
                            "natural": "grassland",
                            "access": "private",
                        },
                        "geometry": box(27.499, 53.899, 27.501, 53.901),
                    }
                ],
                regions,
                database,
            )
        self.assertEqual(3, len(roads))
        self.assertEqual(3, len(areas))
        self.assertTrue(all("restricted" not in case["categories"] for case in roads))
        self.assertTrue(all("restricted" in case["categories"] for case in areas))
        self.assertTrue(all("multi" not in case["categories"] for case in areas))

    def test_frozen_manifest_has_complete_provenance_and_balanced_real_evidence(self):
        path = ROOT / "samples/routing-research-v1.json"
        manifest = json.loads(path.read_text(encoding="utf-8"))
        legacy = corpus.sha256_file(ROOT / "samples/routing-crossing-mass-v1.json")
        corpus.validate_manifest(manifest, legacy)
        self.assertEqual(212, len(manifest["cases"]))
        self.assertEqual(
            170, sum(case["split"] == "development" for case in manifest["cases"])
        )
        self.assertEqual(
            42, sum(case["split"] == "holdout" for case in manifest["cases"])
        )
        self.assertEqual(
            set(corpus.REGIONS), {case["region"] for case in manifest["cases"]}
        )
        self.assertEqual(
            {"local", "medium", "long"},
            {case["distanceClass"] for case in manifest["cases"]},
        )
        self.assertTrue(
            set(corpus.REQUIRED_CATEGORIES)
            <= {
                category
                for case in manifest["cases"]
                for category in case["categories"]
            }
        )
        receipt = json.loads(
            (ROOT / "docs/evidence/routing-research-v1/generation.json").read_text()
        )
        self.assertEqual(corpus.sha256_file(path), receipt["corpusSha256"])
        self.assertEqual(path.read_bytes(), corpus.canonical_bytes(manifest))

    def test_frozen_geographic_split_excludes_legacy_controls_and_unsupported_points(
        self,
    ):
        manifest = json.loads(
            (ROOT / "samples/routing-research-v1.json").read_text(encoding="utf-8")
        )
        historical = json.loads(
            (ROOT / "samples/routing-crossing-mass-v1.json").read_text(encoding="utf-8")
        )
        targets = {
            case["targetRoad"]["sourceId"]
            for case in historical
            if case.get("targetRoad")
        }
        for source in manifest["controlInputSources"]:
            path = ROOT / source["sourceFile"]
            self.assertEqual(source["sha256"], corpus.sha256_file(path))
            historical.append(json.loads(path.read_text(encoding="utf-8")))
        old_lines = [
            corpus.project(
                LineString(
                    [
                        (point["Longitude"], point["Latitude"])
                        for point in case["points"]
                    ]
                )
            )
            for case in historical
        ]
        coverage = Polygon(
            json.loads((ROOT / "data/belarus-coverage.json").read_text())
        )
        for case in manifest["cases"]:
            with self.subTest(case=case["id"]):
                self.assertNotIn(case["osmEvidence"][0]["sourceId"], targets)
                points = [
                    Point(point["Longitude"], point["Latitude"])
                    for point in case["scenario"]["points"]
                ]
                self.assertTrue(all(coverage.covers(point) for point in points))
                line = corpus.project(LineString(points))
                minimum = 1000 if case["split"] == "holdout" else 250
                self.assertTrue(
                    all(line.distance(old) >= minimum - 0.01 for old in old_lines)
                )

    def test_manifest_rejects_duplicate_ids_and_false_category_evidence(self):
        manifest = json.loads(
            (ROOT / "samples/routing-research-v1.json").read_text(encoding="utf-8")
        )
        broken = copy.deepcopy(manifest)
        broken["cases"][1]["id"] = broken["cases"][0]["id"]
        with self.assertRaises(ValueError):
            corpus.validate_manifest(broken)
        broken = copy.deepcopy(manifest)
        broken["cases"][0]["categories"] = ["invented-category"]
        with self.assertRaises(ValueError):
            corpus.validate_manifest(broken)

    def test_frozen_manifest_rejects_spatial_leakage(self):
        manifest = json.loads(
            (ROOT / "samples/routing-research-v1.json").read_text(encoding="utf-8")
        )
        broken = copy.deepcopy(manifest)
        first = broken["cases"][0]
        second = broken["cases"][1]
        second["scenario"]["points"] = first["scenario"]["points"]
        second["distanceClass"] = first["distanceClass"]
        with self.assertRaises(ValueError):
            corpus.validate_manifest(broken)

    def test_manifest_rejects_changed_provenance_and_duplicate_primary_targets(self):
        manifest = json.loads(
            (ROOT / "samples/routing-research-v1.json").read_text(encoding="utf-8")
        )
        with self.assertRaises(ValueError):
            corpus.validate_manifest(manifest, legacy_sha256="0" * 64)
        broken = copy.deepcopy(manifest)
        broken["cases"][1]["osmEvidence"] = broken["cases"][0]["osmEvidence"]
        with self.assertRaises(ValueError):
            corpus.validate_manifest(broken)

    def test_frozen_manifest_rejects_option_tampering(self):
        manifest = json.loads(
            (ROOT / "samples/routing-research-v1.json").read_text(encoding="utf-8")
        )
        broken = copy.deepcopy(manifest)
        broken["cases"][0]["scenario"]["routingOptions"]["CellSizeMetres"] = 100
        with self.assertRaises(ValueError):
            corpus.validate_manifest(broken)
