// Thin client for Fleet.Api. Every call goes through the Vite proxy (/api -> http://localhost:5100).

async function request(method, path, body) {
  const response = await fetch(path, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = text;
  }
  if (!response.ok) {
    const message = (data && (data.message || data.Message)) || (typeof data === 'string' ? data : `Request failed (${response.status})`);
    throw new Error(message);
  }
  return data;
}

export const api = {
  snapshot: () => request('GET', '/api/snapshot'),
  network: () => request('GET', '/api/network'),
  events: (after) => request('GET', `/api/events?after=${after || 0}`),
  integrations: () => request('GET', '/api/integrations'),
  pending: (workflowId) => request('GET', `/api/workflows/${encodeURIComponent(workflowId)}/pending`),
  scenario: (name, body) => request('POST', `/api/scenarios/${name}`, body || {}),
  setChaos: (patch) => request('PUT', '/api/chaos', patch),
  setControls: (patch) => request('PUT', '/api/controls', patch),
  decide: (maintenanceId, decision) => request('POST', `/api/repairs/${encodeURIComponent(maintenanceId)}/decision`, decision),
  cancelRepair: (maintenanceId, reason) => request('POST', `/api/repairs/${encodeURIComponent(maintenanceId)}/cancel`, { reason }),
  resolveCoaching: (coachingId, body) => request('POST', `/api/coaching/${encodeURIComponent(coachingId)}/resolve`, body),
  vendorAction: (requestId, action, note) => request('POST', `/api/vendors/${encodeURIComponent(requestId)}/${action}`, { note }),
  ackCall: (callId) => request('POST', `/api/ivr/${encodeURIComponent(callId)}/acknowledge`),
  tender: (body) => request('POST', '/api/loads/tender', body || {}),
  nudge: (loadId) => request('POST', `/api/loads/${encodeURIComponent(loadId)}/nudge`),
  runSync: () => request('POST', '/api/sync/run'),
};
