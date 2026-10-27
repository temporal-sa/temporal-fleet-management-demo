import { clock, entityFromWorkflowId } from '../format.js';

export default function EventStream({ events, onSelect }) {
  const recent = [...events].reverse().slice(0, 80);
  return (
    <section className="stream" aria-label="Event stream">
      <div className="stream-head">
        <strong>What the outside world did</strong>
        <span className="muted">Telematics rules, geofences, workshop answers, Salesforce and TMS activity, and the signals sent to Temporal</span>
      </div>
      <ul className="stream-list">
        {recent.map((e) => {
          const target = entityFromWorkflowId(e.workflowId);
          return (
            <li key={e.seq} className={target ? 'link' : ''} onClick={() => target && onSelect(target)}>
              <time>{clock(e.at)}</time>
              <span className={`sys sys-${e.system}`}>{e.system}</span>
              <span className={e.kind === 'warn' || e.kind === 'error' ? 'warn' : ''}>{e.message}</span>
            </li>
          );
        })}
        {recent.length === 0 && <li><span /><span /><span className="muted">Waiting for events.</span></li>}
      </ul>
    </section>
  );
}
