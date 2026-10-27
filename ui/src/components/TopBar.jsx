import { money, simClock } from '../format.js';

const QUEUE_LABELS = {
  'fleet-entities': 'Entities',
  'fleet-dispatch': 'Dispatch',
  'fleet-maintenance': 'Maintenance',
  'fleet-integrations': 'Integrations',
};

export default function TopBar({ snapshot, onOpenRunbook }) {
  const k = snapshot?.kpis || {};
  const chaos = snapshot?.chaos || {};
  const outages = [
    chaos.tmsOutage && ['TMS down', ''],
    chaos.salesforceOutage && ['Salesforce down', ''],
    chaos.salesforceRateLimit && ['Salesforce rate limited', ''],
    chaos.vendorsUnresponsive && ['Workshops not answering', 'soft'],
    chaos.duplicateEvents && ['Duplicate events on', 'soft'],
    chaos.simulationPaused && ['Simulation paused', 'soft'],
  ].filter(Boolean);

  return (
    <header className="topbar">
      <div className="sign">
        <div className="sign-inner">
          <span className="sign-title">Fleet control</span>
          <span className="sign-sub">on Temporal</span>
        </div>
      </div>

      <div className="clock" title="Simulated time. One real second is several simulated minutes.">
        <strong>{snapshot?.simTime ? simClock(snapshot.simTime) : 'Connecting'}</strong>
        <span>{snapshot ? `${snapshot.timeScale}x speed` : ''}</span>
      </div>

      <div className="kpis">
        <div className="kpi"><strong>{k.trucksAvailable ?? '0'}</strong><span>Available</span></div>
        <div className="kpi"><strong>{k.trucksOnLoad ?? '0'}</strong><span>On loads</span></div>
        <div className="kpi attention"><strong>{k.trucksInShop ?? '0'}</strong><span>Shop</span></div>
        <div className={`kpi ${k.trucksDown ? 'alert' : ''}`}><strong>{k.trucksDown ?? '0'}</strong><span>Down</span></div>
        <div className="kpi money" title="General workshop baseline minus actual cost, for repairs done at partner workshops">
          <strong>{money(k.savingsCaptured || 0)}</strong>
          <span>Saved at partners</span>
        </div>
        <div className={`kpi ${k.pendingApprovals ? 'attention' : ''}`}><strong>{k.pendingApprovals ?? '0'}</strong><span>Approvals</span></div>
        <div className="kpi"><strong>{k.activeLoads ?? '0'}</strong><span>Active loads</span></div>
        <div className="kpi"><strong>{k.repowers ?? '0'}</strong><span>Re-powers</span></div>
        <div className="kpi"><strong>{k.duplicatesAbsorbed ?? '0'}</strong><span>Duplicates absorbed</span></div>
        <div className="kpi"><strong>{k.deliveredLoads ?? '0'}</strong><span>Delivered</span></div>
      </div>

      <div style={{ display: 'flex', flexDirection: 'column', gap: 5, alignItems: 'flex-end' }}>
        <div className="workers" aria-label="Worker processes">
          {(snapshot?.workers || []).map((w) => (
            <span key={w.queue} className={`worker-pill ${w.online ? '' : 'down'}`} title={`${w.queue}: ${w.instances.length} process(es)`}>
              <span className="dot" />
              {QUEUE_LABELS[w.queue] || w.queue}
            </span>
          ))}
        </div>
        {outages.length > 0 && (
          <div className="banner-row">
            {outages.map(([text, tone]) => (
              <span key={text} className={`outage ${tone}`}>{text}</span>
            ))}
          </div>
        )}
      </div>

      <button className="primary" onClick={onOpenRunbook}>Demo runbook</button>
    </header>
  );
}
