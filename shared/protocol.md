# My-Level protocol

## Agent registration
```json
{"type":"agent.register","pairingCode":"ABC123","deviceId":"OFFICE-PC","name":"OFFICE-PC","platform":"windows"}
```

## Heartbeat
```json
{"type":"agent.heartbeat"}
```

## Sharing state
```json
{"type":"agent.sharing","active":true}
```

Screen sharing is opt-in. Future WebRTC media must start only after explicit authorization on the target PC.
