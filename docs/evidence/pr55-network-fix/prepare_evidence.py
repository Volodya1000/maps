import hashlib
import json
import sqlite3
import sys
from pathlib import Path

import osmium
from shapely import from_wkb, to_wkb
from shapely.geometry import LineString

sys.path.insert(0, str(Path("tools/GeodataPreparation").resolve()))
from underpass import create_staging, import_candidates, stage

root = Path("artifacts/pr55-evidence")
root.mkdir(exist_ok=True)
source = sqlite3.connect("artifacts/pr54-belarus.sqlite")
with Path("artifacts/pr54-belarus.sqlite").open("rb") as stream:
    if (
        hashlib.file_digest(stream, "sha256").hexdigest()
        != "f584e0c9963692b31b0f2b6322dc810641729bbabcc410626faf8ea70b703493"
    ):
        raise ValueError("Expected the pinned SQLite v3 Belarus source")
rows = source.execute(
    "SELECT f.* FROM features f JOIN feature_index i ON i.id=f.id WHERE i.minx<27.60 AND i.maxx>27.565 AND i.miny<53.915 AND i.maxy>53.885"
).fetchall()
road_ids = {
    int(r[5].split("/")[1]) for r in rows if r[1] == "Road" and r[5].startswith("way/")
}
road_tags = {}


class RoadTags(osmium.SimpleHandler):
    def way(self, way):
        road_tags[way.id] = dict(way.tags)


handler = RoadTags()
handler.apply_file(
    "artifacts/osm/4463a38a3b68819c46fd9f89fc286a8f98afec480aff48d5240222254e45ec01.osm.pbf",
    filters=[osmium.filter.IdFilter(road_ids)],
)
del handler
for version in ("before", "after"):
    folder = root / version
    folder.mkdir(exist_ok=True)
    db = folder / "geodata.sqlite"
    if db.exists():
        db.unlink()
    c = sqlite3.connect(db)
    c.executescript(
        "CREATE TABLE features(id INTEGER PRIMARY KEY,kind,wkb,name,approval_id,source_id); CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);"
    )
    create_staging(c)
    for row in rows:
        if version == "after" and row[1] == "PedestrianUnderpassCandidate":
            continue
        c.execute("INSERT INTO features VALUES(?,?,?,?,?,?)", row)
        w, s, e, n = from_wkb(row[2]).bounds
        c.execute("INSERT INTO feature_index VALUES(?,?,?,?,?)", (row[0], w, e, s, n))
        if row[1] == "Road":
            stage(
                c,
                row[1],
                from_wkb(row[2]),
                road_tags.get(int(row[5].split("/")[1]), {}),
                row[5],
                row[0],
            )
    if version == "before":
        ids = {r[0] for r in rows}
        for row in source.execute("SELECT * FROM underpass_roads"):
            if row[0] in ids and row[1] in ids:
                c.execute("INSERT INTO underpass_roads VALUES(?,?,?,?)", row)
    else:
        surface = json.loads(
            Path("artifacts/pr55-surface.json").read_text(encoding="utf-8")
        )
        c.executemany(
            "INSERT OR IGNORE INTO underpass_surface_nodes VALUES(?)",
            ((n,) for w in surface for n in w["nodes"]),
        )
        ways = json.loads(
            Path("artifacts/pr55-fast-networks.json").read_text(encoding="utf-8")
        )["underpasses"]
        for w in ways:
            if not any(
                27.565 < x < 27.60 and 53.885 < y < 53.915 for x, y in w["coordinates"]
            ):
                continue
            stage(c, None, LineString(w["coordinates"]), w["tags"], f"way/{w['id']}")
            c.execute(
                "INSERT INTO underpass_source_nodes VALUES(?,?)",
                (f"way/{w['id']}", json.dumps(w["nodes"])),
            )

        def add(kind, geometry, tags, source_id, connection=c):
            cursor = connection.execute(
                "INSERT INTO features(kind,wkb,name,source_id) VALUES(?,?,?,?)",
                (kind, to_wkb(geometry), "", source_id),
            )
            fid = cursor.lastrowid
            w, s, e, n = geometry.bounds
            connection.execute(
                "INSERT INTO feature_index VALUES(?,?,?,?,?)", (fid, w, e, s, n)
            )
            return fid

        import_candidates(c, add)
    c.commit()
    print(
        version,
        c.execute(
            "SELECT count(*) FROM features WHERE kind='PedestrianUnderpassCandidate'"
        ).fetchone(),
        flush=True,
    )
    counts = dict(c.execute("SELECT kind,count(*) FROM features GROUP BY kind"))
    c.close()
    manifest = json.loads(
        Path("artifacts/pr54-belarus-data/manifest.json").read_text(encoding="utf-8")
    )
    manifest["feature_counts"] = counts
    manifest["database_sha256"] = hashlib.sha256(db.read_bytes()).hexdigest()
    (folder / "manifest.json").write_text(json.dumps(manifest))
scenario = json.loads(
    Path("docs/evidence/routing-underpass-v3/scenario-true.json").read_text()
)
scenario["sourceDatabaseSha256"] = scenario.pop("databaseSha256")
scenario["id"] = "victory-nw-se"
scenario["points"] = [
    dict(Latitude=53.9089564, Longitude=27.5742584),
    dict(Latitude=53.9081863, Longitude=27.5759061),
]
(root / "victory.json").write_text(json.dumps(scenario))
scenario["id"] = "footbridge-25381163"
scenario["points"] = [
    dict(Latitude=53.891348, Longitude=27.585238),
    dict(Latitude=53.890267, Longitude=27.586239),
]
(root / "bridge.json").write_text(json.dumps(scenario))
source.close()
