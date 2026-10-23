"""Download bbox and complete relevant multipolygons using official OSM API."""

import argparse
import gzip
import json
import xml.etree.ElementTree as ET
from pathlib import Path
from prepare import classify, tags, fetch

p = argparse.ArgumentParser()
p.add_argument("--bounds", nargs=4, type=float, required=True)
p.add_argument("--output", type=Path, required=True)
p.add_argument("--existing", type=Path)
a = p.parse_args()
url = "https://api.openstreetmap.org/api/0.6/map?bbox=" + ",".join(map(str, a.bounds))
r = ET.fromstring(a.existing.read_bytes() if a.existing else fetch(url))
known = {(e.tag, e.attrib.get("id")) for e in r}
urls = [url]
for rel in list(r.findall("relation")):
    if tags(rel).get("type") != "multipolygon" or not classify(tags(rel)):
        continue
    if all((m.attrib["type"], m.attrib["ref"]) in known for m in rel.findall("member")):
        continue
    full_url = (
        "https://api.openstreetmap.org/api/0.6/relation/" + rel.attrib["id"] + "/full"
    )
    full = ET.fromstring(fetch(full_url))
    urls.append(full_url)
    for e in full:
        key = (e.tag, e.attrib.get("id"))
        if key not in known:
            r.append(e)
            known.add(key)
    print("Completed relation", rel.attrib["id"])
a.output.parent.mkdir(parents=True, exist_ok=True)
a.output.write_bytes(
    gzip.compress(ET.tostring(r, encoding="utf-8", xml_declaration=True), mtime=0)
)
a.output.with_suffix(".sources.json").write_text(json.dumps(urls, indent=2) + "\n")
