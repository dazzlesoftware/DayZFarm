// DayZ Client Farm dashboard. Plain JS + SignalR — no build step required.

const state = { clients: [] };

function badge(text, cls) {
  return `<span class="badge badge-${cls}">${text}</span>`;
}

function vmBadge(status) {
  const map = { Running: 'running', Off: 'off', Starting: 'starting', Stopping: 'starting', Saved: 'off', Paused: 'off', Error: 'error', Unknown: 'off' };
  return badge(status, map[status] ?? 'off');
}

function agentBadge(c) {
  if (c.agentOnline) return badge('Online', 'online');
  if (c.vmStatus !== 'Running') return badge('Off', 'off');
  if (!c.tokenRegistered) return badge('Awaiting Token', 'starting');
  return badge('Unreachable', 'error');
}

function connBadge(status) {
  const map = {
    Offline: 'offline', StartingSteam: 'starting', SteamReady: 'ready', Updating: 'updating',
    StartingDayZ: 'starting', Connecting: 'connecting', Connected: 'connected',
    Disconnected: 'disconnected', Error: 'error'
  };
  return badge(status, map[status] ?? 'offline');
}

function fmtUptime(ticks) {
  // ticks is a .NET TimeSpan serialized as "d.hh:mm:ss" or similar; fall back to raw string.
  if (!ticks) return '-';
  return typeof ticks === 'string' ? ticks.split('.').slice(-1)[0] : String(ticks);
}

function renderSummary(s) {
  document.getElementById('s-total').textContent = s.totalClients ?? 0;
  document.getElementById('s-running').textContent = s.runningVms ?? 0;
  document.getElementById('s-connected').textContent = s.connectedClients ?? 0;
  document.getElementById('s-updating').textContent = s.updating ?? 0;
  document.getElementById('s-errors').textContent = s.errors ?? 0;
  document.getElementById('s-offline').textContent = s.offline ?? 0;
}

function renderClients(clients) {
  state.clients = clients;
  const tbody = document.getElementById('clients-body');
  tbody.innerHTML = clients.map(c => `
    <tr data-id="${c.id}">
      <td><input type="checkbox" class="row-select" data-id="${c.id}" /></td>
      <td>${c.name}</td>
      <td>${c.steamAccountName ?? '-'}</td>
      <td>${vmBadge(c.vmStatus)}</td>
      <td>${agentBadge(c)}</td>
      <td>${c.steamRunning ? (c.steamLoggedIn ? badge('Ready', 'ready') : badge('Login needed', 'starting')) : badge('Stopped', 'off')}</td>
      <td>${c.dayZRunning ? badge('Running', 'running') : badge('Stopped', 'off')}</td>
      <td>${connBadge(c.connectionStatus)}</td>
      <td>${c.serverAddress}:${c.serverPort}</td>
      <td>${fmtUptime(c.vmUptime)}</td>
      <td class="row-actions">
        <button data-row-action="vm/start" data-id="${c.id}">Start VM</button>
        <button data-row-action="vm/stop" data-id="${c.id}">Stop VM</button>
        <button data-row-action="vm/restart" data-id="${c.id}">Restart VM</button>
        <button data-row-action="steam/start" data-id="${c.id}">Start Steam</button>
        <button data-row-action="dayz/start" data-id="${c.id}">Start DayZ</button>
        <button data-row-action="dayz/stop" data-id="${c.id}">Stop DayZ</button>
        <button data-row-action="join" data-id="${c.id}">Join</button>
        <button data-row-action="update" data-id="${c.id}">Update</button>
        <a href="/detail.html?id=${c.id}"><button type="button">Details</button></a>
        <button data-row-special="register-token" data-id="${c.id}" data-name="${c.name}">Register Token</button>
        <button data-row-special="delete" data-id="${c.id}" data-name="${c.name}" class="danger">Delete</button>
      </td>
    </tr>
  `).join('');
}

async function api(path, opts) {
  const res = await fetch(path, Object.assign({ headers: { 'Content-Type': 'application/json' } }, opts));
  if (!res.ok) {
    console.error('API call failed', path, res.status);
  }
  return res;
}

// Row/detail actions previously gave no feedback at all on success or failure, leaving a
// failure (e.g. "no token registered") silently invisible unless you happened to check the
// Logs panel separately. Every action response now surfaces here instead.
function showToast(message, isError) {
  let el = document.getElementById('toast');
  if (!el) {
    el = document.createElement('div');
    el.id = 'toast';
    el.style.cssText = 'position:fixed;bottom:20px;right:20px;max-width:420px;padding:12px 16px;border-radius:8px;font-size:13px;z-index:1000;box-shadow:0 4px 16px rgba(0,0,0,0.4);';
    document.body.appendChild(el);
  }
  el.style.background = isError ? '#4a1d1f' : '#1d3a26';
  el.style.color = isError ? '#f5b5b8' : '#a8e6bd';
  el.style.border = `1px solid ${isError ? '#e5484d' : '#35c76a'}`;
  el.textContent = message;
  el.style.display = 'block';
  clearTimeout(el._hideTimer);
  el._hideTimer = setTimeout(() => { el.style.display = 'none'; }, 6000);
}

// Runs a row/detail action and surfaces its {success, message} result as a toast, since the
// underlying agent/VM call can fail for reasons the user needs to see (no token registered, VM
// not running, agent unreachable, ...) rather than just silently doing nothing.
async function runActionAndReport(path) {
  const res = await api(path, { method: 'POST' });
  let body = null;
  try { body = await res.json(); } catch { /* no JSON body (e.g. 204/404) */ }
  const message = body?.message ?? (res.ok ? 'Done.' : `Request failed (${res.status}).`);
  showToast(message, !(body?.success ?? res.ok));
  return body;
}

function selectedIds() {
  return Array.from(document.querySelectorAll('.row-select:checked')).map(el => parseInt(el.dataset.id, 10));
}

function confirmAction(message) {
  return new Promise(resolve => {
    const modal = document.getElementById('confirm-modal');
    document.getElementById('confirm-text').textContent = message;
    modal.classList.remove('hidden');
    const cleanup = () => { modal.classList.add('hidden'); yes.removeEventListener('click', onYes); no.removeEventListener('click', onNo); };
    const yes = document.getElementById('confirm-yes');
    const no = document.getElementById('confirm-no');
    const onYes = () => { cleanup(); resolve(true); };
    const onNo = () => { cleanup(); resolve(false); };
    yes.addEventListener('click', onYes);
    no.addEventListener('click', onNo);
  });
}

document.getElementById('select-all').addEventListener('change', e => {
  document.querySelectorAll('.row-select').forEach(cb => cb.checked = e.target.checked);
});

document.getElementById('clients-body').addEventListener('click', async e => {
  const actionBtn = e.target.closest('button[data-row-action]');
  if (actionBtn) {
    await runActionAndReport(`/api/clients/${actionBtn.dataset.id}/${actionBtn.dataset.rowAction}`);
    return;
  }

  const specialBtn = e.target.closest('button[data-row-special]');
  if (!specialBtn) return;
  const { id, name } = specialBtn.dataset;

  if (specialBtn.dataset.rowSpecial === 'register-token') {
    const token = prompt(
      `Paste ${name}'s agent token (from %ProgramData%\\DayZFarmAgent\\agent-token.secret inside that VM):`
    );
    if (!token) return;
    const res = await api(`/api/clients/${id}/agent/register-token`, {
      method: 'POST',
      body: JSON.stringify({ clientName: name, token })
    });
    showToast(res.ok ? `Token registered for ${name}. It may take a few seconds to show as Online.` : `Failed to register token (${res.status}).`, !res.ok);
  } else if (specialBtn.dataset.rowSpecial === 'delete') {
    const ok = await confirmAction(`Permanently delete ${name} and its VM/disk? This cannot be undone.`);
    if (!ok) return;
    await api(`/api/clients/${id}`, { method: 'DELETE' });
    await bootstrap();
  }
});

document.querySelector('.bulk-actions').addEventListener('click', async e => {
  const btn = e.target.closest('button[data-action]');
  if (!btn) return;
  const ids = selectedIds();
  if (ids.length === 0) { alert('Select at least one client.'); return; }
  const res = await api(`/api/clients/${btn.dataset.action}`, { method: 'POST', body: JSON.stringify(ids) });
  try {
    const results = await res.json();
    const failed = results.filter(r => r.success === false || r.result?.success === false);
    showToast(failed.length === 0 ? `${results.length} succeeded.` : `${results.length - failed.length} succeeded, ${failed.length} failed — see console for details.`, failed.length > 0);
    if (failed.length > 0) console.warn('Bulk action failures', failed);
  } catch { /* no JSON body */ }
});

document.querySelector('.global-actions').addEventListener('click', async e => {
  const btn = e.target.closest('button[data-global]');
  if (!btn) return;
  const action = btn.dataset.global;
  const ok = await confirmAction(`Are you sure you want to run "${action}" on ALL clients?`);
  if (!ok) return;
  await api(`/api/clients/${action}`, { method: 'POST' });
});

async function bootstrap() {
  const res = await api('/api/clients');
  const clients = await res.json();
  renderClients(clients);
}

// ---- Create Client / Create Clients (Range) modals ----

function openModal(id) { document.getElementById(id).classList.remove('hidden'); }
function closeModal(id) { document.getElementById(id).classList.add('hidden'); }

document.querySelectorAll('button[data-close-modal]').forEach(btn =>
  btn.addEventListener('click', () => closeModal(btn.dataset.closeModal)));

document.getElementById('create-client-btn').addEventListener('click', () => openModal('create-client-modal'));
document.getElementById('create-clients-bulk-btn').addEventListener('click', () => openModal('create-clients-bulk-modal'));

function parseMods(value) {
  return (value || '').split(',').map(s => s.trim()).filter(Boolean);
}

document.getElementById('create-client-form').addEventListener('submit', async e => {
  e.preventDefault();
  const f = new FormData(e.target);
  const body = {
    name: f.get('name'),
    vmName: f.get('vmName'),
    steamAccountName: f.get('steamAccountName') || null,
    serverAddress: f.get('serverAddress'),
    serverPort: parseInt(f.get('serverPort'), 10),
    requiredMods: parseMods(f.get('requiredMods')),
    autoStart: f.get('autoStart') === 'on',
    autoReconnect: f.get('autoReconnect') === 'on',
    autoUpdate: f.get('autoUpdate') === 'on'
  };

  const res = await api('/api/clients', { method: 'POST', body: JSON.stringify(body) });
  if (res.ok) {
    e.target.reset();
    closeModal('create-client-modal');
    await bootstrap();
  } else {
    const err = await res.json().catch(() => ({ error: 'Unknown error' }));
    alert(`Failed to create client: ${err.error ?? res.status}`);
  }
});

document.getElementById('create-clients-bulk-form').addEventListener('submit', async e => {
  e.preventDefault();
  const f = new FormData(e.target);
  const body = {
    startIndex: parseInt(f.get('startIndex'), 10),
    count: parseInt(f.get('count'), 10),
    serverAddress: f.get('serverAddress'),
    serverPort: parseInt(f.get('serverPort'), 10),
    requiredMods: parseMods(f.get('requiredMods')),
    autoStart: f.get('autoStart') === 'on',
    autoReconnect: f.get('autoReconnect') === 'on',
    autoUpdate: f.get('autoUpdate') === 'on'
  };

  const res = await api('/api/clients/bulk-create', { method: 'POST', body: JSON.stringify(body) });
  const results = await res.json();
  const resultsEl = document.getElementById('bulk-create-results');
  resultsEl.innerHTML = results.map(r =>
    `<div>${r.success ? '✅' : '❌'} ${r.name}${r.success ? '' : ` — ${r.error}`}</div>`
  ).join('');

  await bootstrap();
});

const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/farm-status')
  .withAutomaticReconnect()
  .build();

connection.on('ClientsUpdated', renderClients);
connection.on('SummaryUpdated', renderSummary);

connection.start().catch(err => console.error('SignalR connection failed', err));

bootstrap();
