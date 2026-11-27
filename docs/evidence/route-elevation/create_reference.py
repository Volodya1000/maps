"""Reproduce the summit comparison from native Copernicus pixels; no app algorithms imported.

Run with uv run --project tools/GeodataPreparation --with geographiclib==2.1 python
docs/evidence/route-elevation/create_reference.py artifacts/dem/copernicus-N53E027.tif
"""

import hashlib
import json
import math
import sys
from pathlib import Path

import rasterio
from geographiclib.geodesic import Geodesic

START = (53.8486702, 27.0655127)  # OSM peak node 1372197268, version 23.
END = (53.8400, 27.0655127)
SOURCE_URL = (
    "https://copernicus-dem-30m.s3.amazonaws.com/"
    "Copernicus_DSM_COG_10_N53_00_E027_00_DEM/"
    "Copernicus_DSM_COG_10_N53_00_E027_00_DEM.tif"
)


def bilinear(dataset, latitude, longitude):
    column, row = ~dataset.transform * (longitude, latitude)
    column -= 0.5
    row -= 0.5
    left, top = math.floor(column), math.floor(row)
    values = dataset.read(1, window=((top, top + 2), (left, left + 2)), masked=True)
    if values.shape != (2, 2) or values.mask.any():
        raise ValueError("Missing native DEM sample")
    values = values.astype("float64")
    u, v = column - left, row - top
    return float(
        (1 - u) * (1 - v) * values[0, 0]
        + u * (1 - v) * values[0, 1]
        + (1 - u) * v * values[1, 0]
        + u * v * values[1, 1]
    )


def reference(dataset, spacing):
    # Conservative angular bound matches station placement, not the distance integral.
    bound = 6378137 / (1 - 1 / 298.257223563) * math.pi / 180
    count = math.ceil(bound * abs(END[0] - START[0]) / spacing)
    samples = []
    horizontal = surface = ascent = descent = 0.0
    for index in range(count + 1):
        latitude = START[0] + (END[0] - START[0]) * index / count
        longitude = START[1] + (END[1] - START[1]) * index / count
        height = bilinear(dataset, latitude, longitude)
        if samples:
            previous = samples[-1]
            distance = Geodesic.WGS84.Inverse(
                previous["latitude"], previous["longitude"], latitude, longitude
            )["s12"]
            difference = height - previous["height_metres"]
            horizontal += distance
            surface += math.hypot(distance, difference)
            ascent += max(0, difference)
            descent += max(0, -difference)
        samples.append(
            {
                "latitude": latitude,
                "longitude": longitude,
                "height_metres": height,
                "distance_metres": horizontal,
            }
        )
    return {
        "maximum_spacing_metres": spacing,
        "samples": samples,
        "horizontal_metres": horizontal,
        "surface_metres": surface,
        "correction_metres": surface - horizontal,
        "correction_percent": 100 * (surface / horizontal - 1),
        "ascent_metres": ascent,
        "descent_metres": descent,
        "cable_with_ten_percent_reserve_metres": surface * 1.1,
    }


def main():
    source = Path(sys.argv[1])
    with rasterio.open(source) as dataset:
        result = {
            "source_url": SOURCE_URL,
            "source_release": "AWS GLO-30 2021",
            "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "crs": str(dataset.crs),
            "vertical_datum": "EGM2008",
            "native_affine": list(dataset.transform)[:6],
            "native_size": [dataset.width, dataset.height],
            "reference_engine": "GeographicLib Python 2.1 WGS84 Inverse + Rasterio 1.4.3 native bilinear",
            "peak_osm_url": "https://www.openstreetmap.org/node/1372197268",
            "peak_osm_version": 23,
            "published_peak_metres": 345,
            "interpretation": "DSM surface estimate along a test line, not a surveyed cable design",
            "baseline": reference(dataset, 15),
            "refined": reference(dataset, 7.5),
        }
    output = Path(
        "tests/CableRoutePlanner.Tests/Fixtures/Elevation/summit-reference.json"
    )
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    summary = {
        key: value for key, value in result["baseline"].items() if key != "samples"
    }
    print(json.dumps(summary, indent=2))
    print("refined correction:", result["refined"]["correction_metres"])


if __name__ == "__main__":
    main()
