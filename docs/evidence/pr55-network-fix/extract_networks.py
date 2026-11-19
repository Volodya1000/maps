import hashlib
import json
import sys
from pathlib import Path

import osmium

sys.path.insert(0, str(Path("tools/GeodataPreparation").resolve()))
from underpass import is_underpass

items = []
underpasses = []
with Path(
    "artifacts/osm/4463a38a3b68819c46fd9f89fc286a8f98afec480aff48d5240222254e45ec01.osm.pbf"
).open("rb") as stream:
    if (
        hashlib.file_digest(stream, "sha256").hexdigest()
        != "4463a38a3b68819c46fd9f89fc286a8f98afec480aff48d5240222254e45ec01"
    ):
        raise ValueError("Expected the pinned Belarus PBF")


class NetworkSources(osmium.SimpleHandler):
    def way(self, way):
        tags = dict(way.tags)
        points = [[n.lon, n.lat] for n in way.nodes]
        item = dict(
            id=way.id, tags=tags, nodes=[n.ref for n in way.nodes], coordinates=points
        )
        if is_underpass(tags):
            underpasses.append(item)
        if any(27.565 < x < 27.60 and 53.885 < y < 53.915 for x, y in points):
            items.append(item)


handler = NetworkSources()
handler.apply_file(
    "artifacts/osm/4463a38a3b68819c46fd9f89fc286a8f98afec480aff48d5240222254e45ec01.osm.pbf",
    locations=True,
    filters=[osmium.filter.KeyFilter("tunnel", "bridge")],
)
del handler
Path("artifacts/pr55-fast-networks.json").write_text(
    json.dumps(dict(ways=items, underpasses=underpasses), ensure_ascii=False),
    encoding="utf-8",
)
print("underpasses", len(underpasses))
