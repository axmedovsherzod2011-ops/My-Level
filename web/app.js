const root = document.querySelector('#devices');
const count = document.querySelector('#count');

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#039;'}[c]));
}

function render(devices) {
  count.textContent = devices.length;
  if (!devices.length) {
    root.innerHTML = '<div class="empty">No computers connected yet.</div>';
    return;
  }
  root.innerHTML = devices.map(d => `
    <article class="device">
      <div class="row">
        <div><h2>${escapeHtml(d.name)}</h2><div class="muted">${escapeHtml(d.platform)} · ${escapeHtml(d.id)}</div></div>
        <span class="status ${d.status}">${d.status}</span>
      </div>
      <div class="meta">
        <span>Screen sharing: <b>${d.sharing ? 'ON' : 'OFF'}</b></span>
        <span>Last seen: ${new Date(d.lastSeen).toLocaleString()}</span>
      </div>
      <button class="button secondary" disabled>View screen — next stage</button>
    </article>`).join('');
}

function connect() {
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const ws = new WebSocket(`${protocol}//${location.host}`);
  ws.addEventListener('open', () => ws.send(JSON.stringify({ type: 'admin.connect' })));
  ws.addEventListener('message', event => {
    try {
      const msg = JSON.parse(event.data);
      if (msg.type === 'devices') render(msg.devices);
    } catch {}
  });
  ws.addEventListener('close', () => setTimeout(connect, 1500));
  ws.addEventListener('error', () => ws.close());
}

connect();
