# My-Level

Consent-based remote screen monitoring MVP.

## Structure

```text
My-Level/
├─ server/          Node.js + Express + WebSocket signaling server
├─ web/             Admin dashboard and pairing page
├─ agent/           Windows .NET 8 agent
├─ shared/          Protocol and security docs
└─ README.md
```

## Current status

The project now uses WebRTC for live screen video instead of relaying JPEG frames through the server. The Windows agent captures the authorized computer's primary display, encodes video as VP8, and sends it peer-to-peer when a signed-in admin starts viewing a paired device.

The agent requires explicit local authorization before screen sharing is enabled and shows a Windows notification-area indicator while sharing is enabled. Devices are paired with a short-lived one-time code and subsequently resume with a per-device token.

## Pair a Windows PC

1. Open `/connect.html` on the admin computer.
2. Generate the one-time pairing code.
3. On the authorized Windows PC, run the Windows Agent.
4. Enter the pairing code when prompted.
5. Approve screen sharing on that PC.
6. Sign in to the admin dashboard and choose **View screen** for the online sharing device.

## Windows release

The Windows x64 Agent is published automatically from `main` by GitHub Actions as the `latest` GitHub release asset.

## Important

Only use My-Level on computers you own or where monitoring has been explicitly authorized. The system is designed for consent-based monitoring and does not provide hidden capture or arbitrary remote command execution.
