import base64
import json
import sqlite3
from pathlib import Path

c = sqlite3.connect("artifacts/pr55-evidence/after/geodata.sqlite")


def export(bounds, filename, network_source=None):
    features = []
    for fid, kind, wkb, source in c.execute(
        "SELECT f.id,f.kind,f.wkb,f.source_id FROM features f JOIN feature_index i ON i.id=f.id WHERE i.minx<? AND i.maxx>? AND i.miny<? AND i.maxy>? ORDER BY f.id",
        bounds,
    ):
        topology = c.execute(
            "SELECT network_id,node_ids,entrance_node_ids FROM underpass_topology WHERE candidate_id=?",
            (fid,),
        ).fetchone()
        features.append(
            dict(
                Id=fid,
                Kind=kind,
                Wkb=base64.b64encode(wkb).decode(),
                Name=source,
                UnderpassRoadIds=[
                    row[0]
                    for row in c.execute(
                        "SELECT road_id FROM underpass_roads WHERE candidate_id=? ORDER BY road_id",
                        (fid,),
                    )
                ],
                UnderpassTopology=None
                if topology is None
                else dict(
                    NetworkId=topology[0],
                    NodeIds=json.loads(topology[1]),
                    EntranceNodeIds=json.loads(topology[2]),
                ),
            )
        )
    if network_source:
        network = next(
            feature["UnderpassTopology"]["NetworkId"]
            for feature in features
            if feature["Name"] == network_source
        )
        features = [
            feature
            for feature in features
            if feature["Kind"] != "PedestrianUnderpassCandidate"
            or feature["UnderpassTopology"]["NetworkId"] == network
        ]
    path = Path("tests/CableRoutePlanner.Tests/Fixtures/Underpasses") / filename
    path.parent.mkdir(exist_ok=True)
    path.write_text(json.dumps(features, indent=2) + "\n")


export((27.577, 27.573, 53.910, 53.907), "victory.json", "way/25463811")
export((27.5885, 27.5835, 53.893, 53.8895), "proletarskaya.json")
c.close()
