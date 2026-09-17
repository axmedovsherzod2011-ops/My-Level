using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

const string defaultServer = "ws://localhost:3000";
var serverUrl = Environment.GetEnvironmentVariable("MY_LEVEL_SERVER") ?? defaultServer;
var pairingCode = Environment.GetEnvironmentVariable("MY_LEVEL_PAIRING_CODE");

Console.WriteLine("My-Level Windows Agent");
Console.WriteLine("Use only on a computer that is authorized for monitoring.");
Console.Write("Pairing code (leave blank to exit): ");
pairingCode ??= Console.ReadLine()?.Trim().ToUpperInvariant();

if (string.IsNullOrWhiteSpace(pairingCode)) return;

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(serverUrl), CancellationToken.None);

await Send(ws, new {
    type = "agent.register",
    pairingCode,
    deviceId = Environment.GetEnvironmentVariable("MY_LEVEL_DEVICE_ID") ?? Environment.MachineName,
    name = Environment.MachineName,
    platform = "windows"
});

Console.WriteLine("Connected. Screen sharing is OFF by default.");

while (ws.State == WebSocketState.Open)
{
    await Task.Delay(TimeSpan.FromSeconds(10));
    await Send(ws, new { type = "agent.heartbeat" });
}

static async Task Send(ClientWebSocket ws, object value)
{
    var json = JsonSerializer.Serialize(value);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
}
