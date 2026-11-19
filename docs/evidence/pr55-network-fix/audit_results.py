"""Audit Verify output independently with GEOS against original SQLite WKB."""

import json
import sqlite3
import sys
from pathlib import Path

from shapely.geometry import LineString
from shapely.ops import unary_union

sys.path.insert(0, str(Path("tools").resolve()))
from routing_crossing_pilot import audit, project, query

root = Path("artifacts/pr55-evidence")
summary = {}
for name, database, scenario in (
    ("victory-before", "before", "victory"),
    ("victory-after", "after", "victory"),
    ("victory-300-before", "before", "victory-300-true"),
    ("victory-300-after", "after", "victory-300-true"),
    ("victory-disabled", "after", "victory-300-false"),
    ("bridge-before", "before", "bridge"),
):
    result = json.loads((root / f"{name}.json").read_text())
    inputs = json.loads((root / f"{scenario}.json").read_text())
    checks = []
    connection = sqlite3.connect(root / database / "geodata.sqlite")
    for alternative in result["alternatives"]:
        path = LineString(
            [(p["Longitude"], p["Latitude"]) for p in alternative["geometry"]]
        )
        w, s, e, n = path.bounds
        features = query(connection, (w - 0.001, s - 0.001, e + 0.001, n + 0.001))
        # The 3 m road review can span a source-way boundary. Combine only
        # component membership explicitly retained from shared OSM node IDs.
        topology = dict(
            connection.execute("SELECT candidate_id,network_id FROM underpass_topology")
        )
        networks = {}
        for feature in features:
            if feature["id"] in topology:
                networks.setdefault(topology[feature["id"]], []).append(feature)
        features = [feature for feature in features if feature["id"] not in topology]
        for network_id, members in networks.items():
            features.append(
                dict(
                    id=network_id,
                    kind="PedestrianUnderpassCandidate",
                    geometry=unary_union([member["geometry"] for member in members]),
                    underpass_road_ids=sorted(
                        {
                            road
                            for member in members
                            for road in member.get("underpass_road_ids", [])
                        }
                    ),
                )
            )
        origin = path.coords[0]
        check = audit(
            project(path, origin),
            features,
            origin=origin,
            allow_underpasses=inputs["routingOptions"][
                "AllowExperimentalPedestrianUnderpasses"
            ],
        )
        if name in ("victory-after", "victory-300-after"):
            network = unary_union(
                [
                    project(f["geometry"], origin)
                    for f in features
                    if f["kind"] == "PedestrianUnderpassCandidate"
                ]
            )
            check["entireRouteInsideMappedNetwork1m"] = network.buffer(1).covers(
                project(path, origin)
            )
            if not check["entireRouteInsideMappedNetwork1m"]:
                raise ValueError("Route leaves the mapped underground network")
        if check["status"] != "valid":
            raise ValueError((name, check))
        checks.append(check)
    connection.close()
    summary[name] = {
        "status": result["status"],
        "error": result.get("error"),
        "alternatives": [
            {
                "lengthMetres": a["metrics"]["LengthMetres"],
                "roadCrossings": len(
                    a.get("roadCrossingEvents", a.get("roadContacts", []))
                ),
                "passages": a.get("underpassPassages"),
            }
            for a in result["alternatives"]
        ],
        "audits": checks,
    }
(root / "audit.json").write_text(json.dumps(summary, indent=2) + "\n")
print(json.dumps(summary, indent=2))
