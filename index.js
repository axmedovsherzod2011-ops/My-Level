import express from "express";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { WebSocketServer } from "ws";
import crypto from "node:crypto";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const app = express();
const server = http.createServer(app);
const wss = new WebSocketServer({ server });

app.use(express.json());
app.use(express.static(path.join(__dirname, "..", "web")));

const devices = new Map();
const sockets = new Map();

function publicDevice(d) {
  return {
    id: d.id,
    name: d.name,
    platform: d.platform,
    status: d.socket?.readyState === 1 ? "online" : "offline",
    lastSeen: d.lastSeen,
    sharing: Boolean(d.sharing)
  };
}

app.get("/api/health", (_req, res) => {
  res.json({ ok: true, service: "my-level-server", time: new Date().toISOString() });
});

app.get("/api/devices", (_req, res) => {
  res.json([...devices.values()].map(publicDevice));
});

app.post("/api/pairing-code", (_req, res) => {
  const code = crypto.randomBytes(3).toString("hex").toUpperCase();
  const expiresAt = Date.now() + 10 * 60 * 1000;
  pairingCodes.set(code, { expiresAt });
  res.json({ code, expiresAt });
});

const pairingCodes = new Map();

wss.on("connection", (ws) => {
  ws.on("message", (raw) => {
    let msg;
    try { msg = JSON.parse(raw.toString()); } catch { return; }

    if (msg.type === "agent.register") {
      const id = String(msg.deviceId || crypto.randomUUID());
      const device = {
        id,
        name: String(msg.name || "Windows PC"),
        platform: String(msg.platform || "windows"),
        lastSeen: new Date().toISOString(),
        sharing: false,
        socket: ws
      };
      devices.set(id, device);
      sockets.set(ws, id);

      ws.send(JSON.stringify({
        type: "agent.registered",
        deviceId: id
      }));
      return;
    }

    const deviceId = sockets.get(ws);
    if (!deviceId) return;

    const device = devices.get(deviceId);
    if (!device) return;

    if (msg.type === "agent.heartbeat") {
      device.lastSeen = new Date().toISOString();
      return;
    }

    if (msg.type === "agent.sharing") {
      device.sharing = Boolean(msg.active);
      device.lastSeen = new Date().toISOString();
    }
  });

  ws.on("close", () => {
    const id = sockets.get(ws);
    if (id) {
      const device = devices.get(id);
      if (device) {
        device.socket = null;
        device.sharing = false;
        device.lastSeen = new Date().toISOString();
      }
      sockets.delete(ws);
    }
  });
});

setInterval(() => {
  const now = Date.now();
  for (const [code, item] of pairingCodes) {
    if (item.expiresAt < now) pairingCodes.delete(code);
  }
}, 60_000);

server.listen(process.env.PORT || 3000, () => {
  console.log(`My-Level server listening on http://localhost:${process.env.PORT || 3000}`);
});
