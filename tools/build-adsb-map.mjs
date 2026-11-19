// Usage: node build-adsb-map.mjs <source directory> <AdsbMap.json.gz>
// Natural Earth v5.1.2 GeoJSON and OurAirports CSV. See README for sources.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';

const [source, output] = process.argv.slice(2);
const read = name => readFileSync(join(source, name), 'utf8');
const features = name => JSON.parse(read(name + '.geojson')).features;
const round = value => Math.round(Number(value) * 100000) / 100000;
const Lines = [], Cities = [], Airports = [], Counties = [];
function line(points, Level) {
  if (points.length > 1) Lines.push({ Level, Points: points.flatMap(p => p.slice(0, 2).map(round)) });
}
function outlines(name, level) {
  for (const {geometry: g} of features(name)) {
    for (const polygon of g.type === 'Polygon' ? [g.coordinates] : g.coordinates) line(polygon[0], level);
  }
}
function paths(name, level) {
  for (const {geometry: g} of features(name)) {
    for (const path of g.type === 'LineString' ? [g.coordinates] : g.coordinates) line(path, level);
  }
}
outlines('ne_110m_admin_0_countries', 0);
paths('ne_50m_admin_1_states_provinces_lines', 1);
paths('rivers', 2);
outlines('lakes', 3);
// Douglas-Peucker reduces TIGER road detail for regional display, retaining bend vertices.
function simplify(points, tolerance) {
  if (points.length<=2) return points;
  const end = points.length-1, keep = new Set([0,end]), stack = [[0,end]];
  while (stack.length) {
    const [a,b] = stack.pop(), [x,y] = points[a];
    const dx = points[b][0]-x, dy = points[b][1]-y, length = dx*dx+dy*dy;
    let max = tolerance*tolerance, best = -1;
    for (let i=a+1; i<b; i++) {
      const px=points[i][0]-x, py=points[i][1]-y;
      const t=length ? Math.max(0,Math.min(1,(px*dx+py*dy)/length)) : 0;
      const distance=(px-t*dx)**2+(py-t*dy)**2;
      if (distance>max) { max=distance; best=i; }
    }
    if (best>=0) { keep.add(best); stack.push([a,best],[best,b]); }
  }
  return points.filter((_,i)=>keep.has(i));
}
function shapeLines(path, type, level) {
  const shape = readFileSync(join(source,path));
  if (shape.readInt32LE(32)!==type) throw Error('Unexpected shape type: '+path);
  for (let record=100; record<shape.length;) {
    const data=record+8;
    record=data+2*shape.readUInt32BE(record+4);
    if (shape.readUInt32LE(data)!==type) continue;
    const parts=shape.readUInt32LE(data+36), count=shape.readUInt32LE(data+40);
    const coords=data+44+4*parts;
    for (let p=0; p<parts; p++) {
      const start=shape.readUInt32LE(data+44+4*p);
      const end=p+1===parts ? count : shape.readUInt32LE(data+48+4*p);
      const points=[];
      for (let i=start; i<end; i++) points.push([shape.readDoubleLE(coords+i*16),shape.readDoubleLE(coords+i*16+8)]);
      line(level===5 ? simplify(points,.0005) : points,level);
    }
  }
}
shapeLines('counties/cb_2024_us_county_5m.shp',5,4);
shapeLines('roads/tl_2024_us_primaryroads.shp',3,5);
for (const {geometry, properties: p} of features('places')) {
  Cities.push({ Name: p.name, Lon: round(geometry.coordinates[0]), Lat: round(geometry.coordinates[1]), Rank: p.scalerank });
}
Cities.sort((a,b) => a.Rank - b.Rank);
const named = new Map();
for (const city of Cities) {
  const key = city.Name.toLowerCase();
  if (!named.has(key)) named.set(key, []);
  named.get(key).push(city);
}
const gazetteer = read('us-places/2024_Gaz_place_national.txt').trim().split(/\r?\n/);
const fields = gazetteer.shift().trim().split('\t');
for (const line of gazetteer) {
  const values = line.trim().split('\t');
  const get = name => values[fields.indexOf(name)];
  const Name = get('NAME').replace(/ (?:CDP|city|village|town|borough|municipality)$/,'');
  const Lat = round(get('INTPTLAT')), Lon = round(get('INTPTLONG'));
  if (named.get(Name.toLowerCase())?.some(c=>Math.abs(c.Lat-Lat)<.15 && Math.abs(c.Lon-Lon)<.15)) continue;
  Cities.push({ Name, Lat, Lon, Rank:10 });
}
const countyGaz = read('county-labels/2024_Gaz_counties_national.txt').trim().split(/\r?\n/);
const countyFields = countyGaz.shift().trim().split('\t');
for (const row of countyGaz) {
  const values = row.trim().split('\t');
  Counties.push({ Name: values[countyFields.indexOf('NAME')], Lat:round(values[countyFields.indexOf('INTPTLAT')]),
    Lon:round(values[countyFields.indexOf('INTPTLONG')]) });
}

function* csv(text) {
  let row = [], field = '', quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') {
      if (quoted && text[i+1] === '"') { field += '"'; i++; }
      else quoted = !quoted;
    } else if (!quoted && c === ',') { row.push(field); field = ''; }
    else if (!quoted && c === '\n') { row.push(field); yield row; row = []; field = ''; }
    else if (quoted || c !== '\r') field += c;
  }
  if (field || row.length) { row.push(field); yield row; }
}
function* records(file) {
  const rows = csv(read(file));
  const headers = rows.next().value;
  for (const fields of rows) yield Object.fromEntries(headers.map((key,i) => [key,fields[i]]));
}
const byId = new Map();
function coordinate(value, limit) {
  return value != null && value.trim() !== '' && Number.isFinite(Number(value)) && Math.abs(Number(value)) <= limit;
}
for (const a of records('airports.csv')) {
  const Rank = ['large_airport','medium_airport','small_airport'].indexOf(a.type);
  if (Rank < 0 || !coordinate(a.latitude_deg,90) || !coordinate(a.longitude_deg,180)) continue;
  const airport = { Code: a.icao_code || a.gps_code || a.local_code || a.ident, Name: a.name,
    Lon: round(a.longitude_deg), Lat: round(a.latitude_deg), Rank, Runways: [] };
  byId.set(a.id, airport);
  Airports.push(airport);
}
for (const r of records('runways.csv')) {
  const airport = byId.get(r.airport_ref);
  if (!airport || r.closed !== '0' || !coordinate(r.le_latitude_deg,90) || !coordinate(r.he_latitude_deg,90) ||
      !coordinate(r.le_longitude_deg,180) || !coordinate(r.he_longitude_deg,180)) continue;
  airport.Runways.push(...[r.le_longitude_deg,r.le_latitude_deg,r.he_longitude_deg,r.he_latitude_deg].map(round));
}
Airports.sort((a,b) => a.Rank-b.Rank || a.Code.localeCompare(b.Code,'en'));
writeFileSync(output, gzipSync(JSON.stringify({ Lines, Cities, Airports, Counties }), {level:9}));
console.log(Cities.length, 'places,', Airports.length, 'airports,', Airports.reduce((n,a)=>n+a.Runways.length/4,0), 'runways');
