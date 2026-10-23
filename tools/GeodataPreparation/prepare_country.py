"""Stream a verified Geofabrik OSM PBF into an offline SQLite/R-tree map and routing package.
Python and osmium run only while preparing distributable data, never in the application.
"""

import argparse
import hashlib
import gzip
import shutil
import json
import math
import sqlite3
from collections import Counter
from pathlib import Path

import osmium
from shapely import from_wkb, to_wkb, make_valid
from shapely.affinity import scale
from shapely.geometry import Point, Polygon
from prepare import METRES_PER_DEGREE, classify


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pbf", type=Path, required=True)
    parser.add_argument("--expected-md5", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument(
        "--compress",
        action="store_true",
        help="Create deterministic gzip asset for Android",
    )
    parser.add_argument("--audit-only", action="store_true")
    parser.add_argument("--finalize-only", action="store_true")
    parser.add_argument("--audit-report", type=Path)
    args = parser.parse_args()

    def digest(algorithm):
        h = hashlib.new(algorithm)
        with args.pbf.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                h.update(block)
        return h.hexdigest()

    if digest("md5") != args.expected_md5.lower():
        raise ValueError("Geofabrik PBF checksum mismatch")
    args.output.mkdir(parents=True, exist_ok=True)
    db = args.output / "geodata.sqlite"
    if db.exists() and not args.finalize_only:
        db.unlink()
    connection = sqlite3.connect(":memory:" if args.audit_only else db)
    if not args.finalize_only:
        connection.executescript("""
        PRAGMA journal_mode=OFF;
        PRAGMA synchronous=OFF;
        PRAGMA cache_size=-65536;
        CREATE TABLE features(id INTEGER PRIMARY KEY,kind TEXT NOT NULL,wkb BLOB NOT NULL,
            name TEXT NOT NULL,approval_id TEXT,source_id TEXT NOT NULL);
        CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);
        CREATE TABLE display_features(id INTEGER PRIMARY KEY,kind TEXT NOT NULL,wkb BLOB NOT NULL,
            name TEXT NOT NULL,lod INTEGER NOT NULL);
        CREATE VIRTUAL TABLE display_index USING rtree(id,minx,maxx,miny,maxy);
    """)
    counts = Counter()
    errors = Counter()
    factory = osmium.geom.WKBFactory()

    def add(kind, geometry, tags, source):
        if geometry.is_empty:
            errors["empty_" + kind] += 1
            return
        if not geometry.is_valid:
            geometry = make_valid(geometry)
        name = tags.get("name:ru", tags.get("name", ""))
        west, south, east, north = geometry.bounds
        if args.audit_only:
            counts[kind] += 1
            return
        raw = to_wkb(geometry)
        row = connection.execute(
            "INSERT INTO features(kind,wkb,name,source_id) VALUES(?,?,?,?)",
            (kind, raw, name, source),
        ).lastrowid
        connection.execute(
            "INSERT INTO feature_index VALUES(?,?,?,?,?)",
            (row, west, east, south, north),
        )
        counts[kind] += 1
        area = geometry.area
        road = tags.get("highway", "")
        for lod, tolerance in ((0, 0.003), (1, 0.0005), (2, 0.00005)):
            if lod == 0 and not (
                kind == "Settlement"
                and tags.get("place") in ("city", "town")
                or kind == "Road"
                and road in ("motorway", "trunk", "primary")
                or kind in ("Lake", "Forest")
                and area > 0.005
            ):
                continue
            if lod == 1 and not (
                kind == "Settlement"
                or kind == "Road"
                and road in ("motorway", "trunk", "primary", "secondary", "tertiary")
                or kind in ("River", "Lake", "Marsh", "Forest", "Field")
                and area > 0.00001
            ):
                continue
            if lod == 2 and kind == "Building":
                continue
            display = geometry.simplify(tolerance, preserve_topology=True)
            if display.is_empty:
                continue
            did = connection.execute(
                "INSERT INTO display_features(kind,wkb,name,lod) VALUES(?,?,?,?)",
                (kind, to_wkb(display), name, lod),
            ).lastrowid
            connection.execute(
                "INSERT INTO display_index VALUES(?,?,?,?,?)",
                (did, west, east, south, north),
            )
        if sum(counts.values()) % 10000 == 0:
            connection.commit()
            print(json.dumps(dict(counts)), flush=True)

    class Handler(osmium.SimpleHandler):
        def node(self, node):
            tags = dict(node.tags)
            if (
                tags.get("place") in ("city", "town", "village", "hamlet")
                and node.location.valid()
            ):
                add(
                    "Settlement",
                    Point(node.location.lon, node.location.lat),
                    tags,
                    f"node/{node.id}",
                )

        def way(self, way):
            tags = dict(way.tags)
            kind = classify(tags)
            # Areas are handled by osmium's multipolygon assembler, including closed ways and holes.
            if kind not in ("Road", "BridgeCandidate", "Clearing", "River"):
                return
            if kind == "River" and tags.get("waterway") not in (
                "river",
                "stream",
                "canal",
                "ditch",
                "drain",
            ):
                return
            try:
                geometry = from_wkb(bytes.fromhex(factory.create_linestring(way)))
                if kind in ("River", "Clearing"):
                    latitude = geometry.centroid.y
                    longitude_scale = METRES_PER_DEGREE * math.cos(
                        math.radians(latitude)
                    )
                    metres = scale(
                        geometry,
                        xfact=longitude_scale,
                        yfact=METRES_PER_DEGREE,
                        origin=(0, 0),
                    )
                    # OSM often lacks width. Conservative preparation assumptions:
                    # 12 m default / 6 m minimum for water, 4 m buffer for a clearing.
                    # These values are not surveyed widths or construction permissions.
                    try:
                        width = max(6, float(tags.get("width", "12")))
                    except ValueError:
                        width = 12
                    radius = width / 2 if kind == "River" else 4
                    geometry = scale(
                        metres.buffer(radius),
                        xfact=1 / longitude_scale,
                        yfact=1 / METRES_PER_DEGREE,
                        origin=(0, 0),
                    )
                add(kind, geometry, tags, f"way/{way.id}")
            except (RuntimeError, ValueError) as error:
                errors[kind + ":" + type(error).__name__] += 1

        def area(self, area):
            tags = dict(area.tags)
            kind = classify(tags)
            if kind is None or kind in ("Road", "BridgeCandidate", "Clearing"):
                return
            try:
                geometry = from_wkb(bytes.fromhex(factory.create_multipolygon(area)))
                add(
                    kind,
                    geometry,
                    tags,
                    ("way/" if area.from_way() else "relation/") + str(area.orig_id()),
                )
            except (RuntimeError, ValueError) as error:
                errors[kind + ":" + type(error).__name__] += 1

    if not args.finalize_only:
        Handler().apply_file(str(args.pbf), locations=True, idx="flex_mem")
    else:
        counts.update(
            dict(connection.execute("SELECT kind,count(*) FROM features GROUP BY kind"))
        )
        if args.audit_report:
            audited = json.loads(args.audit_report.read_text())
            if dict(counts) != audited["feature_counts"]:
                raise ValueError("Independent audit feature counts differ")
            errors.update(audited["conversion_errors"])
    connection.commit()
    connection.execute(
        "CREATE INDEX IF NOT EXISTS display_lod ON display_features(lod)"
    )
    connection.execute("ANALYZE")
    connection.commit()
    connection.close()
    reader = osmium.io.Reader(str(args.pbf))
    header = reader.header()
    boxes = [header.box()]
    bounds = [23.17, 51.25, 32.79, 56.18]
    if boxes:
        b = boxes[0]
        bounds = [
            b.bottom_left.lon,
            b.bottom_left.lat,
            b.top_right.lon,
            b.top_right.lat,
        ]
    reader.close()
    h = hashlib.sha256()
    if not args.audit_only:
        with db.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                h.update(block)
    manifest = dict(
        schema_version=2,
        region_id="belarus",
        coordinate_system="EPSG:4326",
        bounds=bounds,
        database_sha256=h.hexdigest(),
        attribution="© OpenStreetMap contributors · ODbL 1.0 · Geofabrik",
        status="Belarus OSM offline package; construction approvals, DEM and ESA WorldCover absent.",
        sources=[
            dict(
                name="OpenStreetMap/Geofabrik",
                url="https://download.geofabrik.de/europe/belarus.html",
                source_file=args.pbf.name,
                md5=args.expected_md5,
                sha256=digest("sha256"),
                license="ODbL-1.0",
                timestamp=header.get("osmosis_replication_timestamp"),
            )
        ],
        feature_counts=dict(counts),
        conversion_errors=dict(errors),
        terrain=False,
        worldcover=False,
        artifacts=[
            "base-map-vectors",
            "map-levels-of-detail",
            "obstacles",
            "routing-data",
            "spatial-index",
        ],
    )
    (args.output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n"
    )
    if args.compress and not args.audit_only:
        with (
            db.open("rb") as source,
            (args.output / "geodata.sqlite.gz").open("wb") as target,
        ):
            with gzip.GzipFile(
                fileobj=target, mode="wb", compresslevel=6, mtime=0
            ) as compressed:
                shutil.copyfileobj(source, compressed, 1024 * 1024)
    print(json.dumps(manifest, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
