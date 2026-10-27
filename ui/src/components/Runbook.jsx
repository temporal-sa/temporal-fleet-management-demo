import { useState } from 'react';
import { api } from '../api.js';

// The demo, organized by theme. Each scenario says what happens and where to look.
const ACTS = [
  {
    title: 'Maintenance and repair',
    intro: 'Repairs are routed to partner workshops when it is safe to do so, which is where the savings come from.',
    scenarios: [
      {
        id: 'engine-approval',
        title: 'Expensive repair needs a manager',
        what: 'An idle truck far from any partner engine workshop overheats. The only safe option is a general workshop above the approval threshold, so the case waits for a fleet manager.',
        look: 'Inbox tab, then the repair panel: approve, pick another workshop, or reject. Approving twice gets a clear rejection from the Update validator. The Decide update is in the Temporal history.',
      },
      {
        id: 'tire-leak',
        title: 'Slow tire leak on a loaded truck',
        what: 'Medium severity, so the case waits until the current load delivers, then ranks workshops, prefers a partner, and books it.',
        look: 'Repair phase goes from "After delivery" to "Sourcing". The "Saved at partners" figure rises when the case closes.',
      },
      {
        id: 'engine-anomaly',
        title: 'Engine overheating on a loaded truck',
        what: 'The stream rule fires and the truck entity opens one repair case, merges related alerts into it, and accepts no new loads until the repair closes.',
        look: 'Truck timeline: anomaly, case opened, repair pending.',
      },
      {
        id: 'pm-due',
        title: 'Preventive maintenance comes due',
        what: 'The odometer passes the PM-A interval. A low-severity case books a partner workshop after the current load.',
        look: 'Repairs tab and the truck timeline.',
      },
      {
        id: 'tire-blowout',
        title: 'Tire blowout on the road',
        what: 'Roadside path: the truck is taken out of service, its load is re-powered, and the nearest capable workshop sends a tow.',
        look: 'Red truck and red dashed tow route on the map; the TMS record shows DOWN.',
      },
    ],
    switches: [
      ['vendorsUnresponsive', 'General workshops stop answering', 'After 2 simulated hours without an answer, the case asks the next workshop. A partner workshop is always the last fallback.'],
      ['vendorsDecline', 'General workshops decline', 'Each decline moves the case to the next option.'],
      ['manualRepairCompletion', 'Workshops report completion by hand', 'Use the repair panel to mark the repair complete on the workshop\u2019s behalf.'],
      ['duplicateEvents', 'Systems send duplicate events', 'Completions and safety events arrive twice. The workflows count them and ignore the duplicates.'],
    ],
  },
  {
    title: 'Dispatch and coordination',
    intro: 'Trucks, loads and repairs depend on each other. Each change goes through the entity it belongs to.',
    scenarios: [
      {
        id: 'breakdown',
        title: 'Loaded truck breaks down',
        what: 'The repair case tells the load workflow its truck is out of service. The load re-plans, a replacement truck drives to the trailer, and the broken truck is towed.',
        look: 'Load panel: planning log and "Roadside trailer transfer". In the Temporal UI, the TruckUnavailable signal on the load.',
      },
      {
        id: 'assign-to-shop-truck',
        title: 'Dispatcher offers a load to a truck in the shop',
        what: 'The truck entity\u2019s update validator refuses the reservation with a reason, and planning moves to the next candidate.',
        look: 'The new load\u2019s planning log.',
        needs: 'Needs a truck with an open repair. Run a maintenance scenario first.',
      },
      {
        id: 'tms-conflict',
        title: 'Someone edits a truck by hand in the TMS',
        what: 'The TMS no longer matches the truck entity. The scheduled reconciliation finds the drift, and the truck entity, the only writer of truck status, publishes its status again.',
        look: 'TMS mismatch pill, then the truck timeline. Systems tab, Equipment reconciliation.',
      },
    ],
  },
  {
    title: 'Drivers',
    intro: 'Driver entities hold hours of service, onboarding and safety events.',
    scenarios: [
      {
        id: 'hard-brake',
        title: 'Hard brake while driving',
        what: 'The driver is driving, so the coaching call is held on the driver entity until the next stop.',
        look: 'Drivers tab: "call held".',
      },
      {
        id: 'driver-break',
        title: 'Driver pulls into a rest area',
        what: 'The ELD reports off duty and the held coaching call starts: a Salesforce case, up to three call attempts, then escalation to a person.',
        look: 'Driver panel, then the coaching workflow it started.',
      },
      {
        id: 'new-hire',
        title: 'New driver hired in Salesforce',
        what: 'A Salesforce change event starts the driver\u2019s workflow. Onboarding steps run on durable timers, and dispatch is refused until they finish.',
        look: 'Drivers tab: onboarding progress.',
      },
    ],
    switches: [['ivrNoAnswer', 'Drivers do not answer calls', 'Coaching retries three times, then escalates to the inbox.']],
  },
  {
    title: 'Failures and recovery',
    intro: 'Outages, expired sessions and dead workers. Retries are a policy on each call, so the workflow code does not change.',
    scenarios: [
      {
        id: 'expire-salesforce-tokens',
        title: 'Salesforce sessions expire',
        what: 'The next Salesforce call gets a 401, re-authenticates, and replays. The workflows never see it.',
        look: 'Systems tab, Salesforce API calls.',
      },
    ],
    switches: [
      ['tmsOutage', 'TMS outage', 'Milestone and status writes retry. When the TMS returns, they land in order, each exactly once.'],
      ['salesforceOutage', 'Salesforce outage', 'Work orders wait, then are created exactly once when Salesforce recovers.'],
      ['salesforceRateLimit', 'Salesforce rate limiting', 'REQUEST_LIMIT_EXCEEDED is retryable; backoff handles it.'],
    ],
    note: 'Kill a worker: close the "Worker: maintenance" window, or run scripts/stop-worker maintenance. Its pill turns red and repair cases pause. Start it again with scripts/worker maintenance and they continue from the same step.',
  },
];

const BACKGROUND = [
  ['organicAnomalies', 'Background anomalies', 'Random faults on the road every few minutes.'],
  ['organicSafetyEvents', 'Background safety events', 'Random hard brakes and following-distance alerts.'],
  ['simulationPaused', 'Pause the simulation', 'Trucks stop moving. Workflows keep their timers.'],
];

function Switch({ checked, onChange, label, calm }) {
  return (
    <label className={`switch ${calm ? 'calm' : ''}`}>
      <input type="checkbox" checked={!!checked} onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  );
}

export default function Runbook({ snapshot, onClose, onResult, setToast }) {
  const [busy, setBusy] = useState(null);
  const chaos = snapshot?.chaos || {};

  async function runScenario(id) {
    setBusy(id);
    try {
      const result = await api.scenario(id, {});
      setToast({ text: result.message });
      onResult(result);
    } catch (e) {
      setToast({ text: e.message, error: true });
    } finally {
      setBusy(null);
    }
  }

  async function toggle(key, value) {
    try {
      await api.setChaos({ [key]: value });
    } catch (e) {
      setToast({ text: e.message, error: true });
    }
  }

  return (
    <aside className="runbook" aria-label="Demo runbook">
      <div className="runbook-head">
        <h2>Demo runbook</h2>
        <button className="small" onClick={onClose}>Close</button>
      </div>
      <div className="runbook-body">
        {ACTS.map((act) => (
          <section key={act.title} className="act">
            <h3>{act.title}</h3>
            <p>{act.intro}</p>
            {act.scenarios.map((s) => (
              <div key={s.id} className="scenario">
                <div className="scenario-top">
                  <h4>{s.title}</h4>
                  <button className="primary small" disabled={busy !== null} onClick={() => runScenario(s.id)}>
                    {busy === s.id ? 'Running' : 'Run'}
                  </button>
                </div>
                <p className="what">{s.what}</p>
                <p className="how"><b>Where to look:</b> {s.look}</p>
                {s.needs && <p className="how">{s.needs}</p>}
              </div>
            ))}
            {(act.switches || []).map(([key, label, how]) => (
              <div key={key} className="scenario">
                <Switch checked={chaos[key]} onChange={(v) => toggle(key, v)} label={label} />
                <p className="how">{how}</p>
              </div>
            ))}
            {act.note && <p className="how" style={{ marginTop: 4 }}>{act.note}</p>}
          </section>
        ))}

        <section className="act">
          <h3>Background</h3>
          <p>Leave these on for a living fleet, or turn them off for a clean run.</p>
          {BACKGROUND.map(([key, label, how]) => (
            <div key={key} className="scenario">
              <Switch calm checked={chaos[key]} onChange={(v) => toggle(key, v)} label={label} />
              <p className="how">{how}</p>
            </div>
          ))}
          <div className="scenario">
            <Switch
              calm
              checked={snapshot?.autoTender}
              onChange={(v) => api.setControls({ autoTender: v }).catch((e) => setToast({ text: e.message, error: true }))}
              label="Tender new loads automatically"
            />
            <p className="how">Keeps about a dozen loads moving.</p>
          </div>
          <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
            <button className="small" disabled={busy !== null} onClick={() => runScenario('tender-load')}>Tender a load now</button>
            <button className="small" disabled={busy !== null} onClick={() => runScenario('run-sync')}>Run equipment sync now</button>
          </div>
        </section>
      </div>
    </aside>
  );
}
