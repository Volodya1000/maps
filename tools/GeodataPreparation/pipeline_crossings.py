"""Derive short, unapproved overhead gas spans at transverse road crossings."""

import math

from shapely import from_wkb
from shapely.affinity import scale
from shapely.geometry import LineString
from shapely.ops import substring

METRES_PER_DEGREE = 111195.0802335
CROSSING_SPAN_HALF_LENGTH_METRES = 12


def crossing_spans(pipeline, road):
    if pipeline.geom_type != "LineString" or not pipeline.crosses(road):
        return []
    intersection = pipeline.intersection(road)
    points = (
        [intersection]
        if intersection.geom_type == "Point"
        else [part for part in intersection.geoms if part.geom_type == "Point"]
        if hasattr(intersection, "geoms")
        else []
    )
    latitude = pipeline.centroid.y
    longitude_scale = METRES_PER_DEGREE * math.cos(math.radians(latitude))
    metres = scale(
        pipeline, xfact=longitude_scale, yfact=METRES_PER_DEGREE, origin=(0, 0)
    )
    spans = []
    for point in points:
        projected = scale(
            point, xfact=longitude_scale, yfact=METRES_PER_DEGREE, origin=(0, 0)
        )
        distance = metres.project(projected)
        before = substring(
            metres, max(0, distance - CROSSING_SPAN_HALF_LENGTH_METRES), distance
        )
        after = substring(
            metres,
            distance,
            min(metres.length, distance + CROSSING_SPAN_HALF_LENGTH_METRES),
        )
        if before.geom_type != "LineString" or after.geom_type != "LineString":
            continue
        span = LineString(list(before.coords) + list(after.coords)[1:])
        spans.append(
            scale(
                span,
                xfact=1 / longitude_scale,
                yfact=1 / METRES_PER_DEGREE,
                origin=(0, 0),
            )
        )
    return spans


def pipeline_road_candidates(connection):
    # Query a handful of pipes against the existing R-tree; do not load all roads.
    pipes = connection.execute(
        "SELECT source_id,wkb FROM features WHERE kind='OverheadGasPipeline'"
    ).fetchall()
    for source, raw in pipes:
        pipeline = from_wkb(raw)
        west, south, east, north = pipeline.bounds
        roads = connection.execute(
            """
            SELECT f.source_id,f.wkb FROM features f JOIN feature_index i ON i.id=f.id
            WHERE f.kind='Road' AND i.minx<=? AND i.maxx>=? AND i.miny<=? AND i.maxy>=?
            ORDER BY f.id
        """,
            (east, west, north, south),
        ).fetchall()
        for road_source, road_raw in roads:
            for index, geometry in enumerate(
                crossing_spans(pipeline, from_wkb(road_raw))
            ):
                yield geometry, f"gas-road:{source}:{road_source}:{index}"
