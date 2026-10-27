import { useEffect, useState } from 'react';
import { api } from '../api.js';
import { clock, temporalUrl, truckTone, words } from '../format.js';

export function ExternalIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.4" aria-hidden="true">
      <path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

export function Plate({ unit, status }) {
  return <span className={`plate ${truckTone(status) === 'green' ? '' : truckTone(status)}`}>{unit}</span>;
}

export function Pill({ tone = 'gray', children, title }) {
  return (
    <span className={`pill ${tone}`} title={title}>
      {children}
    </span>
  );
}

/** Opens the workflow's event history in the Temporal Web UI. */
export function TemporalLink({ snapshot, workflowId, runId, label = 'Open in Temporal' }) {
  if (!workflowId) return null;
  return (
    <a className="temporal-link" href={temporalUrl(snapshot, workflowId, runId)} target="_blank" rel="noreferrer" title={workflowId}>
      {label} <ExternalIcon />
    </a>
  );
}

export function Section({ title, aside, children, actions }) {
  return (
    <section className="side-section">
      <h3>
        {title}
        {aside && <span className="aside">{aside}</span>}
        {actions && <span style={{ float: 'right' }}>{actions}</span>}
      </h3>
      {children}
    </section>
  );
}

export function KV({ items }) {
  return (
    <dl className="kv">
      {items
        .filter((i) => i && i[1] !== undefined && i[1] !== null && i[1] !== '')
        .map(([k, v]) => (
          <div key={k} style={{ display: 'contents' }}>
            <dt>{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
    </dl>
  );
}

export function Timeline({ events, limit = 40 }) {
  if (!events?.length) return <p className="empty">Nothing recorded yet.</p>;
  const ordered = [...events].reverse().slice(0, limit);
  return (
    <ol className="timeline">
      {ordered.map((e, i) => (
        <li key={`${e.at}-${i}`}>
          <time>{clock(e.at).slice(0, 5)}</time>
          <span className={`k-${e.kind}`}>{e.text}</span>
        </li>
      ))}
    </ol>
  );
}

export function Track({ steps, labels, current }) {
  const index = steps.indexOf(current);
  return (
    <div>
      <div className="track" aria-label={`Progress: ${words(current)}`}>
        {steps.map((s, i) => (
          <div key={s} className={`step ${i < index || current === steps[steps.length - 1] ? 'done' : i === index ? 'now' : ''}`} />
        ))}
      </div>
      <div className="track-labels">
        <span>{labels[0]}</span>
        <span>{index >= 0 ? labels[index] : words(current)}</span>
        <span>{labels[labels.length - 1]}</span>
      </div>
    </div>
  );
}

export function Gauge({ label, value, display, min, max, warnBelow, badBelow, warnAbove, badAbove }) {
  const pct = Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
  let level = '';
  if ((badBelow !== undefined && value <= badBelow) || (badAbove !== undefined && value >= badAbove)) level = 'bad';
  else if ((warnBelow !== undefined && value <= warnBelow) || (warnAbove !== undefined && value >= warnAbove)) level = 'warn';
  return (
    <div className={`gauge ${level}`}>
      <span>
        {label} <strong>{display}</strong>
      </span>
      <div className="bar">
        <i style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

/** Polls Temporal for the workflow's pending activities: shows retries during an outage. */
export function PendingActivities({ workflowId }) {
  const [data, setData] = useState(null);
  useEffect(() => {
    let alive = true;
    const load = () =>
      api
        .pending(workflowId)
        .then((d) => alive && setData(d))
        .catch(() => alive && setData(null));
    load();
    const t = setInterval(load, 2500);
    return () => {
      alive = false;
      clearInterval(t);
    };
  }, [workflowId]);

  const pending = data?.pendingActivities || [];
  return (
    <div>
      <p style={{ margin: '0 0 4px', fontSize: 12, color: 'var(--muted)' }}>
        {data ? `${data.historyLength} events in history. ` : ''}
        {pending.length === 0 ? 'No activity is waiting.' : ''}
      </p>
      {pending.map((p, i) => (
        <div className="pending" key={i}>
          <strong>{p.activity}</strong> attempt {p.attempt} ({words(p.state?.replace('PendingActivityState', '') || '')})
          {p.lastFailure && <div>Last failure: {p.lastFailure}</div>}
        </div>
      ))}
    </div>
  );
}

export function useToast() {
  const [toast, setToast] = useState(null);
  useEffect(() => {
    if (!toast) return undefined;
    const t = setTimeout(() => setToast(null), toast.error ? 6000 : 4200);
    return () => clearTimeout(t);
  }, [toast]);
  const node = toast ? <div className={`toast ${toast.error ? 'error' : ''}`} role="status">{toast.text}</div> : null;
  return [node, setToast];
}
