"""Real Python producers, the shipped C# SQLite reader and Android unpacker."""

import hashlib
import json
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from contextlib import closing
from pathlib import Path
from typing import get_args

from shapely.geometry import box, mapping

from tools.GeodataPreparation.terrain_kinds import TerrainKind
from tools.split_android_map import split_package

ROOT = Path(__file__).resolve().parents[3]
VERIFY_PROJECT = ROOT / "tools/CableRoutePlanner.Verify"
VERIFY = VERIFY_PROJECT / "bin/Release/net8.0/CableRoutePlanner.Verify.dll"


def run(*args):
    result = subprocess.run(args, cwd=ROOT, capture_output=True, timeout=120)
    if result.returncode:
        raise AssertionError(
            f"Command failed: {args!r}\n{result.stdout}\n{result.stderr}"
        )
    return result


def write_osm(path):
    osm = ET.Element("osm", version="0.6", generator="cross-language-contract-test")
    ET.SubElement(
        osm, "bounds", minlon="27", minlat="54", maxlon="27.01", maxlat="54.01"
    )
    next_node = 1
    next_way = 1

    def node(x, y, tags=None):
        nonlocal next_node
        ident = next_node
        next_node += 1
        item = ET.SubElement(
            osm,
            "node",
            id=str(ident),
            lon=str(27 + x / 100000),
            lat=str(54 + y / 100000),
            version="1",
        )
        for key, value in (tags or {}).items():
            ET.SubElement(item, "tag", k=key, v=value)
        return ident

    def way(points, tags):
        nonlocal next_way
        item = ET.SubElement(osm, "way", id=str(next_way), version="1")
        next_way += 1
        for ident in points:
            ET.SubElement(item, "nd", ref=str(ident))
        for key, value in tags.items():
            ET.SubElement(item, "tag", k=key, v=value)

    def line(points, tags):
        way([node(*point) for point in points], tags)

    line([(100, 10), (100, 900)], {"highway": "primary"})
    line([(50, 100), (150, 100)], {"highway": "primary", "bridge": "yes"})
    line(
        [(50, 200), (150, 200)],
        {"man_made": "pipeline", "substance": "gas", "location": "overhead"},
    )
    entrances = [node(50, 300), node(150, 300)]
    way(entrances, {"highway": "footway", "tunnel": "yes", "layer": "-1"})
    way([node(30, 300), entrances[0]], {"highway": "footway"})
    way([entrances[1], node(170, 300)], {"highway": "footway"})
    for offset, extra in enumerate(({}, {"bridge": "yes"}, {"tunnel": "yes"})):
        line(
            [(400, 100 + offset * 50), (500, 100 + offset * 50)],
            {"railway": "rail", **extra},
        )
    line([(400, 300), (500, 300)], {"man_made": "cutline"})
    line([(400, 350), (500, 350)], {"waterway": "stream"})
    for offset, tags in enumerate(
        (
            {"natural": "wetland"},
            {"natural": "water"},
            {"natural": "wood"},
            {"landuse": "farmland"},
            {"building": "yes"},
        )
    ):
        x = 300 + offset * 100
        points = [node(x, 500), node(x + 20, 500), node(x + 20, 520), node(x, 520)]
        way([*points, points[0]], tags)
    node(700, 700, {"place": "town", "name": "Contract town"})
    ET.ElementTree(osm).write(path, encoding="utf-8", xml_declaration=True)


class CrossLanguageContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("dotnet"):
            raise RuntimeError("dotnet is required for Python/C# contract tests")
        run(
            "dotnet",
            "build",
            str(VERIFY_PROJECT),
            "-c",
            "Release",
            "-p:RestoreLockedMode=true",
        )

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.folder = Path(self.directory.name)
        self.osm = self.folder / "source.osm"
        write_osm(self.osm)
        self.package = self.folder / "v3"
        run(
            sys.executable,
            str(ROOT / "tools/GeodataPreparation/prepare_country.py"),
            "--pbf",
            str(self.osm),
            "--expected-sha256",
            hashlib.sha256(self.osm.read_bytes()).hexdigest(),
            "--output",
            str(self.package),
            "--compress",
        )

    def inspect(self, package):
        output = self.folder / "inspection.json"
        run("dotnet", str(VERIFY), "--inspect-features", str(package), str(output))
        return json.loads(output.read_text(encoding="utf-8"))

    def test_python_v3_reader_preserves_kinds_road_links_and_source_topology(self):
        loaded = self.inspect(self.package)
        self.assertEqual(loaded["schemaVersion"], 3)
        self.assertEqual(
            loaded["attribution"], "© OpenStreetMap contributors · ODbL 1.0 · Geofabrik"
        )
        features = loaded["features"]
        road = next(feature for feature in features if feature["kind"] == "Road")
        tunnel = next(
            feature
            for feature in features
            if feature["kind"] == "PedestrianUnderpassCandidate"
        )
        self.assertEqual(tunnel["underpassRoadIds"], [road["id"]])
        self.assertEqual(tunnel["underpassTopology"]["networkId"], tunnel["id"])
        self.assertEqual(len(tunnel["underpassTopology"]["nodeIds"]), 2)
        self.assertEqual(
            tunnel["underpassTopology"]["entranceNodeIds"],
            tunnel["underpassTopology"]["nodeIds"],
        )
        with closing(sqlite3.connect(self.package / "geodata.sqlite")) as connection:
            self.assertEqual(
                connection.execute("PRAGMA foreign_key_check").fetchall(), []
            )
            for table, columns in {
                "features": {"id", "kind", "wkb", "name", "approval_id", "source_id"},
                "underpass_roads": {
                    "candidate_id",
                    "road_id",
                    "tunnel_osm_id",
                    "road_osm_id",
                },
                "underpass_topology": {
                    "candidate_id",
                    "network_id",
                    "node_ids",
                    "entrance_node_ids",
                },
                "feature_index": {"id", "minx", "maxx", "miny", "maxy"},
            }.items():
                self.assertEqual(
                    {
                        row[1]
                        for row in connection.execute(f"PRAGMA table_info({table})")
                    },
                    columns,
                )
        self.assertTrue(all(feature["wkbBytes"] > 0 for feature in features))

    def test_exact_producer_kind_set_matches_authoritative_domain_and_legacy_readers(
        self,
    ):
        v3 = self.inspect(self.package)
        legacy = self.folder / "v1"
        crossings = self.folder / "crossings.json"
        crossings.write_text(
            json.dumps(
                {
                    "features": [
                        {
                            "properties": {"approval_id": "contract-review"},
                            "geometry": mapping(box(27.008, 54.008, 27.009, 54.009)),
                        }
                    ]
                }
            )
        )
        run(
            sys.executable,
            str(ROOT / "tools/GeodataPreparation/prepare.py"),
            "--osm",
            str(self.osm),
            "--expected-sha256",
            hashlib.sha256(self.osm.read_bytes()).hexdigest(),
            "--bounds",
            "27",
            "54",
            "27.01",
            "54.01",
            "--output",
            str(legacy),
            "--crossings",
            str(crossings),
        )
        v1 = self.inspect(legacy)
        produced = {feature["kind"] for feature in v3["features"] + v1["features"]}
        self.assertEqual(produced, set(v3["terrainKinds"]))
        self.assertEqual(set(get_args(TerrainKind)), produced)
        self.assertEqual(v1["schemaVersion"], 1)
        self.assertTrue(
            all(feature["underpassRoadIds"] is None for feature in v1["features"])
        )
        manifest_path = legacy / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["schema_version"] = 2
        manifest_path.write_text(json.dumps(manifest))
        v2 = self.inspect(legacy)
        self.assertEqual(v2["schemaVersion"], 2)
        self.assertEqual(v2["features"], v1["features"])

    def test_python_android_chunks_install_through_runtime_unpacker_and_reader(self):
        assets = self.folder / "assets"
        split_package(self.package, assets, chunk_bytes=1024)
        self.assertGreater(len(list(assets.glob("geodata.*.gz"))), 1)
        installed = self.folder / "installed"
        run("dotnet", str(VERIFY), "--install-assets", str(assets), str(installed))
        self.assertEqual(
            (installed / "geodata.sqlite").read_bytes(),
            (self.package / "geodata.sqlite").read_bytes(),
        )
        self.assertEqual(self.inspect(installed), self.inspect(self.package))
        self.assertFalse((installed / "geodata.sqlite.tmp").exists())

    def test_v3_missing_required_links_table_is_rejected_by_csharp_reader(self):
        database = self.package / "geodata.sqlite"
        with closing(sqlite3.connect(database)) as connection:
            connection.execute("DROP TABLE underpass_roads")
            connection.commit()
        manifest_path = self.package / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["database_sha256"] = hashlib.sha256(database.read_bytes()).hexdigest()
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        output = self.folder / "invalid-inspection.json"
        result = subprocess.run(
            [
                "dotnet",
                str(VERIFY),
                "--inspect-features",
                str(self.package),
                str(output),
            ],
            capture_output=True,
            timeout=30,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(output.exists())

    def test_missing_android_chunk_is_not_read_as_a_complete_package(self):
        assets = self.folder / "assets"
        split_package(self.package, assets, chunk_bytes=1024)
        chunks = sorted(assets.glob("geodata.*.gz"))
        missing = chunks[len(chunks) // 2]
        missing_bytes = missing.read_bytes()
        missing.unlink()
        installed = self.folder / "installed"
        shutil.copytree(self.package, installed)
        original = self.inspect(installed)
        previous_database = (installed / "geodata.sqlite").read_bytes()
        previous_manifest = (installed / "manifest.json").read_bytes()
        manifest_path = assets / "manifest.json"
        updated_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        updated_manifest["attribution"] = "Updated contract package"
        manifest_path.write_text(json.dumps(updated_manifest), encoding="utf-8")
        result = subprocess.run(
            ["dotnet", str(VERIFY), "--install-assets", str(assets), str(installed)],
            capture_output=True,
            timeout=30,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((installed / "geodata.sqlite").read_bytes(), previous_database)
        self.assertEqual((installed / "manifest.json").read_bytes(), previous_manifest)
        self.assertEqual(self.inspect(installed), original)
        self.assertEqual(list(installed.glob("*.tmp")), [])
        self.assertEqual(list(installed.glob(".geodata-install-*")), [])

        missing.write_bytes(missing_bytes)
        run("dotnet", str(VERIFY), "--install-assets", str(assets), str(installed))
        self.assertEqual(
            self.inspect(installed),
            original | {"attribution": "Updated contract package"},
        )
        self.assertEqual(
            json.loads((installed / "manifest.json").read_text(encoding="utf-8")),
            updated_manifest,
        )
