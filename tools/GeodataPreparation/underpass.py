"""Conservative OSM evidence for pedestrian underpasses, not cable approval."""

from __future__ import annotations

import json
import re
from itertools import combinations

if __package__:
    from .feature_classification import UNDERPASS_KIND
else:
    from feature_classification import UNDERPASS_KIND


def _layer(value):
    if not re.fullmatch(r"-?[0-9]+", value):
        return None
    return int(value)


def is_underpass(tags):
    level = _layer(tags.get("layer", ""))
    return (
        tags.get("highway") in ("footway", "path", "pedestrian", "steps")
        and tags.get("tunnel") == "yes"
        and level is not None
        and level < 0
        and tags.get("footway") != "crossing"
        and "crossing" not in tags
        and tags.get("bridge", "no") == "no"
        and tags.get("covered", "yes") != "no"
        and tags.get("location", "underground") == "underground"
    )


def compatible_road(tags, tunnel_layer):
    level = _layer(tags.get("layer", "0"))
    return (
        tags.get("highway")
        in (
            "motorway",
            "trunk",
            "primary",
            "secondary",
            "tertiary",
            "unclassified",
            "residential",
            "living_street",
            "service",
            "motorway_link",
            "trunk_link",
            "primary_link",
            "secondary_link",
            "tertiary_link",
        )
        and level is not None
        and level > tunnel_layer
        and tags.get("tunnel", "no") == "no"
        and tags.get("bridge", "no") == "no"
        and "location" not in tags
    )


def crossing(tunnel, road):
    if tunnel.geom_type != "LineString" or road.geom_type != "LineString":
        return False
    if not tunnel.is_simple or not road.is_simple or not tunnel.crosses(road):
        return False
    contacts = tunnel.intersection(road)
    return (
        contacts.geom_type in ("Point", "MultiPoint")
        and not contacts.intersects(tunnel.boundary)
        and not contacts.intersects(road.boundary)
    )


def _junction_crossing(span, center, road):
    from shapely.geometry import LineString

    if not crossing(span, road):
        return False
    origin = center.coords[0]
    road_rays = []
    coordinates = list(road.coords)
    for first, second in zip(coordinates, coordinates[1:], strict=False):
        if LineString([first, second]).covers(center):
            for point in (first, second):
                ray = (point[0] - origin[0], point[1] - origin[1])
                if ray != (0, 0):
                    road_rays.append(ray)
    if len(road_rays) != 2:
        return False
    tunnel_rays = [
        (point[0] - origin[0], point[1] - origin[1])
        for point in (span.coords[0], span.coords[-1])
    ]

    def cross(first, second):
        return first[0] * second[1] - first[1] * second[0]

    if any(
        cross(first, second) == 0 and first[0] * second[0] + first[1] * second[1] > 0
        for first in road_rays
        for second in tunnel_rays
    ):
        return False
    first, second = road_rays
    turn = cross(first, second)
    if turn == 0 and first[0] * second[0] + first[1] * second[1] >= 0:
        return False

    # At a bend, the road's outgoing rays divide the local plane into two
    # sectors. A transverse passage must enter one and leave through the other;
    # GEOS crosses alone also accepts a V-shaped tangent at an interior point.
    def inside(ray):
        left = cross(first, ray) > 0
        right = cross(ray, second) > 0
        if turn > 0:
            return left and right
        if turn < 0:
            return left or right
        return left

    return inside(tunnel_rays[0]) != inside(tunnel_rays[1])


def create_staging(connection):
    connection.executescript("""
        CREATE TEMP TABLE underpass_sources(source TEXT PRIMARY KEY,wkb BLOB,layer INTEGER);
        CREATE TEMP TABLE road_levels(id INTEGER PRIMARY KEY,layer INTEGER);
        CREATE TABLE underpass_roads(
            candidate_id INTEGER NOT NULL REFERENCES features(id),
            road_id INTEGER NOT NULL REFERENCES features(id),
            tunnel_osm_id TEXT NOT NULL,road_osm_id TEXT NOT NULL,
            PRIMARY KEY(candidate_id,road_id));
        CREATE TEMP TABLE underpass_source_nodes(source TEXT PRIMARY KEY,nodes TEXT);
        CREATE TEMP TABLE underpass_surface_nodes(node_id INTEGER PRIMARY KEY);
        CREATE TABLE underpass_topology(candidate_id INTEGER PRIMARY KEY REFERENCES features(id),
            network_id INTEGER NOT NULL,node_ids TEXT NOT NULL,entrance_node_ids TEXT NOT NULL);
    """)


def stage(connection, kind, geometry, tags, source, feature_id=None):
    from shapely import to_wkb

    if is_underpass(tags):
        connection.execute(
            "INSERT OR REPLACE INTO underpass_sources VALUES(?,?,?)",
            (source, to_wkb(geometry), int(tags["layer"])),
        )
    road_layer = _layer(tags.get("layer", "0"))
    if (
        kind == "Road"
        and road_layer is not None
        and compatible_road(tags, road_layer - 1)
    ):
        connection.execute(
            "INSERT INTO road_levels VALUES(?,?)",
            (feature_id, int(tags.get("layer", "0"))),
        )


def import_candidates(connection, add):
    from shapely import from_wkb
    from shapely.geometry import LineString, Point

    sources = connection.execute(
        "SELECT * FROM underpass_sources ORDER BY source"
    ).fetchall()
    nodes = {
        source: json.loads(raw)
        for source, raw in connection.execute("SELECT * FROM underpass_source_nodes")
    }
    # Identity and layer, never proximity, establish connectivity.
    groups = {source: source for source, _, _ in sources}

    def root(source):
        while groups[source] != source:
            source = groups[source]
        return source

    owners = {}
    for source, _, level in sources:
        for node in nodes.get(source, []):
            key = (level, node)
            if key in owners:
                groups[root(source)] = root(owners[key])
            owners[key] = source
    # A transverse passage can be split at a junction on the road axis. Build
    # only two-edge paths through the same source node and layer, never by XY
    # proximity. Such a path proves a crossing even at an odd-degree T junction.
    neighbors = {}
    centers = {}
    for source, raw, level in sources:
        refs = nodes.get(source, [])
        geometry = from_wkb(raw)
        if not refs or geometry.geom_type != "LineString" or not geometry.is_simple:
            continue
        coordinates = list(geometry.coords)
        if len(refs) != len(coordinates):
            raise ValueError(
                f"{source} node_ids count {len(refs)} differs from coordinate count {len(coordinates)}"
            )
        for index, node in enumerate(refs):
            key = (level, node)
            center = coordinates[index]
            if key in centers and centers[key] != center:
                raise ValueError(
                    f"node/{node} coordinates {center!r} differ from shared coordinates {centers[key]!r}"
                )
            centers[key] = center
            adjacent = neighbors.setdefault(key, {})
            for other in (index - 1, index + 1):
                if 0 <= other < len(refs):
                    adjacent[(source, refs[other])] = coordinates[other]
    junctions = {}
    for key, adjacent in neighbors.items():
        for (first, a), (second, b) in combinations(adjacent.items(), 2):
            center = centers[key]
            span = LineString([a, center, b])
            for source in dict.fromkeys((first[0], second[0])):
                junctions.setdefault(source, []).append((span, Point(center)))
    matches_by_source = {}
    for source, raw, level in sources:
        tunnel = from_wkb(raw)
        west, south, east, north = tunnel.bounds
        roads = connection.execute(
            """
            SELECT f.id,f.source_id,f.wkb FROM features f
            JOIN feature_index i ON i.id=f.id JOIN road_levels l ON l.id=f.id
            WHERE l.layer>? AND i.minx<=? AND i.maxx>=? AND i.miny<=? AND i.maxy>=?
            ORDER BY f.id
        """,
            (level, east, west, north, south),
        ).fetchall()
        matches = []
        for rid, osm, raw_road in roads:
            road = from_wkb(raw_road)
            if crossing(tunnel, road) or any(
                _junction_crossing(span, center, road)
                for span, center in junctions.get(source, [])
            ):
                matches.append((rid, osm))
        matches_by_source[source] = matches
    active = {root(source) for source, matches in matches_by_source.items() if matches}
    degrees = {}
    for source, _, _level in sources:
        refs = nodes.get(source, [])
        for a, b in zip(refs, refs[1:], strict=False):
            for node in (a, b):
                key = (root(source), node)
                degrees[key] = degrees.get(key, 0) + 1
    surface_nodes = {
        row[0]
        for row in connection.execute("SELECT node_id FROM underpass_surface_nodes")
    }
    network_ids = {}
    for source, raw, _ in sources:
        group = root(source)
        if group not in active:
            continue
        tunnel = from_wkb(raw)
        candidate_id = add(UNDERPASS_KIND, tunnel, {}, source)
        if source in nodes:
            network_id = network_ids.setdefault(group, candidate_id)
            refs = nodes[source]
            entrances = [
                node
                for node in refs
                if degrees[(group, node)] == 1 and node in surface_nodes
            ]
            connection.execute(
                "INSERT INTO underpass_topology VALUES(?,?,?,?)",
                (candidate_id, network_id, json.dumps(refs), json.dumps(entrances)),
            )
        for road_id, road_osm in matches_by_source[source]:
            connection.execute(
                "INSERT INTO underpass_roads VALUES(?,?,?,?)",
                (candidate_id, road_id, source, road_osm),
            )
