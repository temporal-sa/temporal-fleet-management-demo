// Formatting and lookup helpers shared by the UI.

export const TRUCK_STATUS = {
  Available: { label: 'Available', tone: 'green', color: '#2e7d4f' },
  Reserved: { label: 'Reserved', tone: 'blue', color: '#1d4f91' },
  OnLoad: { label: 'On a load', tone: 'blue', color: '#1d4f91' },
  ShopBound: { label: 'Heading to shop', tone: 'amber', color: '#c98500' },
  InShop: { label: 'In the shop', tone: 'amber', color: '#c98500' },
  Breakdown: { label: 'Broken down', tone: 'red', color: '#b42318' },
  OutOfService: { label: 'Out of service', tone: 'red', color: '#b42318' },
};

export const TMS_FOR = {
  Available: 'AVAILABLE',
  Reserved: 'ASSIGNED',
  OnLoad: 'ASSIGNED',
  ShopBound: 'IN_REPAIR',
  InShop: 'IN_REPAIR',
  Breakdown: 'DOWN',
  OutOfService: 'OUT_OF_SERVICE',
};

export const LOAD_STEPS = ['Tendered', 'Planning', 'Assigned', 'EnRouteToPickup', 'AtPickup', 'InTransit', 'AtDelivery', 'Delivered'];
export const LOAD_STEP_LABELS = ['Tendered', 'Planning', 'Assigned', 'To pickup', 'At pickup', 'In transit', 'At delivery', 'Delivered'];

export const REPAIR_STEPS = ['Opened', 'WaitingForLoadCompletion', 'Triage', 'AwaitingApproval', 'Sourcing', 'EnRouteToShop', 'InRepair', 'Completed'];
export const REPAIR_STEP_LABELS = ['Opened', 'After delivery', 'Triage', 'Approval', 'Sourcing', 'To shop', 'In repair', 'Closed'];

export function truckTone(status) {
  return TRUCK_STATUS[status]?.tone ?? 'gray';
}

export function truckColor(status) {
  return TRUCK_STATUS[status]?.color ?? '#8a948e';
}

export function loadTone(status) {
  if (status === 'Delivered') return 'green';
  if (status === 'Repowering' || status === 'WaitingForCapacity') return 'red';
  if (status === 'AtPickup' || status === 'AtDelivery') return 'amber';
  return 'blue';
}

export function repairTone(phase) {
  if (phase === 'Completed') return 'green';
  if (phase === 'Cancelled') return 'gray';
  if (phase === 'AwaitingApproval') return 'amber';
  return 'blue';
}

const SPLIT = /(?<=[a-z])(?=[A-Z])/g;
export function words(value) {
  if (!value) return '';
  const text = String(value).replace(SPLIT, ' ').toLowerCase();
  return text.charAt(0).toUpperCase() + text.slice(1);
}

export function money(value) {
  if (value === null || value === undefined) return '';
  return `$${Math.round(Number(value)).toLocaleString('en-US')}`;
}

export function miles(value) {
  return `${Math.round(Number(value || 0)).toLocaleString('en-US')} mi`;
}

export function hours(minutes) {
  return `${(Number(minutes || 0) / 60).toFixed(1)} h`;
}

export function clock(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
}

export function simClock(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const day = d.toLocaleDateString('en-US', { weekday: 'short', timeZone: 'America/Chicago' });
  const time = d.toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: 'America/Chicago' });
  return `${day} ${time} CT`;
}

export function ago(iso) {
  if (!iso) return '';
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return `${Math.round(seconds)}s ago`;
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
  return `${Math.round(seconds / 3600)}h ago`;
}

export function makeHubs(network) {
  const byKey = {};
  (network?.hubs || []).forEach((h) => {
    byKey[h.key] = h;
  });
  return byKey;
}

export function hubLabel(hubs, key) {
  const h = hubs[key];
  return h ? `${h.city}, ${h.state}` : key;
}

export function nearestHubLabel(network, lat, lon) {
  if (!network?.hubs?.length || lat === undefined) return '';
  let best = null;
  let bestD = Infinity;
  for (const h of network.hubs) {
    const d = (h.lat - lat) ** 2 + ((h.lon - lon) * Math.cos((lat * Math.PI) / 180)) ** 2;
    if (d < bestD) {
      bestD = d;
      best = h;
    }
  }
  const approxMiles = Math.sqrt(bestD) * 69;
  return approxMiles < 15 ? `${best.city}, ${best.state}` : `${Math.round(approxMiles)} mi from ${best.city}`;
}

/** Deep link into the Temporal Web UI for one workflow. */
export function temporalUrl(snapshot, workflowId, runId) {
  const base = snapshot?.temporalUi || 'http://localhost:8233';
  const ns = encodeURIComponent(snapshot?.namespace || 'default');
  if (runId) {
    return `${base}/namespaces/${ns}/workflows/${encodeURIComponent(workflowId)}/${runId}/history`;
  }
  return temporalQueryUrl(snapshot, `WorkflowId="${workflowId}"`);
}

/** Deep link into the Temporal Web UI workflow list, filtered by a visibility query. */
export function temporalQueryUrl(snapshot, query) {
  const base = snapshot?.temporalUi || 'http://localhost:8233';
  const ns = encodeURIComponent(snapshot?.namespace || 'default');
  return `${base}/namespaces/${ns}/workflows?query=${encodeURIComponent(query)}`;
}

/** Maps a workflow ID to the UI entity it represents, so log lines can open details. */
export function entityFromWorkflowId(workflowId) {
  if (!workflowId) return null;
  if (workflowId.startsWith('truck-')) return { type: 'truck', id: workflowId.slice(6) };
  if (workflowId.startsWith('driver-')) return { type: 'driver', id: workflowId.slice(7) };
  if (workflowId.startsWith('load-')) return { type: 'load', id: workflowId.slice(5) };
  if (workflowId.startsWith('maint-')) return { type: 'repair', id: workflowId };
  if (workflowId.startsWith('coaching-')) return { type: 'coaching', id: workflowId };
  return null;
}
