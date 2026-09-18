const params = new URLSearchParams(location.search);
const id = params.get('id');

const rowActions = [
  ['vm/start', 'Start VM'], ['vm/shutdown', 'Shutdown'], ['vm/restart', 'Restart VM'],
  ['steam/start', 'Start Steam'], ['steam/restart', 'Restart Steam'],
  ['game/start', 'Start Game'], ['game/stop', 'Stop Game'], ['game/restart', 'Restart Game'],
  ['join', 'Join Server'], ['update', 'Update Game'],
  // Runs the "initialize-vm" guest plugin (see docs/PLUGINS.md and config/AgentPlugins/
  // initialize-vm.json) -- sets BEService to auto-start, which every fresh clone otherwise
  // needs done once before BattlEye will actually work (see docs/TROUBLESHOOTING.md). Reuses
  // the exact same /api/clients/{id}/{route} shape as every other button here, since
  // /api/clients/{id}/plugins/{name}/run fits that pattern exactly -- no separate JS needed.
  ['plugins/initialize-vm/run', 'Initialize VM'],
  // Pushes the Controller's currently uploaded Agent package (see docs/AGENT-UPDATES.md) to
  // this client, which applies it to itself and restarts. Same generic route shape as every
  // other button here.
  ['agent/update-package', 'Update Agent']
];

function field(label, value) {
  return `<div><strong>${label}:</strong> ${value ?? '-'}</div>`;
}

function escapeHtml(text) {
  return String(text ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

// Action buttons previously gave no feedback on success/failure at all -- a failure (e.g. "no
// token registered") was silently invisible unless you happened to check the Logs panel.
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

async function loadDetail() {
  const res = await fetch(`/api/clients/${id}`);
  if (!res.ok) { document.getElementById('detail').textContent = 'Client not found.'; return; }
  const c = await res.json();

  const form = document.getElementById('edit-form');
  form.elements['steamAccountName'].value = c.steamAccountName ?? '';
  form.elements['serverAddress'].value = c.serverAddress ?? '';
  form.elements['serverPort'].value = c.serverPort ?? '';
  form.elements['requiredMods'].value = (c.requiredMods ?? []).join(',');
  form.elements['autoStart'].checked = !!c.autoStart;
  form.elements['autoReconnect'].checked = !!c.autoReconnect;
  form.elements['autoUpdate'].checked = !!c.autoUpdate;
  form.elements['serverPassword'].value = '';
  form.elements['clearServerPassword'].checked = false;
  document.getElementById('server-password-status').textContent = c.serverPasswordSet ? '(currently set)' : '(not set)';

  document.getElementById('detail').innerHTML = [
    field('VM name', c.vmName),
    field('VM status', c.vmStatus),
    field('CPU allocation', c.cpuCount),
    field('RAM allocation (bytes)', c.memoryStartupBytes),
    field('Guest IP', c.guestIpAddress),
    field('Agent token registered', c.tokenRegistered),
    field('Steam account', c.steamAccountName),
    field('Steam running', c.steamRunning),
    field('Steam logged in', c.steamLoggedIn),
    field('Game running', c.gameRunning),
    field('Game PID', c.gameProcessId),
    field('Game version', c.gameVersion),
    field('Connection state', c.connectionStatus),
    field('Target server', `${c.serverAddress}:${c.serverPort}`),
    field('Last error', c.lastError),
    field('VM uptime', c.vmUptime),
    field('Agent version', c.agentVersion ? c.agentVersion.slice(0, 12) : 'never updated'),
    field('Agent update available', c.agentUpdateAvailable ? 'Yes' : 'No'),
  ].join('');

  document.getElementById('detail-actions').innerHTML = rowActions
    .map(([route, label]) => `<button data-route="${route}">${label}</button>`).join('');
}

async function loadMods() {
  const panel = document.getElementById('mods-panel');
  const res = await fetch(`/api/clients/${id}/mods`);
  if (!res.ok) { panel.textContent = 'Failed to load mods.'; return; }
  const data = await res.json();

  if (!data.requiredMods || data.requiredMods.length === 0) {
    panel.innerHTML = '<div style="color:var(--text-dim)">No RequiredMods configured for this client.</div>';
    return;
  }

  const installedById = new Map((data.installed ?? []).map(m => [m.workshopId, m]));
  panel.innerHTML = `
    <table style="width:100%;">
      <thead><tr><th>Workshop ID</th><th>Status</th><th>Last Updated</th></tr></thead>
      <tbody>
        ${data.requiredMods.map(modId => {
          const m = installedById.get(modId);
          const status = !data.agentReachable
            ? '<span class="badge badge-offline">Agent offline</span>'
            : m?.installed
              ? '<span class="badge badge-running">Installed</span>'
              : '<span class="badge badge-error">Missing</span>';
          return `<tr><td>${escapeHtml(modId)}</td><td>${status}</td><td>${m?.lastUpdatedUtc ?? '-'}</td></tr>`;
        }).join('')}
      </tbody>
    </table>`;
}

async function loadLogs() {
  const res = await fetch(`/api/clients/${id}/logs`);
  if (!res.ok) return;
  const data = await res.json();

  document.getElementById('dayz-log-source').textContent = data.dayZLogSource ? `(${data.dayZLogSource})` : '';
  document.getElementById('dayz-log').textContent = (data.dayZLog ?? []).join('\n') || '(no log lines)';
  document.getElementById('action-log').textContent = (data.actionLog ?? []).join('\n') || '(no actions logged yet today)';
}

// ---- Guest Plugins (see docs/PLUGINS.md) ----
// Only ever triggers a plugin by name; the actual command it runs is fixed by a JSON file an
// administrator placed under C:\GameFarmAgent\Plugins\ inside this client's own VM, never
// anything typed in the browser.

async function loadGuestPlugins() {
  const panel = document.getElementById('guest-plugins-panel');
  const res = await fetch(`/api/clients/${id}/plugins`);
  if (!res.ok) { panel.textContent = 'Agent unreachable — cannot list guest plugins.'; return; }
  const plugins = await res.json();

  if (!plugins.length) {
    panel.innerHTML = '<div style="color:var(--text-dim)">No plugins configured on this VM. See docs/PLUGINS.md to add one.</div>';
    return;
  }

  panel.innerHTML = plugins.map(p => `
    <div style="display:flex;align-items:center;justify-content:space-between;padding:6px 0;border-bottom:1px solid var(--border,#333);">
      <div>
        <strong>${escapeHtml(p.name)}</strong>
        ${p.description ? `<div style="color:var(--text-dim);font-size:12px;">${escapeHtml(p.description)}</div>` : ''}
      </div>
      <button data-run-guest-plugin="${escapeHtml(p.name)}">Run</button>
    </div>
  `).join('') + '<pre id="guest-plugin-output" style="margin-top:12px;max-height:220px;overflow:auto;font-size:11px;white-space:pre-wrap;"></pre>';
}

document.getElementById('guest-plugins-panel')?.addEventListener('click', async e => {
  const btn = e.target.closest('button[data-run-guest-plugin]');
  if (!btn) return;
  const name = btn.dataset.runGuestPlugin;
  btn.disabled = true;
  const res = await fetch(`/api/clients/${id}/plugins/${encodeURIComponent(name)}/run`, { method: 'POST' });
  btn.disabled = false;
  const result = await res.json().catch(() => null);
  const output = document.getElementById('guest-plugin-output');
  if (result && output) {
    output.textContent =
      `[${name}] exit=${result.exitCode} success=${result.success} (${result.durationSeconds?.toFixed(1)}s)\n` +
      (result.standardOutput || '') + (result.standardError ? `\n--- stderr ---\n${result.standardError}` : '');
  }
  showToast(result?.success ? `Plugin '${name}' succeeded.` : `Plugin '${name}' failed — see output below.`, !(result?.success));
});

async function loadAll() {
  await Promise.all([loadDetail(), loadMods(), loadLogs(), loadGuestPlugins()]);
}

document.getElementById('detail-actions')?.addEventListener('click', async e => {
  const btn = e.target.closest('button[data-route]');
  if (!btn) return;
  const res = await fetch(`/api/clients/${id}/${btn.dataset.route}`, { method: 'POST' });
  let body = null;
  try { body = await res.json(); } catch { /* no JSON body */ }
  // AgentCommandResult-shaped bodies (most actions) carry .message; a plugin run (e.g.
  // Initialize VM) instead returns PluginRunResult, which has no .message -- fall back to a
  // short summary built from its exitCode/output so the toast still says something useful.
  const message = body?.message
    ?? (body?.exitCode !== undefined ? `Exit ${body.exitCode}${body.standardError ? `: ${body.standardError.trim().slice(0, 200)}` : ''}` : null)
    ?? (res.ok ? 'Done.' : `Request failed (${res.status}).`);
  showToast(message, !(body?.success ?? res.ok));
  await loadAll();
});

document.getElementById('ensure-mods-btn')?.addEventListener('click', async () => {
  const res = await fetch(`/api/clients/${id}/mods/ensure`, { method: 'POST' });
  let body = null;
  try { body = await res.json(); } catch { /* no JSON body */ }
  showToast(body?.message ?? (res.ok ? 'Done.' : `Request failed (${res.status}).`), !(body?.success ?? res.ok));
  await loadMods();
});

document.getElementById('refresh-logs-btn')?.addEventListener('click', loadLogs);

document.getElementById('edit-form')?.addEventListener('submit', async e => {
  e.preventDefault();
  const f = new FormData(e.target);
  // ServerPassword: null leaves the stored password untouched, "" clears it, anything else
  // replaces it -- matches ClientOrchestrator.UpdateClientAsync's convention. The field is
  // always rendered blank (loadDetail never fills it back in from the server), so a blank
  // submit must mean "unchanged", not "clear" -- that's what the separate checkbox is for.
  const clearPassword = f.get('clearServerPassword') === 'on';
  const typedPassword = f.get('serverPassword') || '';
  const body = {
    steamAccountName: f.get('steamAccountName') || null,
    serverAddress: f.get('serverAddress'),
    serverPort: parseInt(f.get('serverPort'), 10),
    serverPassword: clearPassword ? '' : (typedPassword || null),
    requiredMods: (f.get('requiredMods') || '').split(',').map(s => s.trim()).filter(Boolean),
    autoStart: f.get('autoStart') === 'on',
    autoReconnect: f.get('autoReconnect') === 'on',
    autoUpdate: f.get('autoUpdate') === 'on'
  };

  const res = await fetch(`/api/clients/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (res.ok) {
    await loadAll();
  } else {
    const err = await res.json().catch(() => ({ error: 'Unknown error' }));
    alert(`Failed to save: ${err.error ?? res.status}`);
  }
});

loadAll();
