"""Rebuild the included OSM snapshot, with no network access."""
import argparse,json,subprocess,sys
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args()
root=Path(__file__).resolve().parents[2];sample=root/'samples/real-geodata-fixtures/minsk'
manifest=json.loads((sample/'manifest.json').read_text())
subprocess.run([sys.executable,str(Path(__file__).with_name('prepare.py')),'--osm',str(sample/'source.osm.gz'),
    '--expected-sha256',manifest['sources'][0]['sha256'],'--bounds',*map(str,manifest['bounds']),'--output',str(a.output)],check=True)
rebuilt=json.loads((a.output/'manifest.json').read_text())
assert rebuilt['feature_counts']==manifest['feature_counts'],'Feature counts changed'
print('Source checksum and rebuilt feature counts verified. SQLite byte layout may vary across SQLite/GEOS versions.')
