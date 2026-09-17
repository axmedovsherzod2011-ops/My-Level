import express from "express";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";
import crypto from "node:crypto";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.join(__dirname, "..");
const PORT = Number(process.env.PORT || 3000);
const ADMIN_PASSWORD = process.env.ADMIN_PASSWORD || "CHANGE-ME";
const app = express();
const server = http.createServer(app);
const wss = new WebSocketServer({ server });
const devices = new Map();
const pairingCodes = new Map();
const sessions = new Map();

app.use(express.json());
app.use((req, res, next) => {
  const origin = req.headers.origin;
  const allowed = !origin || origin === "https://axmedovsherzod2011-ops.github.io" || origin.endsWith(".pages.dev") || origin.endsWith(".workers.dev");
  if (allowed) {
    res.setHeader("Access-Control-Allow-Origin", origin || "*");
    res.setHeader("Vary", "Origin");
    res.setHeader("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
    res.setHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
  }
  if (req.method === "OPTIONS") return res.sendStatus(204);
  next();
});
app.use(express.static(path.join(ROOT, "web")));

function token() { return crypto.randomBytes(24).toString("hex"); }
function ownerToken(req) { return String(req.headers["x-owner-token"] || req.body?.ownerToken || "").slice(0, 128); }
function isAdmin(req) {
  const value = String(req.headers.authorization || "");
  return value.startsWith("Bearer ") && sessions.has(value.slice(7));
}
function requireAdmin(req, res, next) { if (!isAdmin(req)) return res.status(401).json({ error: "Admin authentication required" }); next(); }

app.get("/api/health", (_req, res) => res.json({ ok: true, service: "my-level-server", time: new Date().toISOString() }));

app.post("/api/admin/login", (req, res) => {
  if (String(req.body?.password || "") !== ADMIN_PASSWORD) return res.status(401).json({ error: "Invalid admin password" });
  const session = token();
  sessions.set(session, { createdAt: Date.now() });
  res.json({ token: session });
});

app.post("/api/pairing-code", (req, res) => {
  const owner = ownerToken(req) || token();
  const code = crypto.randomBytes(3).toString("hex").toUpperCase();
  const expiresAt = Date.now() + 5 * 60 * 1000;
  pairingCodes.set(code, { expiresAt, owner });
  res.json({ code, expiresAt, ownerToken: owner });
});

app.get("/api/devices", (req, res) => {
  const admin = isAdmin(req);
  const owner = ownerToken(req);
  const list = [...devices.values()].filter(d => admin || (owner && d.owner === owner));
  res.json(list.map(publicDevice));
});

app.get("/api/admin/devices", requireAdmin, (_req, res) => res.json([...devices.values()].map(publicDevice)));

function publicDevice(device) {
  return { id: device.id, name: device.name, platform: device.platform, status: device.ws?.readyState === 1 ? "online" : "offline", lastSeen: device.lastSeen, sharing: device.sharing, owner: device.owner };
}

function broadcastDevices() {
  const all = [...devices.values()].map(publicDevice);
  for (const ws of wss.clients) {
    if (ws.readyState !== 1) continue;
    if (ws.role === "admin") ws.send(JSON.stringify({ type: "devices", devices: all }));
    if (ws.role === "user") ws.send(JSON.stringify({ type: "devices", devices: [...devices.values()].filter(d => d.owner === ws.owner).map(publicDevice) }));
  }
}

wss.on("connection", ws => {
  ws.role = "unknown";
  ws.on("message", (raw, isBinary) => {
    if (isBinary) {
      if (ws.role !== "agent" || !ws.deviceId) return;
      const device = devices.get(ws.deviceId);
      if (!device || !device.sharing) return;
      for (const client of wss.clients) if (client.readyState === 1 && client.role === "admin" && client.viewDeviceId === ws.deviceId) client.send(raw, { binary: true });
      return;
    }
    let msg; try { msg = JSON.parse(raw.toString()); } catch { return; }

    if (msg.type === "agent.register") {
      const code = String(msg.pairingCode || "").toUpperCase();
      const pairing = pairingCodes.get(code);
      if (!pairing || pairing.expiresAt < Date.now()) { pairingCodes.delete(code); ws.send(JSON.stringify({ type: "error", message: "Invalid or expired pairing code" })); return; }
      pairingCodes.delete(code);
      const id = String(msg.deviceId || crypto.randomUUID());
      const old = devices.get(id); if (old?.ws && old.ws !== ws) old.ws.close();
      const device = { id, owner: pairing.owner, name: String(msg.name || "Windows PC").slice(0, 100), platform: String(msg.platform || "windows").slice(0, 30), lastSeen: new Date().toISOString(), sharing: false, ws };
      devices.set(id, device); ws.role = "agent"; ws.deviceId = id; ws.send(JSON.stringify({ type: "agent.registered", deviceId: id })); broadcastDevices(); return;
    }
    if (msg.type === "user.connect") { ws.role = "user"; ws.owner = String(msg.ownerToken || ""); ws.send(JSON.stringify({ type: "devices", devices: [...devices.values()].filter(d => d.owner === ws.owner).map(publicDevice) })); return; }
    if (msg.type === "admin.connect" && msg.token && sessions.has(String(msg.token))) { ws.role = "admin"; ws.send(JSON.stringify({ type: "devices", devices: [...devices.values()].map(publicDevice) })); return; }
    if (msg.type === "admin.view" && ws.role === "admin") { ws.viewDeviceId = String(msg.deviceId || ""); return; }
    if (msg.type === "admin.stop-view" && ws.role === "admin") { ws.viewDeviceId = null; return; }
    if (ws.role !== "agent" || !ws.deviceId) return;
    const device = devices.get(ws.deviceId); if (!device) return;
    if (msg.type === "agent.heartbeat") { device.lastSeen = new Date().toISOString(); broadcastDevices(); }
    if (msg.type === "agent.sharing") { device.sharing = Boolean(msg.active); device.lastSeen = new Date().toISOString(); broadcastDevices(); }
  });
  ws.on("close", () => { if (ws.role !== "agent" || !ws.deviceId) return; const device = devices.get(ws.deviceId); if (device?.ws === ws) { device.ws = null; device.sharing = false; device.lastSeen = new Date().toISOString(); broadcastDevices(); } });
});

setInterval(() => { const now = Date.now(); for (const [code, item] of pairingCodes) if (item.expiresAt <= now) pairingCodes.delete(code); for (const [s, item] of sessions) if (now - item.createdAt > 12 * 60 * 60 * 1000) sessions.delete(s); }, 30_000);
server.listen(PORT, () => console.log(`My-Level server listening on port ${PORT}`));
