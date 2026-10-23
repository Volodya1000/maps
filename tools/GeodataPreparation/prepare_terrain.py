"""Prepare genuine Terrarium DEM tiles for the MapLibre compatibility experiment.
No height data is fabricated; a tile fails if any source pixel is unavailable.
Input COG files are explicitly supplied and checksum-recorded. Not yet consumed by the desktop UI.
"""
import argparse, hashlib, json, math
from pathlib import Path
import numpy as np
from PIL import Image
import rasterio
from rasterio.transform import from_bounds
from rasterio.warp import reproject, Resampling, transform_bounds

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dem',type=Path,nargs='+',required=True);p.add_argument('--bounds',type=float,nargs=4,required=True)
    p.add_argument('--output',type=Path,required=True);p.add_argument('--minzoom',type=int,default=12);p.add_argument('--maxzoom',type=int,default=14)
    a=p.parse_args();w,s,e,n=a.bounds
    a.output.mkdir(parents=True,exist_ok=True)
    datasets=[rasterio.open(f) for f in a.dem]
    def tile(lon,lat,z):
        return int((lon+180)/360*2**z),int((1-math.asinh(math.tan(math.radians(lat)))/math.pi)/2*2**z)
    hashes={}
    for z in range(a.minzoom,a.maxzoom+1):
        left,top=tile(w,n,z);right,bottom=tile(e,s,z)
        for x in range(left,right+1):
            for y in range(top,bottom+1):
                side=40075016.68557849/2**z
                b=(-20037508.342789244+x*side,20037508.342789244-(y+1)*side,
                   -20037508.342789244+(x+1)*side,20037508.342789244-y*side)
                affine=from_bounds(*b,256,256);heights=np.full((256,256),np.nan,dtype='float32')
                for src in datasets:
                    partial=np.full((256,256),np.nan,dtype='float32')
                    reproject(source=rasterio.band(src,1),destination=partial,src_transform=src.transform,src_crs=src.crs,
                              dst_transform=affine,dst_crs='EPSG:3857',dst_nodata=np.nan,resampling=Resampling.bilinear)
                    valid=np.isfinite(partial);heights[valid]=partial[valid]
                if not np.isfinite(heights).all():raise ValueError(f'Missing DEM coverage for tile {z}/{x}/{y}')
                encoded=np.round((heights.astype('float64')+32768)*256).astype('uint32')
                rgb=np.stack([(encoded>>16)&255,(encoded>>8)&255,encoded&255],axis=-1).astype('uint8')
                relative=f'{z}/{x}/{y}.png';out=a.output/relative;out.parent.mkdir(parents=True,exist_ok=True)
                Image.fromarray(rgb).save(out)
                hashes[relative]=hashlib.sha256(out.read_bytes()).hexdigest()
    (a.output/'manifest.json').write_text(json.dumps(dict(source='Copernicus DEM GLO-30',encoding='terrarium',bounds=a.bounds,
        crs='EPSG:3857',minzoom=a.minzoom,maxzoom=a.maxzoom,
        inputs=[dict(file=f.name,sha256=hashlib.sha256(f.read_bytes()).hexdigest()) for f in a.dem],tiles=hashes,
        attribution='produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved',
        license_url='https://spacedata.copernicus.eu/collections/copernicus-digital-elevation-model'),indent=2)+'\n')
    for src in datasets:src.close()
    print(f'Generated {len(hashes)} real DEM tiles')
if __name__=='__main__':main()
