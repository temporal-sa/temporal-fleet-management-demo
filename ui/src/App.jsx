import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from './api.js';
import { entityFromWorkflowId } from './format.js';
import TopBar from './components/TopBar.jsx';
import FleetMap from './components/FleetMap.jsx';
import EventStream from './components/EventStream.jsx';
import Runbook from './components/Runbook.jsx';
import { DriverList, Inbox, LoadList, RepairList, SystemsPanel, TruckList } from './components/Lists.jsx';
import { CoachingDetail, DriverDetail, LoadDetail, RepairDetail, TruckDetail } from './components/Details.jsx';
import { useToast } from './components/ui.jsx';

function usePoll(fn, ms, enabled = true) {
  const saved = useRef(fn);
  saved.current = fn;
  useEffect(() => {
    if (!enabled) return undefined;
    let alive = true;
    const tick = async () => {
      if (!alive) return;
      try {
        await saved.current();
      } catch {
        // Keep polling; the offline overlay covers persistent failures.
      }
    };
    tick();
    const t = setInterval(tick, ms);
    return () => {
      alive = false;
      clearInterval(t);
    };
  }, [ms, enabled]);
}

const TABS = [
  ['trucks', 'Trucks'],
  ['loads', 'Loads'],
  ['repairs', 'Repairs'],
  ['drivers', 'Drivers'],
  ['inbox', 'Inbox'],
  ['systems', 'Systems'],
];

export default function App() {
  const [snapshot, setSnapshot] = useState(null);
  const [network, setNetwork] = useState(null);
  const [events, setEvents] = useState([]);
  const [integrations, setIntegrations] = useState(null);
  const [tab, setTab] = useState('trucks');
  const [selected, setSelected] = useState(null);
  const [runbookOpen, setRunbookOpen] = useState(false);
  const [showVendors, setShowVendors] = useState(false);
  const [apiDown, setApiDown] = useState(false);
  const lastSeq = useRef(0);
  const [toastNode, setToast] = useToast();

  usePoll(async () => {
    try {
      const s = await api.snapshot();
      setSnapshot(s);
      setApiDown(false);
    } catch (e) {
      setApiDown(true);
      throw e;
    }
  }, 1500);

  usePoll(async () => {
    if (!network) setNetwork(await api.network());
  }, 5000, !network);

  usePoll(async () => {
    const fresh = await api.events(lastSeq.current);
    if (Array.isArray(fresh) && fresh.length) {
      lastSeq.current = Math.max(lastSeq.current, fresh[fresh.length - 1].seq);
      setEvents((prev) => {
        const seen = new Set(prev.map((e) => e.seq));
        return [...prev, ...fresh.filter((e) => !seen.has(e.seq))].slice(-300);
      });
    }
  }, 2000);

  const needIntegrations = tab === 'systems' || selected?.type === 'repair' || selected?.type === 'coaching';
  usePoll(async () => setIntegrations(await api.integrations()), 2500, needIntegrations);

  const select = useCallback((target) => {
    setSelected(target);
    setRunbookOpen(false);
  }, []);

  const onScenarioResult = useCallback(
    (result) => {
      const target = entityFromWorkflowId(result.workflowId) || (result.unit ? { type: 'truck', id: result.unit } : null);
      if (target) select(target);
    },
    [select],
  );

  async function tenderLoad() {
    try {
      const r = await api.tender({});
      setToast({ text: `Load ${r.loadId} tendered.` });
      select({ type: 'load', id: r.loadId });
    } catch (e) {
      setToast({ text: e.message, error: true });
    }
  }

  const inboxCount = snapshot?.inbox?.length || 0;
  const common = { snapshot, network, onSelect: select, onBack: () => setSelected(null), setToast };

  let detail = null;
  if (selected?.type === 'truck') detail = <TruckDetail {...common} unit={selected.id} />;
  if (selected?.type === 'load') detail = <LoadDetail {...common} loadId={selected.id} />;
  if (selected?.type === 'repair') detail = <RepairDetail {...common} integrations={integrations} maintenanceId={selected.id} />;
  if (selected?.type === 'driver') detail = <DriverDetail {...common} driverId={selected.id} />;
  if (selected?.type === 'coaching') detail = <CoachingDetail {...common} integrations={integrations} coachingId={selected.id} />;

  return (
    <div className="app">
      <TopBar snapshot={snapshot} onOpenRunbook={() => setRunbookOpen(true)} />

      <div style={{ gridArea: 'map', position: 'relative', minHeight: 0 }}>
        <FleetMap
          network={network}
          snapshot={snapshot}
          selected={selected}
          onSelect={select}
          showVendors={showVendors}
          onToggleVendors={() => setShowVendors((v) => !v)}
        />
        {runbookOpen && <Runbook snapshot={snapshot} onClose={() => setRunbookOpen(false)} onResult={onScenarioResult} setToast={setToast} />}
        {(apiDown || (snapshot && !snapshot.temporalConnected)) && (
          <div className="offline">
            <div>
              <h2>{apiDown ? 'Waiting for the fleet API' : 'Waiting for Temporal'}</h2>
              <p>
                {apiDown
                  ? 'Start Fleet.Api on port 5100 (scripts/start-all). This page reconnects on its own.'
                  : 'Start the Temporal dev server on port 7233 (scripts/start-temporal). This page reconnects on its own.'}
              </p>
            </div>
          </div>
        )}
      </div>

      <aside className="side" aria-label="Fleet details">
        {detail ? (
          <div className="side-body">{detail}</div>
        ) : (
          <>
            <nav className="tabs" role="tablist">
              {TABS.map(([key, label]) => (
                <button key={key} role="tab" aria-selected={tab === key} className={`tab ${tab === key ? 'active' : ''}`} onClick={() => setTab(key)}>
                  {label}
                  {key === 'inbox' && inboxCount > 0 && <span className="count">{inboxCount}</span>}
                </button>
              ))}
            </nav>
            <div className="side-body">
              {tab === 'trucks' && <TruckList snapshot={snapshot} network={network} selected={selected} onSelect={select} />}
              {tab === 'loads' && <LoadList snapshot={snapshot} network={network} selected={selected} onSelect={select} onTender={tenderLoad} />}
              {tab === 'repairs' && <RepairList snapshot={snapshot} selected={selected} onSelect={select} />}
              {tab === 'drivers' && <DriverList snapshot={snapshot} selected={selected} onSelect={select} />}
              {tab === 'inbox' && <Inbox snapshot={snapshot} onSelect={select} />}
              {tab === 'systems' && <SystemsPanel snapshot={snapshot} integrations={integrations} onSelect={select} />}
            </div>
          </>
        )}
      </aside>

      <EventStream events={events} onSelect={select} />
      {toastNode}
    </div>
  );
}
