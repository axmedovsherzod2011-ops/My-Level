# My-Level protocol

## Agent → server

`agent.register`
```json
{
  "type": "agent.register",
  "deviceId": "PC-001",
  "name": "Office-PC",
  "platform": "windows"
}
```

`agent.heartbeat`
```json
{
  "type": "agent.heartbeat"
}
```

`agent.sharing`
```json
{
  "type": "agent.sharing",
  "active": true
}
```

## Planned WebRTC signaling

The next stage will add:
- authenticated device sessions
- admin session authentication
- WebRTC offer/answer exchange
- ICE candidates
- STUN/TURN configuration
- visible sharing state
- explicit start/stop controls

The media channel should carry the live screen only after sharing is explicitly enabled.
