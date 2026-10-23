#!/usr/bin/env python3
"""Build a bounded, real OSM package. Network is used only with --download.
Unknown terrain remains unknown. No synthetic objects, terrain or crossings are inserted.
"""
import argparse, gzip, hashlib, json, math, sqlite3, urllib.request
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
import xml.etree.ElementTree as ET
from shapely import make_valid, wkb
from shapely.geometry import LineString, Polygon, box, shape
from shapely.ops import polygonize, unary_union, transform

def tags(element):
    return {t.attrib['k']: t.attrib['v'] for t in element.findall('tag')}

def classify(t):
    if t.get('natural') == 'wetland': return 'Marsh'
    if t.get('natural') == 'water' or t.get('landuse') == 'reservoir':
        return 'River' if t.get('water') in ('river', 'stream', 'canal') else 'Lake'
    if t.get('waterway') in ('river', 'stream', 'canal', 'ditch', 'drain'): return 'River'
    if 'building' in t and t['building'] != 'no': return 'Building'
    if t.get('natural') == 'wood' or t.get('landuse') == 'forest': return 'Forest'
    if t.get('landuse') in ('farmland', 'meadow') or t.get('natural') == 'grassland': return 'Field'
    if t.get('man_made') == 'cutline': return 'Clearing'
    if t.get('bridge') not in (None, 'no') and 'highway' in t: return 'BridgeCandidate'
    if 'highway' in t and t['highway'] not in ('footway', 'path', 'steps', 'cycleway', 'pedestrian'): return 'Road'
    return None

def fetch(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'CableRoutePlanner-data-preparation/0.1 (offline engineering research)'})
    with urllib.request.urlopen(req, timeout=120) as response: return response.read()

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--osm', type=Path, required=True)
    ap.add_argument('--bounds', nargs=4, type=float, required=True, metavar=('W', 'S', 'E', 'N'))
    ap.add_argument('--output', type=Path, required=True)
    ap.add_argument('--download', action='store_true')
    ap.add_argument('--expected-sha256', help='Required when rebuilding from an existing source')
    ap.add_argument('--crossings', type=Path, help='Reviewed GeoJSON polygons with nonempty approval_id properties')
    args = ap.parse_args()
    source_url = 'https://api.openstreetmap.org/api/0.6/map?bbox=' + ','.join(map(str, args.bounds))
    if args.download:
        raw = fetch(source_url)
        args.osm.parent.mkdir(parents=True, exist_ok=True)
        if args.osm.suffix == '.gz':
            with args.osm.open('wb') as f: f.write(gzip.compress(raw, mtime=0))
        else: args.osm.write_bytes(raw)
    source_bytes = args.osm.read_bytes()
    source_hash = hashlib.sha256(source_bytes).hexdigest()
    if not args.download and not args.expected_sha256:
        ap.error('--expected-sha256 is required for existing source data')
    if args.expected_sha256 and args.expected_sha256.lower() != source_hash:
        ap.error('Source checksum mismatch')
    raw = gzip.decompress(source_bytes) if args.osm.suffix == '.gz' else source_bytes
    root = ET.fromstring(raw)
    nodes = {e.attrib['id']: (float(e.attrib['lon']), float(e.attrib['lat'])) for e in root.findall('node')}
    ways = {e.attrib['id']: e for e in root.findall('way')}
    def coords(way):
        refs = [n.attrib['ref'] for n in way.findall('nd')]
        if not all(n in nodes for n in refs): raise ValueError('Missing way nodes')
        return [nodes[n] for n in refs]
    extent = box(*args.bounds)
    center_lat = (args.bounds[1] + args.bounds[3]) / 2
    sx = 111195.0802335 * math.cos(math.radians(center_lat)); sy = 111195.0802335
    def buffer_metres(g, metres):
        local = transform(lambda x,y,z=None: (x*sx,y*sy), g)
        return transform(lambda x,y,z=None: (x/sx,y/sy), local.buffer(metres))
    features = []; consumed = set(); warnings = []
    for relation in root.findall('relation'):
        t = tags(relation); kind = classify(t)
        if t.get('type') != 'multipolygon' or not kind: continue
        members = [m for m in relation.findall('member') if m.attrib['type'] == 'way']
        if any(m.attrib['ref'] not in ways for m in members):
            # Incomplete obstacles must never silently disappear from a planning package.
            raise ValueError(f'Incomplete {kind} relation {relation.attrib["id"]}; download relation/full and merge before rebuilding')
        outer = [LineString(coords(ways[m.attrib['ref']])) for m in members if m.attrib.get('role') in ('outer', '')]
        inner = [LineString(coords(ways[m.attrib['ref']])) for m in members if m.attrib.get('role') == 'inner']
        polygons = list(polygonize(unary_union(outer)))
        if not polygons: raise ValueError('Unclosed multipolygon ' + relation.attrib['id'])
        g = unary_union(polygons)
        if inner: g = g.difference(unary_union(list(polygonize(unary_union(inner)))))
        features.append((kind, make_valid(g), t.get('name', ''), None, 'relation/' + relation.attrib['id']))
        consumed.update(m.attrib['ref'] for m in members)
    for way_id, way in ways.items():
        if way_id in consumed: continue
        t = tags(way); kind = classify(t)
        if not kind: continue
        c = coords(way)
        if len(c) < 2: continue
        if kind in ('Marsh','Lake','Forest','Field','Building') or kind == 'River' and t.get('waterway') is None:
            if c[0] != c[-1] or len(c) < 4: raise ValueError(f'Unclosed area way/{way_id}')
            g = make_valid(Polygon(c))
        else:
            g = LineString(c)
            if kind == 'River':
                try: width = max(6, float(t.get('width', '12')))
                except ValueError: width = 12
                g = buffer_metres(g, width / 2)
            if kind == 'Clearing': g = buffer_metres(g, 4)
        features.append((kind, g, t.get('name', ''), None, 'way/' + way_id))
    if args.crossings:
        for f in json.loads(args.crossings.read_text())['features']:
            approval = f['properties'].get('approval_id', '').strip()
            g = shape(f['geometry'])
            if not approval or g.geom_type not in ('Polygon','MultiPolygon') or not g.is_valid:
                raise ValueError('Crossings require valid polygons and an approval_id')
            features.append(('ApprovedCrossing', g, f['properties'].get('name',''), approval, 'reviewed'))
    args.output.mkdir(parents=True, exist_ok=True)
    db = args.output / 'geodata.sqlite'
    if db.exists(): db.unlink()
    con = sqlite3.connect(db)
    con.executescript('''CREATE TABLE features(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, wkb BLOB NOT NULL,
        name TEXT NOT NULL, approval_id TEXT, source_id TEXT NOT NULL);
        CREATE VIRTUAL TABLE feature_index USING rtree(id,minx,maxx,miny,maxy);''')
    counts = Counter()
    for kind, g, name, approval, source_id in features:
        if not g.intersects(extent): continue
        g = make_valid(g.intersection(extent))
        if g.is_empty: continue
        if not g.is_valid: raise ValueError('Invalid geometry: ' + source_id)
        cur = con.execute('INSERT INTO features(kind,wkb,name,approval_id,source_id) VALUES(?,?,?,?,?)',
                          (kind,wkb.dumps(g),name,approval,source_id))
        west,south,east,north = g.bounds
        con.execute('INSERT INTO feature_index VALUES(?,?,?,?,?)', (cur.lastrowid,west,east,south,north))
        counts[kind] += 1
    con.commit(); con.execute('VACUUM'); con.close()
    manifest = dict(schema_version=1, region_id='minsk-drozdy-novinki', coordinate_system='EPSG:4326',
        bounds=args.bounds, database_sha256=hashlib.sha256(db.read_bytes()).hexdigest(),
        attribution='© OpenStreetMap contributors · ODbL 1.0',
        status='OSM-only preliminary package. DEM and ESA WorldCover absent. Survey required.',
        sources=[dict(name='OpenStreetMap',url=source_url,sha256=source_hash,license='ODbL-1.0',
                      license_url='https://www.openstreetmap.org/copyright',
                      snapshot_timestamp=root.attrib.get('timestamp') or (root.find('meta').attrib.get('osm_base') if root.find('meta') is not None else None))],
        artifacts=['base-map-vectors','obstacles','spatial-index'], feature_counts=dict(sorted(counts.items())),
        terrain=False, worldcover=False, approved_crossings=counts['ApprovedCrossing'],
        limitations=['No construction authorization inferred from OSM bridge tags',
                     'Unmapped obstacles remain unknown', 'Line waterways use tagged width or conservative 12 m default',
                     'No elevation or landcover completeness claim'])
    (args.output/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps(dict(counts),ensure_ascii=False))

if __name__ == '__main__': main()
