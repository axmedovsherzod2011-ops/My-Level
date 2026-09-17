# My-Level Windows Agent

This folder is the Windows background-agent starter.

The production agent should:

1. Register the device after explicit enrollment.
2. Maintain a secure WebSocket connection.
3. Send heartbeats.
4. Expose a visible tray/status indicator.
5. Require an explicit user/admin action before screen sharing begins.
6. Stop sharing immediately when the user disables it.
7. Later publish the approved screen stream through WebRTC.

Do not turn the agent into a hidden recorder or add stealth/persistence designed to conceal monitoring.
