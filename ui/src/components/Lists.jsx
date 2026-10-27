import { useState } from 'react';
import {
  TMS_FOR,
  TRUCK_STATUS,
  ago,
  clock,
  hours,
  hubLabel,
  loadTone,
  money,
  nearestHubLabel,
  repairTone,
  temporalQueryUrl,
  words,
} from '../format.js';
import { ExternalIcon, Pill, Plate, Section, TemporalLink } from './ui.jsx';

function VisibilityLink({ snapshot, query, children }) {
  return (
    <a className="temporal-link" href={temporalQueryUrl(snapshot, query)} target="_blank" rel="noreferrer" title={query}>
      {children} <ExternalIcon />
    </a>
  );
}

const TRUCK_FILTERS = [
  ['all', 'All', null],
  ['load', 'On loads', ['Reserved', 'OnLoad']],
  ['available', 'Available', ['Available']],
  ['shop', 'Shop', ['ShopBound', 'InShop']],
  ['down', 'Down', ['Breakdown', 'OutOfService']],
];

export function TruckList({ snapshot, network, selected, onSelect }) {
  const [filter, setFilter] = useState('all');
  const statuses = TRUCK_FILTERS.find((f) => f[0] === filter)[2];
  const trucks = (snapshot?.trucks || []).filter((t) => !statuses || statuses.includes(t.entity?.status));
  const query = statuses
    ? `FleetEntity="Truck" AND (${statuses.map((s) => `FleetStatus="${s}"`).join(' OR ')})`
    : 'FleetEntity="Truck"';

  return (
    <>
      <div className="filters">
        {TRUCK_FILTERS.map(([key, label]) => (
          <button key={key} className={`chip ${filter === key ? 'on' : ''}`} onClick={() => setFilter(key)}>
            {label}
          </button>
        ))}
      </div>
      <ul className="rows">
        {trucks.map((t) => {
          const status = t.entity?.status;
          const tel = t.telematics;
          const tmsMismatch = t.tms && status && TMS_FOR[status] !== t.tms.status;
          return (
            <li
              key={t.unit}
              className={`row ${selected?.type === 'truck' && selected.id === t.unit ? 'selected' : ''}`}
              onClick={() => onSelect({ type: 'truck', id: t.unit })}
            >
              <Plate unit={t.unit} status={status} />
              <div style={{ minWidth: 0 }}>
                <div className="primary-line">
                  {TRUCK_STATUS[status]?.label || 'Starting'}
                  {t.entity?.currentLoadId && <span style={{ fontWeight: 500 }}> on {t.entity.currentLoadId}</span>}
                  {t.entity?.openMaintenanceId && !['ShopBound', 'InShop', 'Breakdown'].includes(status) && (
                    <span style={{ fontWeight: 500, color: '#8a5d00' }}> with repair pending</span>
                  )}
                </div>
                <div className="secondary-line">
                  {t.driverName || 'No driver'}
                  {tel ? `, ${tel.speedMph > 1 ? `${tel.speedMph} mph near ` : ''}${nearestHubLabel(network, tel.lat, tel.lon)}` : ''}
                </div>
              </div>
              <div className="right">
                {tmsMismatch && <Pill tone="red" title="TMS disagrees with the truck entity">TMS mismatch</Pill>}
                {t.stale && <Pill tone="gray" title="The entities worker did not answer the last query">stale</Pill>}
              </div>
            </li>
          );
        })}
      </ul>
      <div className="side-section">
        <VisibilityLink snapshot={snapshot} query={query}>
          Same list in Temporal
        </VisibilityLink>
        <p style={{ fontSize: 12, color: 'var(--muted)', margin: '6px 0 0' }}>
          No database here. This list is a visibility query over running truck workflows; details come from workflow queries.
        </p>
      </div>
    </>
  );
}

export function LoadList({ snapshot, network, selected, onSelect, onTender }) {
  const hubs = {};
  (network?.hubs || []).forEach((h) => {
    hubs[h.key] = h;
  });
  const loads = snapshot?.loads || [];
  const active = loads.filter((l) => l.executionStatus === 'Running');
  const done = loads.filter((l) => l.executionStatus !== 'Running').slice(0, 25);

  const row = (l) => {
    const s = l.state;
    if (!s) return null;
    return (
      <li
        key={l.workflowId}
        className={`row ${selected?.type === 'load' && selected.id === s.loadId ? 'selected' : ''}`}
        onClick={() => onSelect({ type: 'load', id: s.loadId })}
      >
        <span style={{ fontWeight: 800, fontSize: 13 }}>{s.loadId}</span>
        <div style={{ minWidth: 0 }}>
          <div className="primary-line">
            {hubs[s.originHub]?.city || s.originHub} to {hubs[s.destinationHub]?.city || s.destinationHub}
            {s.hazmat && <Pill tone="amber">hazmat</Pill>}
          </div>
          <div className="secondary-line">
            {s.assignedUnit ? `Unit ${s.assignedUnit}, ${s.assignedDriverName || ''}` : s.shipper}
            {s.repowerCount > 0 && `, re-powered ${s.repowerCount}x`}
          </div>
        </div>
        <div className="right">
          <Pill tone={loadTone(s.status)}>{words(s.status)}</Pill>
        </div>
      </li>
    );
  };

  return (
    <>
      <div className="filters" style={{ justifyContent: 'space-between' }}>
        <span style={{ fontSize: 13, color: 'var(--muted)', alignSelf: 'center' }}>
          {active.length} active, {loads.filter((l) => l.state?.status === 'Delivered').length} delivered
        </span>
        <button className="small" onClick={onTender}>Tender a load</button>
      </div>
      <ul className="rows">{active.map(row)}</ul>
      {done.length > 0 && (
        <>
          <div className="side-section" style={{ paddingBottom: 4 }}>
            <h3>Recently closed</h3>
          </div>
          <ul className="rows">{done.map(row)}</ul>
        </>
      )}
      {loads.length === 0 && <p className="empty">No loads yet. They appear once the entity workflows are up.</p>}
    </>
  );
}

export function RepairList({ snapshot, selected, onSelect }) {
  const repairs = snapshot?.repairs || [];
  if (repairs.length === 0) {
    return <p className="empty">No maintenance cases yet. Open the runbook and inject an engine or tire fault.</p>;
  }

  return (
    <ul className="rows">
      {repairs.map((r) => {
        const s = r.state;
        if (!s) return null;
        return (
          <li
            key={r.workflowId}
            className={`row ${selected?.type === 'repair' && selected.id === s.maintenanceId ? 'selected' : ''}`}
            onClick={() => onSelect({ type: 'repair', id: s.maintenanceId })}
          >
            <Plate unit={s.unit} status={s.roadside ? 'Breakdown' : 'ShopBound'} />
            <div style={{ minWidth: 0 }}>
              <div className="primary-line">
                {words(s.category)}, {words(s.severity).toLowerCase()} severity{s.roadside ? ', roadside' : ''}
              </div>
              <div className="secondary-line">
                {s.selectedShop ? s.selectedShop.name : s.recommendation ? `Recommended: ${s.options.find((o) => o.shopId === s.recommendation.shopId)?.name}` : s.description}
              </div>
            </div>
            <div className="right">
              <Pill tone={repairTone(s.phase)}>{words(s.phase)}</Pill>
              {s.savings > 0 && <div style={{ color: 'var(--sign-green)', fontWeight: 700 }}>{money(s.savings)} saved</div>}
            </div>
          </li>
        );
      })}
    </ul>
  );
}

export function DriverList({ snapshot, selected, onSelect }) {
  const drivers = snapshot?.drivers || [];
  return (
    <ul className="rows">
      {drivers.map((d) => {
        const s = d.state;
        if (!s) return null;
        const done = s.onboarding.filter((o) => o.status === 'Done').length;
        return (
          <li
            key={d.workflowId}
            className={`row ${selected?.type === 'driver' && selected.id === s.driverId ? 'selected' : ''}`}
            onClick={() => onSelect({ type: 'driver', id: s.driverId })}
          >
            <span style={{ fontWeight: 800, fontSize: 13 }}>{s.driverId}</span>
            <div style={{ minWidth: 0 }}>
              <div className="primary-line">{s.name}</div>
              <div className="secondary-line">
                {s.employment === 'Onboarding'
                  ? `Onboarding, ${done} of ${s.onboarding.length} steps`
                  : `${words(s.duty)}, ${hours(s.hos?.driveMinutesLeft)} drive left${s.currentUnit ? `, unit ${s.currentUnit}` : ''}`}
              </div>
            </div>
            <div className="right">
              {s.pendingSafetyEvents.length > 0 && <Pill tone="amber" title="Coaching call held until the driver stops">call held</Pill>}
              {s.employment === 'Onboarding' && <Pill tone="blue">onboarding</Pill>}
              {s.currentLoadId && <Pill tone="blue">{s.currentLoadId}</Pill>}
            </div>
          </li>
        );
      })}
    </ul>
  );
}

export function Inbox({ snapshot, onSelect }) {
  const items = snapshot?.inbox || [];
  if (items.length === 0) {
    return (
      <p className="empty">
        Nothing is waiting on a person. Expensive general workshop repairs, unanswered safety calls and loads without a truck show up here.
      </p>
    );
  }

  return (
    <ul className="rows">
      {items.map((i) => (
        <li
          key={i.id}
          className="row"
          style={{ gridTemplateColumns: '1fr auto' }}
          onClick={() =>
            onSelect(
              i.kind === 'approval'
                ? { type: 'repair', id: i.id }
                : i.kind === 'coaching'
                  ? { type: 'coaching', id: i.id }
                  : { type: 'load', id: i.id },
            )
          }
        >
          <div style={{ minWidth: 0 }}>
            <div className="primary-line">{i.title}</div>
            <div className="secondary-line" style={{ whiteSpace: 'normal' }}>{i.detail}</div>
          </div>
          <div className="right">
            <Pill tone="amber">{i.kind === 'approval' ? 'Decide' : i.kind === 'coaching' ? 'Follow up' : 'Dispatch'}</Pill>
            <div>{ago(i.since)}</div>
          </div>
        </li>
      ))}
    </ul>
  );
}

function statusTone(code) {
  if (code >= 500 || code === 403 || code === 401) return 'red';
  if (code >= 400) return 'amber';
  return 'green';
}

export function SystemsPanel({ snapshot, integrations, onSelect }) {
  const sync = snapshot?.sync;
  const i = integrations || {};
  return (
    <>
      <Section title="Salesforce API calls" aside="most recent first">
        <table className="data">
          <tbody>
            {(i.salesforceCalls || []).slice(0, 14).map((c, idx) => (
              <tr key={idx}>
                <td style={{ width: 56 }}>{clock(c.at).slice(0, 8)}</td>
                <td><Pill tone={statusTone(c.status)}>{c.status}</Pill></td>
                <td>{c.method} {c.path} <span style={{ color: 'var(--muted)' }}>{c.note}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title="Salesforce work orders" aside="upserted by external ID">
        <table className="data">
          <thead>
            <tr><th>Number</th><th>Unit</th><th>Status</th><th>Workflow</th></tr>
          </thead>
          <tbody>
            {(i.workOrders || []).slice(0, 12).map((w) => (
              <tr key={w.Id} style={{ cursor: 'pointer' }} onClick={() => onSelect({ type: 'repair', id: w.Temporal_Workflow_Id__c })}>
                <td>{w.WorkOrderNumber}</td>
                <td>{w.Unit_Number__c}</td>
                <td>{w.Status}</td>
                <td style={{ color: 'var(--shield-blue)' }}>{w.Temporal_Workflow_Id__c}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title="TMS calls">
        <table className="data">
          <tbody>
            {(i.tmsCalls || []).slice(0, 14).map((c, idx) => (
              <tr key={idx}>
                <td style={{ width: 56 }}>{clock(c.at).slice(0, 8)}</td>
                <td><Pill tone={statusTone(c.status)}>{c.status}</Pill></td>
                <td>{c.method} {c.path} <span style={{ color: 'var(--muted)' }}>{c.note}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>

      <Section title="Equipment reconciliation" aside="scheduled every minute">
        {sync?.report ? (
          <>
            <p style={{ margin: '0 0 6px', fontSize: 13 }}>
              Last run {ago(sync.report.ranAt)}: {sync.report.equipmentRecords} trucks and {sync.report.driverRecords} drivers read from Salesforce,{' '}
              {sync.report.startedEntities.length} entity workflows started, {sync.report.conflicts.length} conflicts fixed.
            </p>
            {sync.report.conflicts.map((c, idx) => (
              <div key={idx} className="callout red" style={{ marginBottom: 6 }}>
                Unit {c.unit}: TMS had {c.tmsStatus}, the truck entity says {words(c.temporalStatus)}. {c.resolution}
              </div>
            ))}
            <TemporalLink snapshot={snapshot} workflowId={sync.workflowId} runId={sync.runId} label="Open last sync run" />
          </>
        ) : (
          <p className="empty">Waiting for the first sync run.</p>
        )}
      </Section>

      <Section title="Kafka hand-offs" aside="to your existing pipelines">
        {(i.kafka || []).length === 0 && <p style={{ margin: 0, color: 'var(--muted)' }}>Nothing published yet.</p>}
        {(i.kafka || []).map((t) => (
          <div key={t.topic} style={{ marginBottom: 6 }}>
            <strong>{t.topic}</strong> <span style={{ color: 'var(--muted)' }}>{t.count} messages</span>
          </div>
        ))}
      </Section>

      <Section title="Workshop requests">
        <table className="data">
          <tbody>
            {(i.vendors || []).slice(0, 12).map((v) => (
              <tr key={v.request.requestId}>
                <td>{v.request.shopName}</td>
                <td>{v.request.unit}</td>
                <td><Pill tone={v.status === 'Accepted' || v.status === 'Completed' ? 'green' : v.status === 'Pending' ? 'amber' : 'gray'}>{v.status}</Pill></td>
              </tr>
            ))}
          </tbody>
        </table>
      </Section>
    </>
  );
}

export { hubLabel };
