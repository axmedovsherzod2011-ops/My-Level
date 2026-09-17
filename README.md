# My-Level

Consent-based remote screen monitoring MVP.

## Structure

```text
My-Level/
├─ server/          Node.js + Express + WebSocket server
├─ web/             Admin dashboard and pairing page
├─ agent/           Windows .NET 8 agent
├─ shared/          Protocol and security docs
├─ package.json
└─ README.md
```

## Start the server

```bash
npm install
npm run dev
```

Open `http://localhost:3000/`.

## Pair a Windows PC

1. Open `/connect.html` on the admin computer.
2. Generate the one-time pairing code.
3. On the authorized Windows PC, run the agent from `agent/`.
4. Enter the pairing code.
5. The PC should appear online in the dashboard.

## Agent

```powershell
cd agent
dotnet run
```

For a remote server, set `MY_LEVEL_SERVER` to the server WebSocket URL before starting the agent.

## Current stage

This version verifies the clean project structure, server health, WebSocket connection, one-time pairing, device listing, and heartbeats.

Live screen streaming is intentionally not enabled yet. The next stage will add WebRTC plus Windows screen capture with explicit local authorization and a visible sharing indicator.
