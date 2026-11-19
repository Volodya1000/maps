import json
from pathlib import Path

import osmium

data = json.loads(Path("artifacts/pr55-fast-networks.json").read_text(encoding="utf-8"))
targets = {
    n
    for w in data["underpasses"]
    if any(27.565 < x < 27.60 and 53.885 < y < 53.915 for x, y in w["coordinates"])
    for n in w["nodes"]
}
surface = []


class SurfaceSources(osmium.SimpleHandler):
    def way(self, w):
        t = dict(w.tags)
        if t.get("tunnel", "no") != "no" or t.get("location") == "underground":
            return
        nodes = [n.ref for n in w.nodes]
        if targets.intersection(nodes):
            surface.append(dict(id=w.id, tags=t, nodes=nodes))


handler = SurfaceSources()
handler.apply_file(
    "artifacts/osm/4463a38a3b68819c46fd9f89fc286a8f98afec480aff48d5240222254e45ec01.osm.pbf",
    filters=[
        osmium.filter.TagFilter(
            ("highway", "footway"),
            ("highway", "path"),
            ("highway", "pedestrian"),
            ("highway", "steps"),
        )
    ],
)
del handler
Path("artifacts/pr55-surface.json").write_text(
    json.dumps(surface, ensure_ascii=False), encoding="utf-8"
)
print(len(surface), flush=True)
