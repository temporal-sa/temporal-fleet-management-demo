import { useMemo, useRef, useState } from 'react';
import { LAKE_MICHIGAN, US_OUTLINE, VIEW_H, VIEW_W, pathFromLonLat, project, screenHeading } from '../geo.js';
import { TRUCK_STATUS, truckColor } from '../format.js';

const LABELED_HUBS = new Set([
  'SEA', 'PDX', 'SAC', 'LAX', 'PHX', 'SLC', 'DEN', 'ELP', 'DFW', 'HOU', 'SAT', 'OKC', 'OMA', 'MKC', 'MSP',
  'CHI', 'STL', 'MEM', 'ATL', 'JAX', 'CLT', 'IND', 'CMH', 'PIT', 'EWR', 'BOS', 'ABQ', 'LAS', 'DET', 'MSY',
]);

export default function FleetMap({ network, snapshot, selected, onSelect, showVendors, onToggleVendors }) {
  const [hover, setHover] = useState(null);
  const wrap = useRef(null);
  const hubs = network?.hubs || [];

  const base = useMemo(() => {
    const byKey = {};
    hubs.forEach((h) => {
      byKey[h.key] = h;
    });
    const roads = (network?.corridors || [])
      .filter(([a, b]) => byKey[a] && byKey[b])
      .map(([a, b]) => {
        const [x1, y1] = project(byKey[a].lat, byKey[a].lon);
        const [x2, y2] = project(byKey[b].lat, byKey[b].lon);
        return `M${x1.toFixed(1)},${y1.toFixed(1)} L${x2.toFixed(1)},${y2.toFixed(1)}`;
      })
      .join(' ');
    return { outline: pathFromLonLat(US_OUTLINE), lake: pathFromLonLat(LAKE_MICHIGAN), roads };
  }, [network]);

  const trucks = snapshot?.trucks || [];
  const selectedTruck = selected?.type === 'truck' ? trucks.find((t) => t.unit === selected.id) : null;
  const activeShopIds = new Set(
    (snapshot?.repairs || [])
      .filter((r) => r.executionStatus === 'Running')
      .flatMap((r) => [r.state?.selectedShop?.shopId, ...(r.state?.vendorAttempts || []).map((v) => v.shopId)])
      .filter(Boolean),
  );

  const routePath = useMemo(() => {
    const points = selectedTruck?.telematics?.path || [];
    if (points.length < 2) return null;
    return points
      .map(([lat, lon], i) => {
        const [x, y] = project(lat, lon);
        return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(' ');
  }, [selectedTruck?.telematics?.path]);

  function showTip(event, text) {
    const rect = wrap.current.getBoundingClientRect();
    setHover({ x: event.clientX - rect.left, y: event.clientY - rect.top, text });
  }

  return (
    <div className="map-wrap" ref={wrap}>
      <svg className="map-svg" viewBox={`0 0 ${VIEW_W} ${VIEW_H}`} preserveAspectRatio="xMidYMid meet" role="img" aria-label="Fleet map">
        <path className="land" d={base.outline} />
        <path className="lake" d={base.lake} />
        <path className="road-casing" d={base.roads} />
        <path className="road" d={base.roads} />

        {hubs
          .filter((h) => LABELED_HUBS.has(h.key))
          .map((h) => {
            const [x, y] = project(h.lat, h.lon);
            return (
              <text key={h.key} className="hub-label" x={x + 7} y={y - 6}>
                {h.city}
              </text>
            );
          })}

        {(network?.shops || [])
          .filter((s) => s.isPartner || showVendors || activeShopIds.has(s.shopId))
          .map((s) => {
            const [x, y] = project(s.lat, s.lon);
            const offset = s.isPartner ? -9 : 9;
            return s.isPartner ? (
              <rect
                key={s.shopId}
                className="shop-own"
                x={x - 6 + offset}
                y={y - 6}
                width="12"
                height="12"
                rx="2.5"
                onMouseMove={(e) => showTip(e, `${s.name} (partner workshop)`)}
                onMouseLeave={() => setHover(null)}
              />
            ) : (
              <path
                key={s.shopId}
                className="shop-vendor"
                d={`M${x + offset},${y - 6} L${x + offset + 6},${y} L${x + offset},${y + 6} L${x + offset - 6},${y} Z`}
                onMouseMove={(e) => showTip(e, `${s.name} (general workshop)`)}
                onMouseLeave={() => setHover(null)}
              />
            );
          })}

        {routePath && <path className={`route ${selectedTruck?.telematics?.towed ? 'tow' : ''}`} d={routePath} />}
        {selectedTruck?.telematics?.path?.length > 1 && (() => {
          const last = selectedTruck.telematics.path[selectedTruck.telematics.path.length - 1];
          const [x, y] = project(last[0], last[1]);
          return <circle className="stop" cx={x} cy={y} r="5" />;
        })()}

        {trucks
          .filter((t) => t.telematics)
          .sort((a, b) => (a.unit === selected?.id ? 1 : b.unit === selected?.id ? -1 : 0))
          .map((t) => {
            const tel = t.telematics;
            const [x, y] = project(tel.lat, tel.lon);
            const status = t.entity?.status;
            const color = t.stale && !status ? '#8a948e' : truckColor(status);
            const isSelected = selected?.type === 'truck' && selected.id === t.unit;
            const moving = tel.speedMph > 1;
            const angle = moving ? screenHeading(tel.lat, tel.lon, tel.heading) : 0;
            const down = status === 'Breakdown' || tel.state === 'Broken';
            const label = `${t.unit}: ${TRUCK_STATUS[status]?.label || 'Starting'}${tel.routeLabel ? `, ${tel.routeLabel}` : ''}`;
            return (
              <g
                key={t.unit}
                className={`truck ${isSelected ? 'selected' : ''}`}
                style={{ transform: `translate(${x}px, ${y}px)` }}
                onClick={() => onSelect({ type: 'truck', id: t.unit })}
                onMouseMove={(e) => showTip(e, label)}
                onMouseLeave={() => setHover(null)}
              >
                {down && <circle className="ring" r="9" />}
                {moving ? (
                  <path className="body" d="M9,0 L-6,-6 L-3,0 L-6,6 Z" fill={color} transform={`rotate(${angle}) scale(${isSelected ? 1.35 : 1})`} />
                ) : (
                  <circle className="body" r={isSelected ? 7.5 : 5.5} fill={color} />
                )}
                {isSelected && (
                  <text className="label" x="12" y="4">
                    {t.unit}
                  </text>
                )}
              </g>
            );
          })}
      </svg>

      <div className="map-tools">
        <button className="small" onClick={onToggleVendors} aria-pressed={showVendors}>
          {showVendors ? 'Hide general workshops' : 'Show general workshops'}
        </button>
      </div>

      <div className="map-legend" aria-label="Legend">
        <span className="legend-item"><span className="swatch round" style={{ background: '#2e7d4f' }} />Available</span>
        <span className="legend-item"><span className="swatch round" style={{ background: '#1d4f91' }} />On a load</span>
        <span className="legend-item"><span className="swatch round" style={{ background: '#c98500' }} />Shop bound or in shop</span>
        <span className="legend-item"><span className="swatch round" style={{ background: '#b42318' }} />Broken down</span>
        <span className="legend-item"><span className="swatch" style={{ background: '#fff', boxShadow: 'inset 0 0 0 2.5px #0b5d3b' }} />Partner workshop</span>
        <span className="legend-item"><span className="swatch" style={{ background: '#8b958e', transform: 'rotate(45deg) scale(0.8)' }} />General workshop</span>
        <span className="legend-item" style={{ gridColumn: '1 / -1', color: 'var(--muted)' }}>Arrows are moving, dots are stopped</span>
      </div>

      {hover && (
        <div className="tooltip" style={{ left: hover.x, top: hover.y }}>
          {hover.text}
        </div>
      )}
    </div>
  );
}
