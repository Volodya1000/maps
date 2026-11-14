"""Conservative OSM evidence for pedestrian underpasses, not cable approval."""

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
    return tunnel.intersection(road).geom_type == "Point"


def create_staging(connection):
    connection.executescript("""
        CREATE TEMP TABLE underpass_sources(source TEXT PRIMARY KEY,wkb BLOB,layer INTEGER);
        CREATE TEMP TABLE road_levels(id INTEGER PRIMARY KEY,layer INTEGER);
        CREATE TABLE underpass_roads(
            candidate_id INTEGER NOT NULL REFERENCES features(id),
            road_id INTEGER NOT NULL REFERENCES features(id),
            tunnel_osm_id TEXT NOT NULL,road_osm_id TEXT NOT NULL,
            PRIMARY KEY(candidate_id,road_id));
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

    for source, raw, level in connection.execute(
        "SELECT * FROM underpass_sources ORDER BY source"
    ).fetchall():
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
        if not matches:
            continue
        candidate_id = add("PedestrianUnderpassCandidate", tunnel, {}, source)
        for road_id, road_osm in matches:
            connection.execute(
                "INSERT INTO underpass_roads VALUES(?,?,?,?)",
                (candidate_id, road_id, source, road_osm),
            )
