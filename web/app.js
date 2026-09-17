const root = document.querySelector('#devices');
const count = document.querySelector('#count');
const online = document.querySelector('#online');
const sharing = document.querySelector('#sharing');
const refreshButton = document.querySelector('#refresh');

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#039;'}[c]));
}

function render(devices) {
  count.textContent = devices.length;
  online.textContent = devices.filter(d => d.status === 'online').length;
  sharing.textContent = devices.filter(d => d.sharing).length;

  if (!devices.length) {
    root.innerHTML = `
      <div class="empty">
        <div class="empty-icon">⌁</div>
        <strong>No computers connected yet</strong>
        <span>Add your first authorized computer to get started.</span>
      </div>`;
    return;
  }

  root.innerHTML = devices.map(d => `
    <article class="device">
      <div class="device-main">
        <div class="device-icon">▣</div>
        <div class="device-copy">
          <h3>${escapeHtml(d.name)}</h3>
          <div class="meta">
            <span>${escapeHtml(d.platform)}</span>
            <span class="meta-dot"></span>
            <span>${d.sharing ? 'Screen sharing on' : 'Screen sharing off'}</span>
          </div>
        </div>
      </div>
      <div class="device-side">
        <span class="status ${d.status}">${d.status}</span>
        <button class="view-button" disabled title="Live screen viewing is coming next">View screen</button>
      </div>
    </article>`).join('');
}

async function loadDevices() {
  refreshButton.disabled = true;
  try {
    const response = await fetch('/api/devices', { cache: 'no-store' });
    if (!response.ok) throw new Error('Failed to load devices');
    render(await response.json());
  } catch {
    root.innerHTML = '<div class="empty"><div class="empty-icon">!</div><strong>Could not load computers</strong><span>Check that the My-Level server is running.</span></div>';
  } finally {
    refreshButton.disabled = false;
  }
}

refreshButton?.addEventListener('click', loadDevices);
document.querySelector('#help')?.addEventListener('click', () => {
  window.alert('Connect a computer with “Add computer”. The Windows Agent will use the one-time pairing code shown there.');
});
document.querySelector('#settings')?.addEventListener('click', () => {
  window.alert('Settings will be available in a later version.');
});

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

loadDevices();
connect();
