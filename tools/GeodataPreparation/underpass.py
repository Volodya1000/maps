"""Conservative OSM evidence for pedestrian underpasses, not cable approval."""

import json
import re


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
        matches = [
            (rid, osm) for rid, osm, road in roads if crossing(tunnel, from_wkb(road))
        ]
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
        candidate_id = add("PedestrianUnderpassCandidate", tunnel, {}, source)
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
