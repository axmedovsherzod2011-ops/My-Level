using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

const string serverUrl = "ws://localhost:3000";
var deviceId = Environment.GetEnvironmentVariable("MY_LEVEL_DEVICE_ID")
               ?? Environment.MachineName;

Console.WriteLine("My-Level Agent");
Console.WriteLine($"Device: {deviceId}");
Console.WriteLine("Visible sharing control is required before screen streaming.");

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(serverUrl), CancellationToken.None);

await Send(new {
    type = "agent.register",
    deviceId,
    name = Environment.MachineName,
    platform = "windows"
});

Console.WriteLine("Connected to My-Level server.");

while (ws.State == WebSocketState.Open)
{
    await Task.Delay(TimeSpan.FromSeconds(15));
    await Send(new { type = "agent.heartbeat" });
}

async Task Send(object value)
{
    var json = JsonSerializer.Serialize(value);
    var bytes = Encoding.UTF8.GetBytes(json);
    await ws.SendAsync(
        bytes,
        WebSocketMessageType.Text,
        true,
        CancellationToken.None
    );
}
