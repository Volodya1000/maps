"""Freeze outcome-blind Belarus OSM research scenarios; never invokes the router.

Run with the GeodataPreparation locked environment. Candidate extraction may be
cached outside Git; the cache is bound to the verified PBF hash. Spatial context
is always recomputed against the verified database before selection.
"""

import argparse
import gzip
import hashlib
import importlib.metadata
import inspect
import json
import math
import sqlite3
import sys
from collections import Counter, defaultdict
from contextlib import closing
from pathlib import Path

from routing_crossing_pilot import distance as geographic_distance
from routing_crossing_pilot import query
from routing_regression import CONTROLS, CORPUS_SHA256
from shapely import from_wkb, geos_version_string, prepare, to_wkb
from shapely.geometry import LineString, Point, Polygon
from shapely.ops import nearest_points, transform

SCHEMA = "routing-research-corpus-v1"
SEED = 20261127
EXTRACTOR_PROTOCOL = "routing-research-osm-extractor-v1-broad-links-military-tags"
REGIONS = ("BY-BR", "BY-HO", "BY-HM", "BY-HR", "BY-MA", "BY-MI", "BY-VI")
REQUIRED_CATEGORIES = (
    "forest",
    "field",
    "countryside",
    "road-major",
    "road-secondary",
    "road-local",
    "roundabout",
    "interchange",
    "railway",
    "station",
    "underpass",
    "bridge",
    "water",
    "dense-buildings",
    "narrow-gap",
    "marsh",
    "restricted",
    "multi",
)
TAG_KEYS = (
    "highway",
    "junction",
    "railway",
    "tunnel",
    "location",
    "bridge",
    "waterway",
    "natural",
    "water",
    "landuse",
    "access",
    "military",
    "building",
    "name",
    "admin_level",
    "boundary",
    "ISO3166-2",
    "name:en",
)
METRES_PER_DEGREE = 111320.0
LONGITUDE_SCALE = METRES_PER_DEGREE * math.cos(math.radians(53.5))
OPTIONS = {
    "CellSizeMetres": 25,
    "MaxNodes": 100000,
    "ReserveFraction": 0.1,
    "PreferOverheadGasCrossings": True,
    "AllowExperimentalPedestrianUnderpasses": True,
    "MaxRouteLengthMetres": 1000000,
}
DISTANCES = {"local": 300, "medium": 1200, "long": 4000}
ENDPOINT_RIVER_CLEARANCE_METRES = 2
ENDPOINT_BLOCKING_KINDS = ("Building", "Lake", "Marsh", "River")
MULTI_OBSTACLE_KINDS = ("Road", *ENDPOINT_BLOCKING_KINDS)
CONTEXT_SEGMENT_METRES = 300


def sha256_file(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def canonical_bytes(value):
    return (
        json.dumps(value, indent=2, ensure_ascii=False, sort_keys=True) + "\n"
    ).encode("utf-8")


def categories_for_tags(tags):
    """Labels describe source features, never engineering approval or prohibition."""
    result = []
    highway = tags.get("highway")
    if tags.get("landuse") == "forest" or tags.get("natural") == "wood":
        result.append("forest")
    if tags.get("landuse") == "farmland":
        result.append("field")
    if tags.get("landuse") == "meadow" or tags.get("natural") == "grassland":
        result.append("countryside")
    if highway in ("motorway", "trunk", "primary"):
        result.append("road-major")
    if highway in ("secondary", "tertiary"):
        result.append("road-secondary")
    if highway in ("residential", "unclassified", "service", "track", "living_street"):
        result.append("road-local")
    if tags.get("junction") in ("roundabout", "circular"):
        result.append("roundabout")
    if highway in ("motorway_link", "trunk_link"):
        result.append("interchange")
    if tags.get("railway") in ("rail", "light_rail", "narrow_gauge", "tram"):
        result.append("railway")
    if tags.get("railway") in ("station", "halt", "yard"):
        result.append("station")
    if highway in ("footway", "path", "pedestrian", "steps") and (
        tags.get("tunnel") not in (None, "no") or tags.get("location") == "underground"
    ):
        result.append("underpass")
    if tags.get("bridge") not in (None, "no"):
        result.append("bridge")
    if (
        tags.get("natural") == "water"
        or tags.get("waterway") in ("river", "stream", "canal")
        or tags.get("landuse") == "reservoir"
    ):
        result.append("water")
    if tags.get("natural") == "wetland":
        result.append("marsh")
    if (
        tags.get("access") in ("private", "no", "military")
        or tags.get("landuse") == "military"
        or tags.get("military") not in (None, "no")
    ):
        result.append("restricted")
    return sorted(result)


def project(geometry):
    return transform(
        lambda x, y, z=None: (x * LONGITUDE_SCALE, y * METRES_PER_DEGREE), geometry
    )


def semantic_categories(tags):
    """Strict final labels; broad extraction families remain cache-compatible."""
    categories = categories_for_tags(tags)
    if "station" in categories and tags.get("railway") not in ("station", "halt"):
        categories.remove("station")
    if (
        "underpass" in categories
        and tags.get("tunnel") != "yes"
        and tags.get("location") != "underground"
    ):
        categories.remove("underpass")
    return categories


def filter_semantic_candidates(candidates):
    filtered = []
    rejected = Counter()
    for item in candidates:
        tag_labels = {
            category
            for evidence in item["osmEvidence"]
            for category in semantic_categories(evidence["tags"])
        }
        categories = [
            category
            for category in item["categories"]
            if category in tag_labels
            or category in ("multi", "dense-buildings", "narrow-gap")
        ]
        for category in set(item["categories"]) - set(categories):
            rejected[f"semantic-label-removed:{category}"] += 1
        if categories:
            filtered.append(dict(item, categories=categories))
        else:
            rejected["no-final-semantic-category"] += 1
    return filtered, dict(rejected)


def unproject(geometry):
    return transform(
        lambda x, y, z=None: (x / LONGITUDE_SCALE, y / METRES_PER_DEGREE), geometry
    )


def containing_region(regions, geometry):
    return next(
        (region for region in regions if region["geometry"].covers(geometry)), None
    )


class QuotaShortfall(ValueError):
    def __init__(self, shortfalls):
        super().__init__("OSM candidate quotas could not be fulfilled")
        self.shortfalls = shortfalls


def rank(seed, value):
    return hashlib.sha256(f"{seed}:{value}".encode()).hexdigest()


def select_candidates(candidates, legacy_lines, quotas, *, seed=SEED):
    """Select stable targets with corridor separation, not just endpoint separation.

    Candidate lines use the declared study projection. Holdout is picked first;
    its entire corridor excludes development and historical corridors by 1 km.
    Every other pair of new corridors is separated by at least 250 m.
    """
    candidates = sorted(
        candidates,
        key=lambda item: (
            rank(seed, item["sourceId"]),
            item["sourceId"],
            item.get("distanceClass", ""),
        ),
    )
    selected = []
    used = set()
    rejected = Counter()
    shortfalls = {}
    for split in ("holdout", "development"):
        for region, counts in sorted(quotas.items()):
            target = counts[split]
            eligible = [item for item in candidates if item["region"] == region]
            picked = 0
            # Rotate held-out feature classes across regions. Pair class goals
            # with lengths so early category quotas cannot consume every slot
            # at the same lexically first distance class.
            offset = list(sorted(quotas)).index(region) * 3 if split == "holdout" else 0
            category_order = (
                REQUIRED_CATEGORIES[offset % len(REQUIRED_CATEGORIES) :]
                + REQUIRED_CATEGORIES[: offset % len(REQUIRED_CATEGORIES)]
            )
            lengths = ("local", "medium", "local", "long", "local", "medium")
            goals = [
                (category, lengths[index % len(lengths)])
                for index, category in enumerate(category_order)
            ]
            goals += [(category, None) for category in category_order]
            goals += [(None, distance) for distance in DISTANCES for _ in range(3)]
            goals += [(None, None)] * target
            for category, distance in goals:
                if picked == target:
                    break
                for candidate in eligible:
                    source = candidate["sourceId"]
                    if (
                        source in used
                        or (category and category not in candidate["categories"])
                        or (distance and candidate.get("distanceClass") != distance)
                    ):
                        continue
                    line = candidate["line"]
                    legacy_minimum = 1000 if split == "holdout" else 250
                    if any(line.distance(old) < legacy_minimum for old in legacy_lines):
                        rejected["legacy-spatial-overlap"] += 1
                        continue
                    if any(
                        line.distance(old["line"])
                        < (1000 if split != old["split"] else 250)
                        for old in selected
                    ):
                        rejected["new-spatial-overlap"] += 1
                        continue
                    selected.append(dict(candidate, split=split))
                    used.add(source)
                    picked += 1
                    break
            if picked < target:
                shortfalls[f"{region}/{split}"] = target - picked
    if shortfalls:
        raise QuotaShortfall(shortfalls)
    return selected, dict(sorted(rejected.items()))


def extract_pbf(path):
    """Retain a deterministic tag sample and exact level-four boundary polygons."""
    import osmium

    candidates = []
    regions = []
    available = Counter()
    errors = Counter()
    factory = osmium.geom.WKBFactory()

    def keep(source, tags):
        categories = categories_for_tags(tags)
        # Extraction intentionally retains a broad superset. Final semantic
        # evidence calls categories_for_tags again and never treats an ordinary
        # turning link as an interchange or military=no as restricted land.
        if tags.get("highway") in ("primary_link", "secondary_link", "tertiary_link"):
            categories.append("interchange")
        if "military" in tags and "restricted" not in categories:
            categories.append("restricted")
        building = tags.get("building") not in (None, "no")
        available.update(categories)
        if building:
            available["building"] += 1
        if not categories and not building:
            return False
        ordinary = {
            "forest",
            "field",
            "countryside",
            "road-major",
            "road-secondary",
            "road-local",
            "railway",
            "water",
        }
        divisor = 100 if building else 25
        return (
            bool(set(categories) - ordinary)
            or int(rank(SEED, source)[:8], 16) % divisor == 0
        )

    def add(source, tags, geometry):
        if geometry.is_empty or not geometry.is_valid:
            errors["invalid-candidate-geometry"] += 1
            return
        candidates.append(
            {
                "sourceId": source,
                "tags": {key: tags[key] for key in TAG_KEYS if key in tags},
                "geometry": geometry,
            }
        )

    class Handler(osmium.SimpleHandler):
        def node(self, node):
            tags = dict(node.tags)
            if (
                tags.get("railway") not in ("station", "halt", "yard")
                or not node.location.valid()
            ):
                return
            source = f"node/{node.id}"
            if keep(source, tags):
                add(source, tags, Point(node.location.lon, node.location.lat))

        def way(self, way):
            tags = dict(way.tags)
            source = f"way/{way.id}"
            if not keep(source, tags):
                return
            try:
                geometry = from_wkb(bytes.fromhex(factory.create_linestring(way)))
                if way.is_closed() and (
                    tags.get("building") or tags.get("landuse") or tags.get("natural")
                ):
                    geometry = Polygon(geometry.coords)
                add(source, tags, geometry)
            except (RuntimeError, ValueError) as error:
                errors[type(error).__name__] += 1

        def area(self, area):
            tags = dict(area.tags)
            region = tags.get("ISO3166-2")
            if tags.get("admin_level") == "4" and region in REGIONS:
                geometry = from_wkb(bytes.fromhex(factory.create_multipolygon(area)))
                if geometry.is_valid and not geometry.is_empty:
                    regions.append(
                        {
                            "region": region,
                            "sourceId": f"relation/{area.orig_id()}",
                            "tags": {key: tags[key] for key in TAG_KEYS if key in tags},
                            "geometry": geometry,
                        }
                    )

    handler = Handler()
    handler.apply_file(str(path), locations=True, idx="flex_mem")
    del handler
    if {region["region"] for region in regions} != set(REGIONS):
        raise ValueError(
            "Pinned PBF does not assemble all seven Belarus region polygons"
        )
    return (
        candidates,
        sorted(regions, key=lambda item: item["region"]),
        dict(available),
        dict(errors),
    )


def building_context(connection, candidate):
    geometry = project(candidate["geometry"])
    point = geometry.representative_point()
    bounds = unproject(point.buffer(150)).bounds
    rows = connection.execute(
        "SELECT f.source_id,f.wkb FROM features f JOIN feature_index i ON i.id=f.id "
        "WHERE f.kind='Building' AND i.minx<=? AND i.maxx>=? AND i.miny<=? AND i.maxy>=?",
        (bounds[2], bounds[0], bounds[3], bounds[1]),
    )
    neighbours = [
        (source, project(from_wkb(blob)))
        for source, blob in rows
        if source != candidate["sourceId"]
    ]
    neighbours = sorted(
        (
            (source, shape, geometry.distance(shape))
            for source, shape in neighbours
            if point.distance(shape) <= 150
        ),
        key=lambda item: (item[2], item[0]),
    )
    evidence = {
        "buildingCountWithin150Metres": len(neighbours) + 1,
        "neighbourSourceIds": [item[0] for item in neighbours],
    }
    categories = []
    if len(neighbours) >= 11:
        categories.append("dense-buildings")
    gaps = [item for item in neighbours if 2 <= item[2] <= 30]
    if gaps:
        source, other, gap = gaps[0]
        first, second = nearest_points(geometry, other)
        point = Point((first.x + second.x) / 2, (first.y + second.y) / 2)
        evidence.update(
            {
                "gapMetres": round(gap, 3),
                "gapNeighbourSourceId": source,
                "passageBearingRadians": math.atan2(
                    second.y - first.y, second.x - first.x
                )
                + math.pi / 2,
            }
        )
        categories.append("narrow-gap")
    return categories, point, evidence


def endpoint_blocker(connection, point):
    """Geodata-only placement screen; deliberately never audits the connecting line.

    Polygons use exact covers, as application placement validation does. For
    curve rivers, a conservative two projected metre exclusion includes the
    application's one metre uncertainty margin despite study projection error.
    """
    projected = project(point)
    bounds = unproject(projected.buffer(ENDPOINT_RIVER_CLEARANCE_METRES)).bounds
    for feature in query(connection, bounds, ENDPOINT_BLOCKING_KINDS):
        geometry = feature["geometry"]
        if geometry.covers(point):
            return feature["kind"]
        if (
            feature["kind"] == "River"
            and geometry.geom_type in ("LineString", "MultiLineString")
            and project(geometry).distance(projected) <= ENDPOINT_RIVER_CLEARANCE_METRES
        ):
            return "River"
    return None


def obstacle_context(connection, segment):
    """Measure co-occurring source geometries; this does not prove no detour."""
    sources = defaultdict(set)
    for feature in query(connection, segment.bounds, MULTI_OBSTACLE_KINDS):
        if feature["geometry"].intersects(segment):
            sources[feature["kind"]].add(feature["source_id"])
    distinct = {source for identifiers in sources.values() for source in identifiers}
    return {
        "obstacleKinds": sorted(sources),
        "obstacleSourceCounts": {
            kind: len(identifiers) for kind, identifiers in sorted(sources.items())
        },
        "obstacleSourceExamples": {
            kind: sorted(identifiers)[:3]
            for kind, identifiers in sorted(sources.items())
        },
        "distinctObstacleSourceCount": len(distinct),
        "isMultiObstacle": len(sources) >= 2 and len(distinct) >= 2,
        "measurementSegment": [
            {"Longitude": x, "Latitude": y} for x, y in segment.coords
        ],
        "proofScope": "local geometry exposure only; does not establish infeasibility or absence of a detour",
    }


def make_candidates(raw, regions, database, *, coverage=None):
    result = []
    rejected = Counter()
    for region in regions:
        prepare(region["geometry"])
        region["geometrySha256"] = hashlib.sha256(
            to_wkb(region["geometry"])
        ).hexdigest()
    with closing(
        sqlite3.connect(f"file:{Path(database).as_posix()}?mode=ro", uri=True)
    ) as connection:
        for item in sorted(raw, key=lambda candidate: candidate["sourceId"]):
            categories = categories_for_tags(item["tags"])
            if "restricted" in categories and (
                item["geometry"].geom_type not in ("Polygon", "MultiPolygon")
                or item["tags"].get("building") not in (None, "no")
            ):
                categories.remove("restricted")
            geometry = project(item["geometry"])
            center = (
                geometry.interpolate(0.5, normalized=True)
                if geometry.geom_type == "LineString"
                else geometry.representative_point()
            )
            context = None
            if item["tags"].get("building") not in (None, "no"):
                building_categories, center, context = building_context(
                    connection, item
                )
                categories += building_categories
            if not categories:
                continue
            # Direction is fixed from source geometry, with hash bearing for areas.
            if context and "passageBearingRadians" in context:
                angle = context["passageBearingRadians"]
            elif geometry.geom_type == "LineString" and geometry.length > 0:
                a = geometry.interpolate(0.45, normalized=True)
                b = geometry.interpolate(0.55, normalized=True)
                angle = math.atan2(b.y - a.y, b.x - a.x)
                if not {"bridge", "underpass"} & set(categories):
                    angle += math.pi / 2
            else:
                angle = (
                    int(rank(SEED, item["sourceId"])[8:16], 16) / 0xFFFFFFFF * math.pi
                )
            dx = math.cos(angle) * CONTEXT_SEGMENT_METRES / 2
            dy = math.sin(angle) * CONTEXT_SEGMENT_METRES / 2
            measured = obstacle_context(
                connection,
                unproject(
                    LineString(
                        [(center.x - dx, center.y - dy), (center.x + dx, center.y + dy)]
                    )
                ),
            )
            context = (context or {}) | measured
            if measured["isMultiObstacle"]:
                categories.append("multi")
            for distance_class, distance in DISTANCES.items():
                dx, dy = math.cos(angle) * distance / 2, math.sin(angle) * distance / 2
                line = LineString(
                    [(center.x - dx, center.y - dy), (center.x + dx, center.y + dy)]
                )
                geographic = unproject(line)
                if coverage is not None and any(
                    not coverage.covers(Point(coordinate))
                    for coordinate in geographic.coords
                ):
                    rejected["outside-package-endpoint"] += 1
                    continue
                blockers = [
                    endpoint_blocker(connection, Point(coordinate))
                    for coordinate in geographic.coords
                ]
                if any(blockers):
                    for kind in set(blockers) - {None}:
                        rejected[f"blocked-endpoint:{kind}"] += 1
                    continue
                region = containing_region(regions, geographic)
                if region is None:
                    rejected["outside-single-administrative-region"] += 1
                    continue
                evidence = {
                    "sourceId": item["sourceId"],
                    "tags": item["tags"],
                    "geometryType": item["geometry"].geom_type,
                    "geometrySha256": hashlib.sha256(
                        to_wkb(item["geometry"])
                    ).hexdigest(),
                }
                if context:
                    evidence["spatialContext"] = context
                result.append(
                    {
                        "sourceId": item["sourceId"],
                        "region": region["region"],
                        "categories": sorted(set(categories)),
                        "line": line,
                        "distanceClass": distance_class,
                        "osmEvidence": [evidence],
                        "regionEvidence": {
                            key: region[key]
                            for key in ("sourceId", "tags", "geometrySha256")
                        },
                    }
                )
    return result, dict(rejected)


def validate_candidate_cache(cache, source_sha256):
    if (
        cache.get("sourceSha256") != source_sha256
        or cache.get("extractorProtocol") != EXTRACTOR_PROTOCOL
        or cache.get("seed") != SEED
        or cache.get("tagKeys") != list(TAG_KEYS)
    ):
        raise ValueError("Candidate cache source or extractor protocol mismatch")


def context_signature():
    names = (
        "make_candidates",
        "endpoint_blocker",
        "obstacle_context",
        "building_context",
        "categories_for_tags",
        "project",
        "unproject",
        "containing_region",
    )
    constants = {
        name: globals()[name]
        for name in (
            "DISTANCES",
            "METRES_PER_DEGREE",
            "LONGITUDE_SCALE",
            "ENDPOINT_RIVER_CLEARANCE_METRES",
            "ENDPOINT_BLOCKING_KINDS",
            "MULTI_OBSTACLE_KINDS",
            "CONTEXT_SEGMENT_METRES",
            "SEED",
        )
    }
    return hashlib.sha256(
        (
            "".join(inspect.getsource(globals()[name]) for name in names)
            + json.dumps(constants, sort_keys=True)
        ).encode()
    ).hexdigest()


def write_context_cache(path, candidates, metadata, rejections):
    path.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(path, "wt", encoding="utf-8", compresslevel=1) as stream:
        stream.write(
            json.dumps(
                metadata
                | {
                    "schema": "routing-research-context-cache-v1",
                    "rejections": rejections,
                },
                sort_keys=True,
            )
            + "\n"
        )
        for item in candidates:
            stream.write(
                json.dumps(
                    dict(item, line=to_wkb(item["line"]).hex()),
                    sort_keys=True,
                    ensure_ascii=False,
                    separators=(",", ":"),
                )
                + "\n"
            )


def load_context_cache(path, expected):
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        metadata = json.loads(next(stream))
        if metadata.get("schema") != "routing-research-context-cache-v1" or any(
            metadata.get(field) != value for field, value in expected.items()
        ):
            raise ValueError(
                "Enriched candidate cache input or implementation mismatch"
            )
        candidates = []
        for line in stream:
            item = json.loads(line)
            item["line"] = from_wkb(bytes.fromhex(item["line"]))
            candidates.append(item)
    return candidates, metadata["rejections"]


def validate_manifest(manifest, legacy_sha256=None):
    if manifest.get("schema") != SCHEMA or manifest.get("seed") != SEED:
        raise ValueError("Unsupported frozen corpus schema or seed")
    if not manifest.get("converterRecipeKey", "").startswith("belarus-v1-"):
        raise ValueError("Missing pinned converter recipe")
    for field in (
        "sourceSha256",
        "databaseSha256",
        "legacyCorpusSha256",
        "coverageSha256",
    ):
        digest = manifest.get(field, "")
        if len(digest) != 64 or any(
            character not in "0123456789abcdef" for character in digest
        ):
            raise ValueError(f"Invalid source digest: {field}")
    if manifest["legacyCorpusSha256"] != CORPUS_SHA256 or (
        legacy_sha256 is not None and manifest["legacyCorpusSha256"] != legacy_sha256
    ):
        raise ValueError("Historical corpus hash mismatch")
    cases = manifest["cases"]
    if len(cases) != 212 or Counter(case["split"] for case in cases) != {
        "development": 170,
        "holdout": 42,
    }:
        raise ValueError("Frozen corpus split mismatch")
    if len({case["id"] for case in cases}) != len(cases):
        raise ValueError("Duplicate corpus ID")
    if len({case["osmEvidence"][0]["sourceId"] for case in cases}) != len(cases):
        raise ValueError("Duplicate primary target")
    if {case["region"] for case in cases} != set(REGIONS):
        raise ValueError("Missing Belarus administrative region")
    for region in REGIONS:
        counts = Counter(case["split"] for case in cases if case["region"] == region)
        if counts != {"development": 26 if region == "BY-HM" else 24, "holdout": 6}:
            raise ValueError("Regional split quota mismatch")
    observed = set()
    lines = []
    for case in cases:
        if (
            case["scenario"]["id"] != case["id"]
            or case["distanceClass"] not in DISTANCES
        ):
            raise ValueError("Scenario identity or distance mismatch")
        points = case["scenario"]["points"]
        if len(points) != 2 or any(
            not math.isfinite(point["Latitude"])
            or not math.isfinite(point["Longitude"])
            or not -90 <= point["Latitude"] <= 90
            or not -180 <= point["Longitude"] <= 180
            for point in points
        ):
            raise ValueError("Invalid corpus coordinates")
        line = project(
            LineString([(point["Longitude"], point["Latitude"]) for point in points])
        )
        if abs(line.length - DISTANCES[case["distanceClass"]]) > 0.01:
            raise ValueError("Distance stratum does not match geometry")
        if case["scenario"]["routingOptions"] != OPTIONS:
            raise ValueError("Unfrozen routing options")
        lines.append(line)
        evidence = case["osmEvidence"]
        justified = {
            category
            for item in evidence
            for category in semantic_categories(item["tags"])
            if category != "restricted"
            or (
                item.get("geometryType") in ("Polygon", "MultiPolygon")
                and item["tags"].get("building") in (None, "no")
            )
        }
        for item in evidence:
            context = item.get("spatialContext", {})
            if item["tags"].get("building") not in (None, "no"):
                if context.get("buildingCountWithin150Metres", 0) >= 12:
                    justified.add("dense-buildings")
                if 2 <= context.get("gapMetres", 0) <= 30 and context.get(
                    "gapNeighbourSourceId"
                ):
                    justified.add("narrow-gap")
            kinds = set(context.get("obstacleKinds", []))
            source_examples = context.get("obstacleSourceExamples", {})
            distinct_sources = {
                source
                for kind, sources in source_examples.items()
                if kind in kinds
                for source in sources
            }
            if (
                context.get("isMultiObstacle") is True
                and len(kinds) >= 2
                and kinds <= set(MULTI_OBSTACLE_KINDS)
                and len(distinct_sources) >= 2
                and context.get("distinctObstacleSourceCount", 0)
                >= len(distinct_sources)
            ):
                justified.add("multi")
        if (
            not case["categories"]
            or len(set(case["categories"])) != len(case["categories"])
            or not set(case["categories"]) <= justified
        ):
            raise ValueError("Unsupported OSM category evidence")
        complexity = (
            "dense"
            if "dense-buildings" in case["categories"]
            else "combined"
            if "multi" in case["categories"]
            else "single"
        )
        if case["complexity"] != complexity:
            raise ValueError("Unsupported complexity label")
        if (
            case["regionEvidence"]["tags"].get("ISO3166-2") != case["region"]
            or case["regionEvidence"]["tags"].get("admin_level") != "4"
        ):
            raise ValueError("Unsupported administrative region evidence")
        observed.update(case["categories"])
    if not set(REQUIRED_CATEGORIES) <= observed:
        raise QuotaShortfall(
            {
                f"category/{category}": 1
                for category in sorted(set(REQUIRED_CATEGORIES) - observed)
            }
        )
    for split in ("development", "holdout"):
        categories = {
            category
            for case in cases
            if case["split"] == split
            for category in case["categories"]
        }
        if not set(REQUIRED_CATEGORIES) <= categories:
            raise QuotaShortfall(
                {
                    f"{split}/category/{category}": 1
                    for category in sorted(set(REQUIRED_CATEGORIES) - categories)
                }
            )
        for field, required in (
            ("distanceClass", set(DISTANCES)),
            ("complexity", {"single", "combined", "dense"}),
        ):
            missing = required - {
                case[field] for case in cases if case["split"] == split
            }
            if missing:
                raise QuotaShortfall(
                    {f"{split}/{field}/{value}": 1 for value in sorted(missing)}
                )
    for index, first in enumerate(cases):
        for second_index in range(index + 1, len(cases)):
            minimum = 1000 if first["split"] != cases[second_index]["split"] else 250
            if lines[index].distance(lines[second_index]) < minimum - 0.01:
                raise ValueError("Frozen spatial separation violated")


def generate(args):
    print(json.dumps({"phase": "verifying-pinned-inputs"}), file=sys.stderr, flush=True)
    if sha256_file(args.legacy) != CORPUS_SHA256:
        raise ValueError(
            "Historical corpus bytes differ from the immutable 88-case input"
        )
    source_hash = sha256_file(args.pbf)
    database_hash = sha256_file(args.database)
    if (
        source_hash != args.expected_source_sha256
        or database_hash != args.expected_database_sha256
    ):
        raise ValueError("Pinned source checksum mismatch")
    from GeodataPreparation.country_data import cache_key

    root = Path(__file__).resolve().parents[1]
    coverage_path = args.coverage or root / "data/belarus-coverage.json"
    coverage = Polygon(json.loads(coverage_path.read_text(encoding="utf-8")))
    if coverage.is_empty or not coverage.is_valid:
        raise ValueError("Invalid supported package coverage polygon")
    prepare(coverage)
    lock = json.loads(
        (root / "tools/GeodataPreparation/belarus.lock.json").read_text(
            encoding="utf-8"
        )
    )
    package_path = args.package_receipt or args.database.parent / "package.json"
    package = json.loads(package_path.read_text(encoding="utf-8"))
    package_manifest = json.loads(
        (package_path.parent / "manifest.json").read_text(encoding="utf-8")
    )
    if (
        package["key"] != cache_key(lock, root)
        or package["source_sha256"] != source_hash
        or package_manifest["database_sha256"] != database_hash
    ):
        raise ValueError(
            "Generation database does not match the current verified converter recipe"
        )
    cache = None
    print(
        json.dumps({"phase": "loading-osm-candidates", "cache": str(args.cache)}),
        file=sys.stderr,
        flush=True,
    )
    if args.cache.exists():
        cache = json.loads(args.cache.read_text(encoding="utf-8"))
        validate_candidate_cache(cache, source_hash)
        raw, regions = cache["candidates"], cache["regions"]
        if not (args.context_cache and args.context_cache.exists()):
            for item in raw + regions:
                item["geometry"] = from_wkb(bytes.fromhex(item["geometry"]))
        available, extraction_errors = cache["available"], cache["errors"]
    else:
        raw, regions, available, extraction_errors = extract_pbf(args.pbf)
        args.cache.parent.mkdir(parents=True, exist_ok=True)
        args.cache.write_bytes(
            canonical_bytes(
                {
                    "sourceSha256": source_hash,
                    "extractorProtocol": EXTRACTOR_PROTOCOL,
                    "seed": SEED,
                    "tagKeys": list(TAG_KEYS),
                    "available": available,
                    "errors": extraction_errors,
                    "candidates": [
                        dict(item, geometry=to_wkb(item["geometry"]).hex())
                        for item in raw
                    ],
                    "regions": [
                        dict(item, geometry=to_wkb(item["geometry"]).hex())
                        for item in regions
                    ],
                }
            )
        )
    retained_targets = len(raw)
    context_metadata = {
        "databaseSha256": database_hash,
        "sourceCacheSha256": sha256_file(args.cache),
        "coverageSha256": sha256_file(coverage_path),
        "contextSignature": context_signature(),
    }
    if args.context_cache and args.context_cache.exists():
        print(
            json.dumps(
                {
                    "phase": "loading-verified-spatial-context",
                    "cache": str(args.context_cache),
                }
            ),
            file=sys.stderr,
            flush=True,
        )
        del raw
        del cache
        candidates, rejected = load_context_cache(args.context_cache, context_metadata)
    else:
        print(
            json.dumps(
                {
                    "phase": "enriching-spatial-context",
                    "sourceTargets": retained_targets,
                }
            ),
            file=sys.stderr,
            flush=True,
        )
        candidates, rejected = make_candidates(
            raw, regions, args.database, coverage=coverage
        )
        if args.context_cache:
            write_context_cache(
                args.context_cache, candidates, context_metadata, rejected
            )
    candidates, semantic_rejections = filter_semantic_candidates(candidates)
    print(
        json.dumps(
            {
                "phase": "selecting-frozen-corpus",
                "eligibleVariants": len(candidates),
                "databaseConnection": "closed",
            }
        ),
        file=sys.stderr,
        flush=True,
    )
    rejected = dict(Counter(rejected) + Counter(semantic_rejections))
    legacy = json.loads(args.legacy.read_text(encoding="utf-8"))
    controls = []
    control_sources = []
    for _, source, *_ in CONTROLS:
        path = root / "docs/evidence" / source
        controls.append(json.loads(path.read_text(encoding="utf-8")))
        control_sources.append(
            {
                "sourceFile": path.relative_to(root).as_posix(),
                "sha256": sha256_file(path),
            }
        )
    legacy_targets = {
        case["targetRoad"]["sourceId"] for case in legacy if case.get("targetRoad")
    }
    before = len(candidates)
    candidates = [
        candidate
        for candidate in candidates
        if candidate["sourceId"] not in legacy_targets
    ]
    rejected["historical-primary-target"] = before - len(candidates)
    legacy_lines = [
        project(
            LineString(
                [(point["Longitude"], point["Latitude"]) for point in case["points"]]
            )
        )
        for case in legacy + controls
    ]
    quotas = {
        region: {"development": 26 if region == "BY-HM" else 24, "holdout": 6}
        for region in REGIONS
    }
    selected, spatial_rejections = select_candidates(candidates, legacy_lines, quotas)
    cases = []
    for candidate in selected:
        points = [
            {"Latitude": y, "Longitude": x}
            for x, y in unproject(candidate["line"]).coords
        ]
        bounds = unproject(candidate["line"]).buffer(0.03).bounds
        identifier = f"research-{candidate['region'].lower()}-{candidate['sourceId'].replace('/', '-')}"
        categories = candidate["categories"]
        complexity = (
            "dense"
            if "dense-buildings" in categories
            else "combined"
            if "multi" in categories
            else "single"
        )
        cases.append(
            {
                key: candidate[key]
                for key in (
                    "region",
                    "categories",
                    "split",
                    "distanceClass",
                    "osmEvidence",
                    "regionEvidence",
                )
            }
            | {
                "id": identifier,
                "complexity": complexity,
                "scenario": {
                    "id": identifier,
                    "points": points,
                    "routingOptions": OPTIONS,
                    "distanceMetres": geographic_distance(
                        *[(point["Longitude"], point["Latitude"]) for point in points]
                    ),
                    "auditBounds": list(bounds),
                },
            }
        )
    manifest = {
        "schema": SCHEMA,
        "seed": SEED,
        "sourceSha256": source_hash,
        "databaseSha256": database_hash,
        "converterRecipeKey": package["key"],
        "coverageSha256": sha256_file(coverage_path),
        "legacyCorpusSha256": sha256_file(args.legacy),
        "controlInputSources": control_sources,
        "policy": {
            "selection": "OSM-only SHA256 source-ID ordering; no router execution or outcome access",
            "projection": "equirectangular 53.5N; 111320 metres/degree latitude",
            "distancesMetres": DISTANCES,
            "regionalQuotas": quotas,
            "holdoutSeparationMetres": 1000,
            "newCorridorSeparationMetres": 250,
            "legacyDevelopmentSeparationMetres": 250,
            "historicalCorridors": "immutable 88 inputs plus all six separate control inputs; control assessment remains separate",
            "railwayCrossings": "intentionally allowed",
            "restrictedAreas": "category/model limitation; no new routing prohibition",
            "restrictedCategoryScope": "restricted/military tags on non-building polygons only; private road ways do not establish area coverage",
            "categoryMeaning": "source feature context, not proof that the route crosses it or is constructible",
            "administrativeMembership": "entire direct scenario segment covered by exact PBF admin_level=4 polygon, including holes",
            "engineeringLimitations": "OSM bridge is not crossing approval; building gap dimensions are map geometry, not surveyed clearance",
            "endpointPlacementScreen": {
                "blockingKinds": list(ENDPOINT_BLOCKING_KINDS),
                "polygonRule": "exact covers, including boundary",
                "curveRiverClearanceMetres": ENDPOINT_RIVER_CLEARANCE_METRES,
                "scope": "only endpoints; direct route and route outcomes never inspected",
            },
            "categoryQuotas": "development goal one per available region/category; held-out categories rotate regionally; global full category inventory required; unmet regional goals reported",
            "minimumPerSplit": {
                "category": {category: 1 for category in REQUIRED_CATEGORIES},
                "distanceClass": {distance: 1 for distance in DISTANCES},
                "complexity": {
                    complexity: 1 for complexity in ("single", "combined", "dense")
                },
            },
            "candidatePrefilter": "all rare tag classes; SHA256 modulo25 ordinary ways, modulo100 buildings; all station nodes",
            "extractorProtocol": EXTRACTOR_PROTOCOL,
            "extractionSuperset": "all link-road classes and military-tagged objects retained; final categories revalidated strictly from tags",
            "strictSemanticLabels": "station requires railway=station/halt; underpass requires tunnel=yes or location=underground on a pedestrian way, without requiring importer-specific layer tags",
            "multiObstacleRule": {
                "kinds": list(MULTI_OBSTACLE_KINDS),
                "minimumDistinctKinds": 2,
                "minimumDistinctSourceIds": 2,
                "measurementSegmentMetres": CONTEXT_SEGMENT_METRES,
                "scope": "intersection with central direct segment; not a no-detour or construction-feasibility proof",
            },
            "complexityScope": "local 300m source context: dense >=12 buildings/150m, combined = measured multi, otherwise single; not full-route difficulty",
            "holdout": "selected first without outcomes; entire direct corridors exclude development by 1km; evaluation only",
        },
        "cases": sorted(cases, key=lambda case: case["id"]),
    }
    validate_manifest(manifest)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(canonical_bytes(manifest))
    regional_targets = {region: defaultdict(set) for region in REGIONS}
    for candidate in candidates:
        for category in candidate["categories"]:
            regional_targets[candidate["region"]][category].add(candidate["sourceId"])
    unmet_categories = {}
    unavailable_categories = {}
    for region in REGIONS:
        development_categories = {
            category
            for case in cases
            if case["region"] == region and case["split"] == "development"
            for category in case["categories"]
        }
        unmet_categories[region] = sorted(
            set(regional_targets[region]) - development_categories
        )
        unavailable_categories[region] = sorted(
            set(REQUIRED_CATEGORIES) - set(regional_targets[region])
        )
    receipt = {
        "schema": "routing-research-generation-v1",
        "corpusSha256": sha256_file(args.output),
        "sourceSha256": source_hash,
        "databaseSha256": database_hash,
        "converterRecipeKey": package["key"],
        "coverageSha256": sha256_file(coverage_path),
        "candidateCacheSha256": sha256_file(args.cache),
        "packageReceiptSha256": sha256_file(package_path),
        "packageManifestSha256": sha256_file(package_path.parent / "manifest.json"),
        "contextCacheSha256": sha256_file(args.context_cache)
        if args.context_cache
        else None,
        "contextSignature": context_metadata["contextSignature"],
        "extractorProtocol": EXTRACTOR_PROTOCOL,
        "legacyCorpusSha256": manifest["legacyCorpusSha256"],
        "seed": SEED,
        "generatorSha256": sha256_file(Path(__file__)),
        "runtime": {
            "python": sys.version.split()[0],
            "platform": sys.platform,
            "shapely": importlib.metadata.version("shapely"),
            "geos": geos_version_string,
            "osmium": importlib.metadata.version("osmium"),
        },
        "source": "Geofabrik Belarus OSM PBF pinned by SHA256; OpenStreetMap contributors, ODbL-1.0",
        "sourceTagAvailabilityBeforePrefilter": {
            (
                "station-halt-or-yard"
                if category == "station"
                else "potential-pedestrian-tunnel"
                if category == "underpass"
                else "link-road"
                if category == "interchange"
                else "access-or-military-tag"
                if category == "restricted"
                else category
            ): count
            for category, count in available.items()
        },
        "retainedSourceTargets": retained_targets,
        "eligibleScenarioVariants": len(candidates),
        "regionalCandidateTargets": {
            region: {
                category: len(targets)
                for category, targets in sorted(categories.items())
            }
            for region, categories in regional_targets.items()
        },
        "unfilledDevelopmentCategoryGoals": unmet_categories,
        "unavailableRegionalCategories": unavailable_categories,
        "rejections": rejected | spatial_rejections,
        "rejectionCountUnit": "input-stage variant rejections and selection attempts; a source can contribute multiple variants/goals",
        "extractionErrors": extraction_errors,
        "quotaShortfalls": {},
        "counts": {
            "region": dict(Counter(case["region"] for case in cases)),
            "split": dict(Counter(case["split"] for case in cases)),
            "category": dict(
                Counter(category for case in cases for category in case["categories"])
            ),
            "distanceClass": dict(Counter(case["distanceClass"] for case in cases)),
            "complexity": dict(Counter(case["complexity"] for case in cases)),
        },
        "routingExecutedDuringGeneration": False,
    }
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    args.receipt.write_bytes(canonical_bytes(receipt))
    print(
        json.dumps(
            {
                "phase": "corpus-frozen",
                "sha256": receipt["corpusSha256"],
                "cases": len(cases),
            }
        ),
        file=sys.stderr,
        flush=True,
    )
    print(json.dumps(receipt, indent=2), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("pbf", "database", "cache", "output", "receipt"):
        parser.add_argument(f"--{name}", type=Path, required=True)
    parser.add_argument(
        "--legacy", type=Path, default=Path("samples/routing-crossing-mass-v1.json")
    )
    parser.add_argument("--expected-source-sha256", required=True)
    parser.add_argument("--expected-database-sha256", required=True)
    parser.add_argument("--package-receipt", type=Path)
    parser.add_argument("--coverage", type=Path)
    parser.add_argument("--context-cache", type=Path)
    args = parser.parse_args()
    generate(args)


if __name__ == "__main__":
    # Match prepare_country's Windows libosmium lifetime workaround, only after
    # all synchronous writes, resources and output are complete.
    import os

    main()
    sys.stdout.flush()
    sys.stderr.flush()
    if os.name == "nt" and "osmium" in sys.modules:
        import ctypes

        # As in prepare_country.py, ExitProcess can wait forever in libosmium's
        # DLL finalization. All files and SQLite connections are closed above.
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        kernel32.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        kernel32.TerminateProcess.restype = ctypes.c_int
        kernel32.TerminateProcess(kernel32.GetCurrentProcess(), 0)
        raise OSError(ctypes.get_last_error(), "TerminateProcess failed")
    os._exit(0)
