async function refresh() {
  const root = document.querySelector("#devices");
  try {
    const r = await fetch("/api/devices");
    const devices = await r.json();

    if (!devices.length) {
      root.innerHTML = '<div class="empty">No computers connected yet.</div>';
      return;
    }

    root.innerHTML = devices.map(d => `
      <article class="card device">
        <div class="row">
          <div>
            <h2>${escapeHtml(d.name)}</h2>
            <div class="muted">${escapeHtml(d.platform)} · ${escapeHtml(d.id)}</div>
          </div>
          <span class="status ${d.status}">${d.status}</span>
        </div>
        <div class="meta">
          <span>Screen sharing: <b>${d.sharing ? "ON" : "OFF"}</b></span>
          <span>Last seen: ${new Date(d.lastSeen).toLocaleString()}</span>
        </div>
        <button class="button secondary" disabled>View screen — next stage</button>
      </article>
    `).join("");
  } catch {
    root.innerHTML = '<div class="empty">Server unavailable.</div>';
  }
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, c => ({
    "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#039;"
  }[c]));
}

refresh();
setInterval(refresh, 2000);
