const API_BASE = 'https://my-level-api.onrender.com';
const WS_BASE = 'wss://my-level-api.onrender.com';

const root = document.querySelector('#devices');
const count = document.querySelector('#count');
const online = document.querySelector('#online');
const sharing = document.querySelector('#sharing');
const refreshButton = document.querySelector('#refresh');
let adminSocket = null;
let currentObjectUrl = null;

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#039;'}[c]));
}

function openViewer(device) {
  if (!adminSocket || adminSocket.readyState !== WebSocket.OPEN) return alert('Connecting to My-Level. Try again in a moment.');
  adminSocket.send(JSON.stringify({ type: 'admin.view', deviceId: device.id }));

  const modal = document.createElement('div');
  modal.className = 'screen-modal';
  modal.innerHTML = `<div class="screen-modal-card"><div class="screen-modal-head"><div><strong>${escapeHtml(device.name)}</strong><span>Live screen</span></div><button class="screen-close" aria-label="Close">×</button></div><div class="screen-stage"><div class="screen-placeholder">Waiting for screen frames…<small>The authorized PC must have screen sharing enabled.</small></div><img class="screen-image" alt="Live screen of authorized computer"></div></div>`;
  document.body.appendChild(modal);
  const close = () => {
    if (adminSocket?.readyState === WebSocket.OPEN) adminSocket.send(JSON.stringify({ type: 'admin.stop-view' }));
    if (currentObjectUrl) { URL.revokeObjectURL(currentObjectUrl); currentObjectUrl = null; }
    modal.remove();
  };
  modal.querySelector('.screen-close').onclick = close;
  modal.addEventListener('click', e => { if (e.target === modal) close(); });
  document.addEventListener('keydown', function esc(e) { if (e.key === 'Escape') { document.removeEventListener('keydown', esc); close(); } });
}

function render(devices) {
  count.textContent = devices.length;
  online.textContent = devices.filter(d => d.status === 'online').length;
  sharing.textContent = devices.filter(d => d.sharing).length;

  if (!devices.length) {
    root.innerHTML = '<div class="empty"><div class="empty-icon">⌁</div><strong>No computers connected yet</strong><span>Add your first authorized computer to get started.</span></div>';
    return;
  }

  root.innerHTML = devices.map(d => `<article class="device"><div class="device-main"><div class="device-icon">▣</div><div class="device-copy"><h3>${escapeHtml(d.name)}</h3><div class="meta"><span>${escapeHtml(d.platform)}</span><span class="meta-dot"></span><span>${d.sharing ? 'Screen sharing on' : 'Screen sharing off'}</span></div></div></div><div class="device-side"><span class="status ${d.status}">${d.status}</span><button class="view-button" data-device="${escapeHtml(d.id)}" ${d.status !== 'online' || !d.sharing ? 'disabled' : ''}>View screen</button></div></article>`).join('');
  root.querySelectorAll('.view-button:not(:disabled)').forEach(button => {
    button.addEventListener('click', () => {
      const device = devices.find(d => d.id === button.dataset.device);
      if (device) openViewer(device);
    });
  });
}

async function loadDevices() {
  refreshButton.disabled = true;
  try {
    const response = await fetch(`${API_BASE}/api/devices`, { cache: 'no-store' });
    if (!response.ok) throw new Error('Failed to load devices');
    render(await response.json());
  } catch {
    root.innerHTML = '<div class="empty"><div class="empty-icon">!</div><strong>Could not connect to My-Level</strong><span>The backend may be waking up. Try Refresh in a few seconds.</span></div>';
  } finally { refreshButton.disabled = false; }
}

refreshButton?.addEventListener('click', loadDevices);
document.querySelector('#help')?.addEventListener('click', () => window.alert('Add an authorized PC, enter the one-time pairing code in the Windows Agent, then enable screen sharing locally.'));
document.querySelector('#settings')?.addEventListener('click', () => window.alert('Settings will be available in a later version.'));

autoConnect();
function autoConnect() {
  adminSocket = new WebSocket(WS_BASE);
  adminSocket.addEventListener('open', () => adminSocket.send(JSON.stringify({ type: 'admin.connect' })));
  adminSocket.addEventListener('message', async event => {
    if (typeof event.data === 'string') {
      try { const msg = JSON.parse(event.data); if (msg.type === 'devices') render(msg.devices); } catch {}
      return;
    }
    const image = document.querySelector('.screen-image');
    if (!image) return;
    const blob = event.data instanceof Blob ? event.data : new Blob([event.data], { type: 'image/jpeg' });
    if (currentObjectUrl) URL.revokeObjectURL(currentObjectUrl);
    currentObjectUrl = URL.createObjectURL(blob);
    image.src = currentObjectUrl;
    image.previousElementSibling?.remove();
  });
  adminSocket.addEventListener('close', () => setTimeout(autoConnect, 3000));
  adminSocket.addEventListener('error', () => adminSocket.close());
}

loadDevices();