import { useState } from 'react';
import { api } from '../api.js';
import {
  LOAD_STEPS,
  LOAD_STEP_LABELS,
  TMS_FOR,
  REPAIR_STEPS,
  REPAIR_STEP_LABELS,
  TRUCK_STATUS,
  hours,
  hubLabel,
  loadTone,
  miles,
  money,
  nearestHubLabel,
  repairTone,
  truckTone,
  words,
} from '../format.js';
import { Gauge, KV, PendingActivities, Pill, Plate, Section, TemporalLink, Timeline, Track } from './ui.jsx';

function Head({ title, badge, snapshot, workflowId, runId, onBack, children }) {
  return (
    <div className="detail-head">
      <div className="nav-row">
        <button className="ghost small" onClick={onBack}>Back to list</button>
        <TemporalLink snapshot={snapshot} workflowId={workflowId} runId={runId} />
      </div>
      <div className="title-row">
        {badge}
        <h2>{title}</h2>
      </div>
      {children && <div className="actions">{children}</div>}
    </div>
  );
}

function useAction(setToast) {
  const [busy, setBusy] = useState(false);
  const run = async (fn, okText) => {
    setBusy(true);
    try {
      const result = await fn();
      setToast({ text: result?.message || okText || 'Done.' });
    } catch (e) {
      setToast({ text: e.message, error: true });
    } finally {
      setBusy(false);
    }
  };
  return [busy, run];
}

// ------------------------------------------------------------------ truck

export function TruckDetail({ snapshot, network, unit, onBack, onSelect, setToast }) {
  const [busy, run] = useAction(setToast);
  const t = (snapshot?.trucks || []).find((x) => x.unit === unit);
  if (!t) return <p className="empty">Unit {unit} not found.</p>;
  const e = t.entity;
  const tel = t.telematics;
  const tm = tel?.telemetry || {};
  const expectedTms = e ? TMS_FOR[e.status] : null;
  const mismatch = t.tms && expectedTms && t.tms.status !== expectedTms;
  const scenario = (name, text) => run(() => api.scenario(name, { unit }), text);

  return (
    <>
      <Head
        title={`Unit ${unit}`}
        badge={<Plate unit={unit} status={e?.status} />}
        snapshot={snapshot}
        workflowId={t.workflowId}
        runId={t.runId}
        onBack={onBack}
      >
        <button className="small" disabled={busy} onClick={() => scenario('engine-anomaly')}>Overheat engine</button>
        <button className="small" disabled={busy} onClick={() => scenario('tire-leak')}>Slow tire leak</button>
        <button className="small" disabled={busy} onClick={() => scenario('pm-due')}>PM due</button>
        <button className="small danger" disabled={busy} onClick={() => scenario('breakdown')}>Break down</button>
        <button className="small" disabled={busy} onClick={() => scenario('hard-brake')}>Hard brake</button>
        <button className="small" disabled={busy} onClick={() => scenario('assign-to-shop-truck')} title="Tender a load that asks for this truck">
          Offer it a load
        </button>
      </Head>

      <Section title="Entity state" aside={e ? `run ${e.runNumber}, status v${e.statusVersion}` : 'starting'}>
        {t.stale && (
          <div className="callout" style={{ marginBottom: 8 }}>
            The entities worker did not answer the last query. Showing the last known state; nothing is lost while the worker is down.
          </div>
        )}
        <KV
          items={[
            ['Status', e ? <Pill tone={truckTone(e.status)}>{TRUCK_STATUS[e.status]?.label}</Pill> : 'Starting'],
            ['Driver', t.driverName ? `${t.driverName} (${tel?.driverId})` : 'None'],
            ['Load', e?.currentLoadId ? <a href="#" onClick={(ev) => { ev.preventDefault(); onSelect({ type: 'load', id: e.currentLoadId }); }}>{e.currentLoadId}</a> : 'None'],
            ['Open repair', e?.openMaintenanceId ? <a href="#" onClick={(ev) => { ev.preventDefault(); onSelect({ type: 'repair', id: e.openMaintenanceId }); }}>{e.openMaintenanceId}</a> : 'None'],
            ['Where', tel ? `${nearestHubLabel(network, tel.lat, tel.lon)}${tel.speedMph > 1 ? `, ${tel.speedMph} mph` : ''}` : ''],
            ['Route', tel?.routeLabel ? `${tel.routeLabel}${tel.nextStop ? `, next: ${tel.nextStop}` : ''}` : 'None'],
            ['Truck', e ? `${e.year} ${e.make} ${e.model}` : ''],
            ['Loads delivered', e?.loadsCompleted],
            ['Repair spend', e ? money(e.maintenanceSpend) : ''],
          ]}
        />
      </Section>

      <Section title="TMS record" aside="the truck entity is the only writer">
        {t.tms ? (
          <div className={`callout ${mismatch ? 'red' : 'green'}`}>
            The TMS shows <strong>{t.tms.status}</strong> (v{t.tms.version}, written by {t.tms.source}).
            {mismatch
              ? ` The entity says ${expectedTms}. The scheduled reconciliation will have the entity re-publish.`
              : ' In sync with the entity.'}
          </div>
        ) : (
          <p className="empty">No TMS record yet.</p>
        )}
      </Section>

      <Section title="Telemetry" aside="from the telematics stream, not from Temporal">
        <div className="gauges">
          <Gauge label="Coolant" value={tm.engineTempF || 0} display={`${tm.engineTempF ?? '-'}°F`} min={100} max={240} warnAbove={215} badAbove={228} />
          <Gauge label="Steer tire" value={tm.tirePsi || 0} display={`${tm.tirePsi ?? '-'} psi`} min={0} max={115} warnBelow={95} badBelow={85} />
          <Gauge label="DEF" value={tm.defPct || 0} display={`${tm.defPct ?? '-'}%`} min={0} max={100} warnBelow={15} badBelow={5} />
          <Gauge label="Fuel" value={tm.fuelPct || 0} display={`${tm.fuelPct ?? '-'}%`} min={0} max={100} warnBelow={25} />
          <Gauge label="Charging" value={tm.batteryV || 0} display={`${tm.batteryV ?? '-'} V`} min={11} max={14.5} warnBelow={12.4} badBelow={12.1} />
          <Gauge
            label="To PM-A"
            value={Math.max(0, (tm.pmDueOdometer || 0) - (tm.odometer || 0))}
            display={miles(Math.max(0, (tm.pmDueOdometer || 0) - (tm.odometer || 0)))}
            min={0}
            max={15000}
            warnBelow={1000}
            badBelow={1}
          />
        </div>
      </Section>

      <Section title="Waiting on">
        <PendingActivities workflowId={t.workflowId} />
      </Section>

      <Section title="Timeline">
        <Timeline events={e?.recentEvents} />
      </Section>
    </>
  );
}

// ------------------------------------------------------------------ load

export function LoadDetail({ snapshot, network, loadId, onBack, onSelect, setToast }) {
  const [busy, run] = useAction(setToast);
  const view = (snapshot?.loads || []).find((l) => l.state?.loadId === loadId);
  if (!view) return <p className="empty">Load {loadId} not found.</p>;
  const s = view.state;
  const hubs = {};
  (network?.hubs || []).forEach((h) => {
    hubs[h.key] = h;
  });
  const stepStatus = s.status === 'WaitingForCapacity' ? 'Planning' : s.status === 'Repowering' ? 'Planning' : s.status;

  return (
    <>
      <Head title={`Load ${s.loadId}`} badge={<Pill tone={loadTone(s.status)}>{words(s.status)}</Pill>} snapshot={snapshot} workflowId={view.workflowId} runId={view.runId} onBack={onBack}>
        {s.status === 'WaitingForCapacity' && (
          <button className="small" disabled={busy} onClick={() => run(() => api.nudge(s.loadId), 'Planning will try again now.')}>
            Retry planning now
          </button>
        )}
      </Head>

      <Section title="Progress">
        <Track steps={LOAD_STEPS} labels={LOAD_STEP_LABELS} current={stepStatus} />
        {s.repowerCount > 0 && (
          <div className="callout red" style={{ marginTop: 10 }}>
            Re-powered {s.repowerCount}x. {s.repowerReason}. Previous units: {s.previousUnits.join(', ')}.
            {s.roadsideTransferPoint && ' The trailer is picked up at the breakdown location.'}
          </div>
        )}
        {s.detentionStarted && <div className="callout" style={{ marginTop: 8 }}>Detention started at the shipper; billing was notified over Kafka.</div>}
      </Section>

      <Section title="Load">
        <KV
          items={[
            ['Lane', `${hubLabel(hubs, s.originHub)} to ${hubLabel(hubs, s.destinationHub)}`],
            ['Distance', miles(s.routeMiles)],
            ['Shipper', s.shipper],
            ['Commodity', `${s.commodity}${s.hazmat ? ' (hazmat)' : ''}`],
            ['Rate', money(s.rate)],
            ['Truck', s.assignedUnit ? <a href="#" onClick={(e) => { e.preventDefault(); onSelect({ type: 'truck', id: s.assignedUnit }); }}>Unit {s.assignedUnit}</a> : 'Not assigned'],
            ['Driver', s.assignedDriverId ? <a href="#" onClick={(e) => { e.preventDefault(); onSelect({ type: 'driver', id: s.assignedDriverId }); }}>{s.assignedDriverName}</a> : ''],
            ['Planning attempts', s.planningAttempts],
          ]}
        />
      </Section>

      <Section title="Planning log" aside="the truck and driver entities decide, not the recommender">
        <Timeline events={s.planningLog} limit={20} />
      </Section>

      <Section title="Waiting on">
        <PendingActivities workflowId={view.workflowId} />
      </Section>

      <Section title="Timeline">
        <Timeline events={s.timeline} />
      </Section>
    </>
  );
}

// ------------------------------------------------------------------ repair

export function RepairDetail({ snapshot, integrations, maintenanceId, onBack, onSelect, setToast }) {
  const [busy, run] = useAction(setToast);
  const [alternate, setAlternate] = useState('');
  const view = (snapshot?.repairs || []).find((r) => r.state?.maintenanceId === maintenanceId);
  if (!view) return <p className="empty">Case {maintenanceId} not found yet.</p>;
  const s = view.state;
  const recommended = s.options.find((o) => o.shopId === s.recommendation?.shopId);
  const vendorRecords = (integrations?.vendors || []).filter((v) => v.request.maintenanceId === s.maintenanceId);
  const pendingRequest = vendorRecords.find((v) => v.status === 'Pending');
  const inRepair = vendorRecords.find((v) => v.status === 'InRepair');
  const completed = vendorRecords.find((v) => v.status === 'Completed');
  const running = view.executionStatus === 'Running';
  const decide = (approve) =>
    run(
      () => api.decide(s.maintenanceId, { approve, alternateShopId: alternate || null, decidedBy: 'Fleet manager (demo)', note: null }),
      approve ? 'Approved. The workflow continues.' : 'Rejected. The workflow reroutes.',
    );
  const stepPhase = s.phase === 'Cancelled' ? 'Opened' : s.phase;

  return (
    <>
      <Head title={`Repair, unit ${s.unit}`} badge={<Pill tone={repairTone(s.phase)}>{words(s.phase)}</Pill>} snapshot={snapshot} workflowId={view.workflowId} runId={view.runId} onBack={onBack}>
        <button className="small" onClick={() => onSelect({ type: 'truck', id: s.unit })}>Show truck</button>
        {running && !['InRepair', 'Completed', 'Cancelled'].includes(s.phase) && (
          <button className="small danger" disabled={busy} onClick={() => run(() => api.cancelRepair(s.maintenanceId, 'Truck towed to the dealer instead'), 'Cancelling. Compensations will run.')}>
            Cancel case
          </button>
        )}
      </Head>

      <Section title="Progress">
        <Track steps={REPAIR_STEPS} labels={REPAIR_STEP_LABELS} current={stepPhase} />
        <p style={{ margin: '8px 0 0', fontSize: 13 }}>
          <strong>{s.anomalyCode}</strong>: {s.description}
          {s.mergedAnomalies.length > 0 && <span style={{ color: '#5a3f99' }}> ({s.mergedAnomalies.length} related anomalies merged)</span>}
        </p>
        {s.deferredForLoadId && s.phase === 'WaitingForLoadCompletion' && (
          <div className="callout blue" style={{ marginTop: 8 }}>
            Waiting for load {s.deferredForLoadId} to deliver. The truck can't take new loads meanwhile. No polling: the truck entity signals this case when it is released.
          </div>
        )}
        {s.repoweredLoadId && (
          <div className="callout red" style={{ marginTop: 8 }}>
            Load <a href="#" onClick={(e) => { e.preventDefault(); onSelect({ type: 'load', id: s.repoweredLoadId }); }}>{s.repoweredLoadId}</a> was told to re-power.
          </div>
        )}
      </Section>

      {s.recommendation && (
        <Section title="Recommendation" aside={s.recommendation.source}>
          <div className="callout green">
            <strong>{recommended?.name}</strong>
            <p>{s.recommendation.rationale}</p>
          </div>

          {s.phase === 'AwaitingApproval' && running && (
            <div className="approve-box">
              <div className="callout">{s.approval?.reason}</div>
              <label style={{ fontSize: 13 }}>
                Use a different shop (optional){' '}
                <select value={alternate} onChange={(e) => setAlternate(e.target.value)}>
                  <option value="">Keep the recommendation</option>
                  {s.options.map((o) => (
                    <option key={o.shopId} value={o.shopId}>
                      {o.name} ({o.isPartner ? 'partner' : 'general'}, {money(o.estimatedCost)})
                    </option>
                  ))}
                </select>
              </label>
              <div className="buttons">
                <button className="primary" disabled={busy} onClick={() => decide(true)}>Approve</button>
                <button disabled={busy} onClick={() => decide(false)}>Reject, use a partner workshop</button>
              </div>
              <p style={{ margin: 0, fontSize: 12, color: 'var(--muted)' }}>
                This is a Workflow Update: validated by the workflow and answered synchronously. A second click gets a clear rejection.
              </p>
            </div>
          )}
          {s.approval?.outcome && (
            <p style={{ fontSize: 13, margin: '8px 0 0' }}>
              Decision: <strong>{s.approval.outcome}</strong> by {s.approval.decidedBy}.
            </p>
          )}
        </Section>
      )}

      {s.options.length > 0 && (
        <Section title="Shop options">
          <table className="data">
            <thead>
              <tr><th>Shop</th><th className="num">Distance</th><th className="num">Estimate</th><th className="num">ETA</th></tr>
            </thead>
            <tbody>
              {s.options.map((o) => (
                <tr key={o.shopId} className={s.selectedShop?.shopId === o.shopId ? 'chosen' : ''}>
                  <td>
                    {o.name} {o.isPartner ? <Pill tone="green">partner</Pill> : null}
                  </td>
                  <td className="num">{miles(o.distanceMiles)}</td>
                  <td className="num">{money(o.estimatedCost)}</td>
                  <td className="num">{o.etaHours.toFixed(1)} h</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Section>
      )}

      {s.vendorAttempts.length > 0 && (
        <Section title="Service requests" aside="no answer in 2 h (simulated) moves to the next shop">
          <table className="data">
            <tbody>
              {s.vendorAttempts.map((v) => (
                <tr key={v.requestId}>
                  <td>{v.shopName}</td>
                  <td>
                    <Pill tone={v.outcome === 'Accepted' ? 'green' : v.outcome === 'Pending' ? 'amber' : 'red'}>{v.outcome}</Pill>
                  </td>
                  <td style={{ color: 'var(--muted)' }}>{v.note}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {pendingRequest && (
            <div style={{ display: 'flex', gap: 6, marginTop: 8, flexWrap: 'wrap' }}>
              <span style={{ fontSize: 12, color: 'var(--muted)', alignSelf: 'center' }}>Act as {pendingRequest.request.shopName}:</span>
              <button className="small" disabled={busy} onClick={() => run(() => api.vendorAction(pendingRequest.request.requestId, 'accept'), 'Vendor accepted.')}>Accept</button>
              <button className="small" disabled={busy} onClick={() => run(() => api.vendorAction(pendingRequest.request.requestId, 'decline', 'No bay available'), 'Vendor declined.')}>Decline</button>
            </div>
          )}
          {inRepair && (
            <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
              <button className="small" disabled={busy} onClick={() => run(() => api.vendorAction(inRepair.request.requestId, 'complete'), 'Shop reported the repair complete.')}>
                Shop: mark repair complete
              </button>
            </div>
          )}
          {completed && (
            <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
              <button className="small" disabled={busy} onClick={() => run(() => api.vendorAction(completed.request.requestId, 'resend-completion'), 'Duplicate completion sent.')}>
                Send the completion again
              </button>
            </div>
          )}
        </Section>
      )}

      <Section title="Cost">
        <KV
          items={[
            ['Work order', s.workOrderNumber ? `${s.workOrderNumber} (Salesforce)` : 'Pending'],
            ['Estimate', s.estimatedCost ? money(s.estimatedCost) : ''],
            ['Actual', s.actualCost ? money(s.actualCost) : ''],
            ['General workshop baseline', s.generalWorkshopBaseline ? money(s.generalWorkshopBaseline) : ''],
            ['Saved', s.savings ? <span style={{ color: 'var(--sign-green)' }}>{money(s.savings)}</span> : ''],
            ['Duplicates ignored', s.duplicateEventsIgnored || ''],
          ]}
        />
      </Section>

      <Section title="Waiting on">
        <PendingActivities workflowId={view.workflowId} />
      </Section>

      <Section title="Timeline">
        <Timeline events={s.timeline} />
      </Section>
    </>
  );
}

// ------------------------------------------------------------------ driver

export function DriverDetail({ snapshot, driverId, onBack, onSelect, setToast }) {
  const [busy, run] = useAction(setToast);
  const view = (snapshot?.drivers || []).find((d) => d.state?.driverId === driverId);
  if (!view) return <p className="empty">Driver {driverId} not found yet.</p>;
  const s = view.state;
  const hos = view.eld?.hos || s.hos;

  return (
    <>
      <Head title={s.name} badge={<span className="plate gray">{s.driverId}</span>} snapshot={snapshot} workflowId={view.workflowId} runId={view.runId} onBack={onBack}>
        {s.currentUnit && (
          <>
            <button className="small" disabled={busy} onClick={() => run(() => api.scenario('hard-brake', { unit: s.currentUnit }))}>Hard brake</button>
            <button className="small" disabled={busy} onClick={() => run(() => api.scenario('driver-break', { driverId: s.driverId }))}>Pull into rest area</button>
            <button className="small" onClick={() => onSelect({ type: 'truck', id: s.currentUnit })}>Show unit {s.currentUnit}</button>
          </>
        )}
      </Head>

      <Section title="Driver">
        <KV
          items={[
            ['Employment', <Pill tone={s.employment === 'Active' ? 'green' : 'blue'}>{words(s.employment)}</Pill>],
            ['Duty status', words(view.eld?.duty || s.duty)],
            ['Unit', s.currentUnit],
            ['Load', s.currentLoadId],
            ['Endorsements', s.endorsements.join(', ') || 'None'],
            ['Home terminal', s.homeHub],
            ['Safety events', s.safetyEventsTotal],
          ]}
        />
      </Section>

      {s.onboarding.length > 0 && (
        <Section title="Onboarding" aside="runs inside the driver entity">
          <ol className="timeline">
            {s.onboarding.map((o) => (
              <li key={o.name}>
                <time>{o.status === 'Done' ? 'done' : o.status === 'Running' ? 'now' : ''}</time>
                <span className={o.status === 'Done' ? 'k-success' : ''}>{o.name}</span>
              </li>
            ))}
          </ol>
          {s.employment === 'Onboarding' && (
            <p style={{ fontSize: 12, color: 'var(--muted)', margin: '6px 0 0' }}>
              Loads that try to reserve this driver are refused by the driver entity until onboarding finishes.
            </p>
          )}
        </Section>
      )}

      <Section title="Hours of service" aside="from the ELD">
        <div className="gauges">
          <Gauge label="Drive" value={hos.driveMinutesLeft} display={hours(hos.driveMinutesLeft)} min={0} max={660} warnBelow={120} badBelow={30} />
          <Gauge label="Shift" value={hos.shiftMinutesLeft} display={hours(hos.shiftMinutesLeft)} min={0} max={840} warnBelow={120} badBelow={30} />
          <Gauge label="70-hour cycle" value={hos.cycleMinutesLeft} display={hours(hos.cycleMinutesLeft)} min={0} max={4200} warnBelow={600} badBelow={120} />
          <Gauge label="Since break" value={480 - hos.minutesSinceBreak} display={hours(hos.minutesSinceBreak)} min={0} max={480} warnBelow={60} badBelow={10} />
        </div>
      </Section>

      <Section title="Safety">
        {s.pendingSafetyEvents.length > 0 ? (
          <div className="callout">
            {s.pendingSafetyEvents.length} event(s) held. The coaching call starts when the ELD reports the driver off the road.
          </div>
        ) : (
          <p style={{ margin: 0, fontSize: 13, color: 'var(--muted)' }}>No held events.</p>
        )}
        {s.coachingWorkflowIds.map((id) => (
          <div key={id} style={{ marginTop: 6 }}>
            <a href="#" onClick={(e) => { e.preventDefault(); onSelect({ type: 'coaching', id }); }}>{id}</a>
          </div>
        ))}
      </Section>

      <Section title="Timeline">
        <Timeline events={s.recentEvents} />
      </Section>
    </>
  );
}

// ------------------------------------------------------------------ coaching

export function CoachingDetail({ snapshot, integrations, coachingId, onBack, onSelect, setToast }) {
  const [busy, run] = useAction(setToast);
  const view = (snapshot?.coaching || []).find((c) => c.state?.coachingId === coachingId);
  if (!view) return <p className="empty">Coaching {coachingId} not found yet.</p>;
  const s = view.state;
  const calls = (integrations?.ivr || []).filter((c) => c.call.workflowId === coachingId);
  const ringing = calls.find((c) => c.outcome === 'Ringing');

  return (
    <>
      <Head title={`Safety coaching, ${s.driverName}`} badge={<Pill tone={s.status === 'Escalated' ? 'amber' : s.status === 'Calling' ? 'blue' : 'green'}>{words(s.status)}</Pill>} snapshot={snapshot} workflowId={view.workflowId} runId={view.runId} onBack={onBack}>
        <button className="small" onClick={() => onSelect({ type: 'driver', id: s.driverId })}>Show driver</button>
        {ringing && (
          <button className="small" disabled={busy} onClick={() => run(() => api.ackCall(ringing.call.callId), 'Driver pressed 1.')}>Driver presses 1</button>
        )}
        {s.status === 'Escalated' && (
          <button className="primary small" disabled={busy} onClick={() => run(() => api.resolveCoaching(s.coachingId, { resolvedBy: 'Safety manager (demo)', note: 'Reached the driver by phone' }), 'Resolved.')}>
            Mark resolved
          </button>
        )}
      </Head>
      <Section title="Events reviewed">
        {s.events.map((e) => (
          <div key={e.eventId} style={{ fontSize: 13, marginBottom: 4 }}>
            {words(e.type)} at {Math.round(e.speedMph)} mph near {e.location}
          </div>
        ))}
        <KV items={[['Call attempts', s.callAttempts], ['Salesforce case', s.caseId], ['Resolved by', s.resolvedBy]]} />
      </Section>
      <Section title="Timeline">
        <Timeline events={s.timeline} />
      </Section>
    </>
  );
}
