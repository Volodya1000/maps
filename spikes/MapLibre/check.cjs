const {chromium}=require('playwright');
const fs=require('fs');
const path=require('path');
const evidence=path.resolve(process.env.TERRAIN_EVIDENCE_DIR||path.join(__dirname,'../../docs/evidence'));fs.mkdirSync(evidence,{recursive:true});
(async()=>{
const browser=await chromium.launch({headless:true,args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
const page=await browser.newPage({viewport:{width:1200,height:800}});
const external=[],errors=[];
await page.route('**/*',async route=>{const u=new URL(route.request().url());if(u.protocol==='http:'&&u.hostname==='127.0.0.1')return route.continue();external.push(u.href);return route.abort();});
page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
await page.goto('http://127.0.0.1:8765');
await page.waitForFunction(()=>window.cableMap?.map.getTerrain()&&window.cableMap.map.loaded()&&window.cableMap.map.areTilesLoaded(),{timeout:30000});
const terrain=await page.evaluate(()=>{
const api=window.cableMap;
api.applySnapshot({points:[{id:'A',latitude:53.944,longitude:27.505},{id:'B',latitude:53.947,longitude:27.510}],routes:[{id:'r',geometry:[{latitude:53.944,longitude:27.505},{latitude:53.947,longitude:27.510}]}],selectedId:'r'});
const heights=[[27.505,53.944],[27.51,53.947],[27.515,53.95]].map(c=>api.map.queryTerrainElevation(c));
return {terrain:api.map.getTerrain(),pitch:api.map.getPitch(),heightsRelativeToCenterMetres:heights,points:api.markers.size,routeFeatures:api.map.getSource('routes')._data.features.length};
});
await page.screenshot({path:path.join(evidence,'maplibre-dem.png')});
const interactions=await page.evaluate(()=>{
const api=window.cableMap;api.markers.get('A').setLngLat([27.506,53.945]).fire('dragend');
api.map.fire('click',{lngLat:{lat:53.946,lng:27.507},originalEvent:{target:document.querySelector('#map')}});
api.applySnapshot({points:[],routes:[],selectedId:null});api.set3D(false);
return {events:api.emitted,remainingPoints:api.markers.size,terrainAfter2D:api.map.getTerrain()};
});
const report={scope:'Linux headless Chromium only; native C# WebView and target platforms unverified',terrain,interactions,externalRequests:external,errors};
fs.writeFileSync(path.join(evidence,'maplibre-check.json'),JSON.stringify(report,null,2)+'\n');console.log(JSON.stringify(report));
await browser.close();
if(external.length||errors.length||terrain.heightsRelativeToCenterMetres.some(h=>h===null)||new Set(terrain.heightsRelativeToCenterMetres).size<2||terrain.pitch===0)process.exitCode=1;
})();
