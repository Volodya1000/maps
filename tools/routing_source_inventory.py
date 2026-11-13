"""Count crossing-related OSM ways against the pinned prepared SQLite."""

import argparse
import hashlib
import json
import sqlite3
from collections import Counter
from pathlib import Path

import osmium


def categories(tags):
    highway = tags.get("highway")
    pedestrian = highway in ("footway", "path", "steps", "pedestrian", "cycleway")
    bridge = tags.get("bridge") not in (None, "no")
    tunnel = tags.get("tunnel") not in (None, "no")
    layer = tags.get("layer")
    pipeline = tags.get("man_made") == "pipeline"
    gas = tags.get("substance") == "gas"
    overhead = tags.get("location") == "overhead"
    if bridge and highway:
        yield "pedestrian_bridge" if pedestrian else "road_bridge"
    if bridge and tags.get("railway"):
        yield "railway_bridge"
    if tunnel and highway:
        yield "pedestrian_tunnel" if pedestrian else "road_tunnel"
    if tunnel and tags.get("railway"):
        yield "railway_tunnel"
    if layer not in (None, "0"):
        yield "nonzero_layer"
    if pipeline and gas:
        yield "gas_pipeline_overhead" if overhead else "gas_pipeline_other"
    if tags.get("man_made") == "bridge":
        yield "bridge_structure"
    if tags.get("covered") not in (None, "no") and highway:
        yield "covered_highway"
    if tags.get("ford") not in (None, "no"):
        yield "ford"


class Inventory(osmium.SimpleHandler):
    def __init__(self, imported):
        super().__init__()
        self.imported = imported
        self.counts = Counter()
        self.imported_counts = Counter()
        self.unsupported_examples = {}

    def way(self, way):
        source = f"way/{way.id}"
        kinds = self.imported.get(source, set())
        for category in categories(way.tags):
            self.counts[category] += 1
            if kinds:
                for kind in kinds:
                    self.imported_counts[f"{category}:{kind}"] += 1
            elif len(self.unsupported_examples.setdefault(category, [])) < 5:
                self.unsupported_examples[category].append(source)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pbf", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with args.pbf.open("rb") as stream:
        source_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    database = args.data / "geodata.sqlite"
    with database.open("rb") as stream:
        database_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    manifest = json.loads((args.data / "manifest.json").read_text())
    if source_hash != manifest["sources"][0]["sha256"]:
        raise ValueError("PBF differs from pinned source")
    if database_hash != manifest["database_sha256"]:
        raise ValueError("SQLite differs from pinned package")
    connection = sqlite3.connect(f"file:{database}?mode=ro", uri=True)
    imported = {}
    for source, kind in connection.execute(
        "SELECT source_id,kind FROM features WHERE kind IN "
        "('Road','BridgeCandidate','OverheadGasPipeline')"
    ):
        imported.setdefault(source, set()).add(kind)
    prepared_counts = dict(
        connection.execute(
            "SELECT kind,count(*) FROM features GROUP BY kind"
        ).fetchall()
    )
    connection.close()
    inventory = Inventory(imported)
    inventory.apply_file(str(args.pbf))
    result = {
        "sourceSha256": source_hash,
        "databaseSha256": database_hash,
        "scope": "all tagged OSM ways in pinned Belarus PBF",
        "osmWays": dict(inventory.counts),
        "importedByCategoryAndKind": dict(inventory.imported_counts),
        "preparedFeatureCounts": prepared_counts,
        "sourceWaysWithoutComparedKindExamples": inventory.unsupported_examples,
        "notes": "Categories overlap. Way counts do not prove grade-separated road crossings or engineering permission. Prepared gas crossing candidates are generated spans, not source ways.",
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
