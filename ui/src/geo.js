// Simplified lower-48 outline (lon, lat), hand-drawn at road-atlas detail. Good enough for orientation.
export const US_OUTLINE = [
  [-124.73, 48.38], [-124.1, 46.86], [-123.95, 46.18], [-124.07, 44.6], [-124.5, 42.84], [-124.21, 41.99],
  [-124.41, 40.44], [-123.8, 39.3], [-123.02, 38.0], [-122.51, 37.78], [-122.4, 37.2], [-121.9, 36.6],
  [-121.3, 35.7], [-120.64, 34.58], [-119.4, 34.37], [-118.5, 34.03], [-118.1, 33.7], [-117.25, 32.85],
  [-117.12, 32.53], [-114.72, 32.72], [-114.81, 32.49], [-111.07, 31.33], [-108.21, 31.33], [-108.21, 31.78],
  [-106.53, 31.78], [-106.0, 31.39], [-104.92, 30.6], [-104.4, 29.57], [-103.3, 28.98], [-102.4, 29.76],
  [-101.4, 29.77], [-100.3, 28.3], [-99.5, 27.5], [-99.1, 26.43], [-97.4, 25.84], [-97.2, 26.6],
  [-97.4, 27.8], [-96.8, 28.4], [-95.1, 29.1], [-94.0, 29.68], [-93.3, 29.77], [-92.0, 29.55],
  [-90.9, 29.1], [-89.4, 28.95], [-89.6, 29.7], [-89.4, 30.05], [-88.6, 30.35], [-87.5, 30.3],
  [-86.5, 30.4], [-85.4, 29.7], [-84.3, 30.05], [-83.4, 29.55], [-82.8, 28.2], [-82.65, 27.4],
  [-82.0, 26.5], [-81.25, 25.3], [-80.4, 25.2], [-80.1, 25.8], [-80.05, 26.7], [-80.6, 28.4],
  [-81.2, 29.8], [-81.4, 30.7], [-81.1, 31.9], [-80.3, 32.5], [-79.1, 33.3], [-77.95, 33.85],
  [-76.6, 34.7], [-75.55, 35.25], [-75.9, 36.6], [-76.0, 37.3], [-75.7, 37.95], [-75.25, 38.45],
  [-75.05, 38.95], [-74.4, 39.4], [-73.98, 40.5], [-73.1, 40.95], [-71.9, 41.3], [-71.0, 41.5],
  [-70.0, 41.7], [-70.6, 42.3], [-70.8, 43.1], [-70.2, 43.7], [-69.0, 44.1], [-67.8, 44.5],
  [-66.95, 44.8], [-67.8, 45.7], [-67.8, 47.1], [-68.3, 47.35], [-69.2, 47.45], [-70.0, 46.7],
  [-70.9, 45.3], [-71.5, 45.01], [-74.7, 45.0], [-75.3, 44.85], [-76.3, 44.2], [-76.8, 43.6],
  [-79.1, 43.3], [-79.05, 42.9], [-79.8, 42.4], [-81.2, 41.9], [-82.5, 41.7], [-83.4, 41.75],
  [-83.1, 42.3], [-82.5, 43.0], [-82.4, 43.8], [-83.4, 44.0], [-83.3, 45.0], [-84.4, 45.9],
  [-84.1, 46.5], [-85.5, 46.6], [-87.0, 46.5], [-88.3, 47.3], [-89.7, 47.0], [-90.7, 46.7],
  [-92.1, 46.75], [-89.6, 48.0], [-91.4, 48.05], [-93.1, 48.6], [-94.7, 48.8], [-95.15, 49.0],
  [-104.0, 49.0], [-115.0, 49.0], [-122.8, 49.0], [-122.5, 48.4], [-123.4, 48.15],
];

export const LAKE_MICHIGAN = [
  [-87.5, 41.65], [-87.8, 42.5], [-87.9, 43.4], [-87.5, 44.5], [-87.0, 45.3], [-86.5, 45.8],
  [-85.0, 45.8], [-85.4, 45.1], [-86.2, 44.2], [-86.5, 43.2], [-86.2, 42.3], [-86.9, 41.7],
];

// Albers equal-area conic, the projection used for most US national maps.
const RAD = Math.PI / 180;
const phi1 = 29.5 * RAD;
const phi2 = 45.5 * RAD;
const phi0 = 37.5 * RAD;
const lambda0 = -96 * RAD;
const n = (Math.sin(phi1) + Math.sin(phi2)) / 2;
const C = Math.cos(phi1) ** 2 + 2 * n * Math.sin(phi1);
const rho0 = Math.sqrt(C - 2 * n * Math.sin(phi0)) / n;

function albers(lon, lat) {
  const rho = Math.sqrt(C - 2 * n * Math.sin(lat * RAD)) / n;
  const theta = n * (lon * RAD - lambda0);
  return [rho * Math.sin(theta), rho0 - rho * Math.cos(theta)];
}

export const VIEW_W = 1000;
export const VIEW_H = 620;
const PAD = 26;

const raw = US_OUTLINE.map(([lon, lat]) => albers(lon, lat));
const minX = Math.min(...raw.map((p) => p[0]));
const maxX = Math.max(...raw.map((p) => p[0]));
const minY = Math.min(...raw.map((p) => p[1]));
const maxY = Math.max(...raw.map((p) => p[1]));
const scale = Math.min((VIEW_W - PAD * 2) / (maxX - minX), (VIEW_H - PAD * 2) / (maxY - minY));
const offsetX = (VIEW_W - (maxX - minX) * scale) / 2;
const offsetY = (VIEW_H - (maxY - minY) * scale) / 2;

/** Projects a lat/lon to SVG coordinates in the VIEW_W x VIEW_H viewBox. */
export function project(lat, lon) {
  const [x, y] = albers(lon, lat);
  return [offsetX + (x - minX) * scale, offsetY + (maxY - y) * scale];
}

export function pathFromLonLat(points) {
  return points
    .map(([lon, lat], i) => {
      const [x, y] = project(lat, lon);
      return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ') + ' Z';
}

/** Screen bearing at a point, so truck chevrons point along the road on the projected map. */
export function screenHeading(lat, lon, headingDeg) {
  const d = 0.05;
  const rad = headingDeg * RAD;
  const [x1, y1] = project(lat, lon);
  const [x2, y2] = project(lat + Math.cos(rad) * d, lon + (Math.sin(rad) * d) / Math.cos(lat * RAD));
  return (Math.atan2(y2 - y1, x2 - x1) * 180) / Math.PI;
}
