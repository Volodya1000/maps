'use strict';
const emitted=[];
function emit(event){
  emitted.push(event);
  if(window.chrome?.webview) window.chrome.webview.postMessage(event);
  if(window.cableNative?.postMessage) window.cableNative.postMessage(JSON.stringify(event));
}
const map=new maplibregl.Map({container:'map',center:[27.505,53.944],zoom:14,pitch:60,bearing:-20,
  minZoom:12,maxZoom:17,maxBounds:[[27.48,53.93],[27.53,53.96]],attributionControl:true,
  style:{version:8,sources:{
    osm:{type:'geojson',data:'map.geojson',attribution:'© OpenStreetMap contributors · ODbL 1.0'},
    terrain:{type:'raster-dem',tiles:[`${location.origin}/terrain/{z}/{x}/{y}.png`],encoding:'terrarium',tileSize:256,minzoom:12,maxzoom:14,
      bounds:[27.48,53.93,27.53,53.96],attribution:'Copernicus DEM GLO-30 · © DLR / Airbus · EU / ESA'}},
    layers:[{id:'background',type:'background',paint:{'background-color':'#f3f0e5'}},
      {id:'land',type:'fill',source:'osm',filter:['in',['geometry-type'],['literal',['Polygon','MultiPolygon']]],paint:{'fill-color':['match',['get','kind'],'Forest','#bdd5b2','Field','#e6deb1','River','#93c2d9','Lake','#93c2d9','Marsh','#8daf9c','Building','#c7c1b5','#e6e3d7']}},
      {id:'roads',type:'line',source:'osm',filter:['in',['get','kind'],['literal',['Road','BridgeCandidate']]],paint:{'line-color':'#bfae8e','line-width':2}}]}});
map.addControl(new maplibregl.NavigationControl({visualizePitch:true}),'top-right');
const markers=new Map();let threeD=true;
function coordinate(value){
  if(!Number.isFinite(value.latitude)||!Number.isFinite(value.longitude)||Math.abs(value.latitude)>90||Math.abs(value.longitude)>180)throw new Error('Invalid coordinate');
  return [value.longitude,value.latitude];
}
function applySnapshot(snapshot){
  for(const marker of markers.values())marker.remove();markers.clear();
  for(const [index,point] of snapshot.points.entries()){
    const element=document.createElement('div');element.className='point';element.textContent=String(index+1);
    const marker=new maplibregl.Marker({element,draggable:true}).setLngLat(coordinate(point)).addTo(map);
    marker.on('dragend',()=>{const c=marker.getLngLat();emit({type:'pointMoved',id:point.id,latitude:c.lat,longitude:c.lng});});
    markers.set(point.id,marker);
  }
  const features=snapshot.routes.map(route=>({type:'Feature',properties:{selected:route.id===snapshot.selectedId},geometry:{type:'LineString',coordinates:route.geometry.map(coordinate)}}));
  const data={type:'FeatureCollection',features};
  if(map.getSource('routes'))map.getSource('routes').setData(data);
  else{
    map.addSource('routes',{type:'geojson',data});
    map.addLayer({id:'routes',type:'line',source:'routes',paint:{'line-color':['case',['get','selected'],'#1672d4','#8a98a6'],'line-width':['case',['get','selected'],5,3]}});
  }
}
function set3D(enabled){threeD=enabled;map.setTerrain(enabled?{source:'terrain',exaggeration:1}:null);map.setPitch(enabled?60:0);}
map.on('click',e=>emit({type:'pointAdded',latitude:e.lngLat.lat,longitude:e.lngLat.lng}));
map.on('load',()=>{map.setTerrain({source:'terrain',exaggeration:1});document.getElementById('status').textContent='Загружены локальная карта и DEM';emit({type:'ready'});});
map.on('error',e=>{document.getElementById('status').textContent=e.error.message;emit({type:'error',message:e.error.message});});
document.getElementById('mode').addEventListener('click',()=>set3D(!threeD));
window.cableMap={map,applySnapshot,set3D,emitted,markers};
