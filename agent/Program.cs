using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

const string defaultServer = "wss://my-level-api.onrender.com";
var serverUrl = Environment.GetEnvironmentVariable("MY_LEVEL_SERVER") ?? defaultServer;
var pairingCode = Environment.GetEnvironmentVariable("MY_LEVEL_PAIRING_CODE");
var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "My-Level");
Directory.CreateDirectory(stateDir);
var stateFile = Path.Combine(stateDir, "device.json");
var state = LoadState(stateFile);
var deviceId = state.DeviceId ?? Environment.GetEnvironmentVariable("MY_LEVEL_DEVICE_ID") ?? Guid.NewGuid().ToString("N");
var deviceToken = state.DeviceToken;

Console.WriteLine("My-Level Windows Agent");
Console.WriteLine("Use only on a computer that is authorized for monitoring.");
Console.WriteLine($"Computer: {Environment.MachineName}");

if (string.IsNullOrWhiteSpace(deviceToken))
{
    Console.Write("Pairing code (leave blank to exit): ");
    pairingCode ??= Console.ReadLine()?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(pairingCode)) return;
}
else
{
    Console.WriteLine("Saved device identity found. Reconnecting automatically when needed.");
}

Console.Write("Start screen sharing now? Type YES to enable it: ");
var consent = Console.ReadLine();
var sharing = string.Equals(consent?.Trim(), "YES", StringComparison.OrdinalIgnoreCase);
if (!sharing) Console.WriteLine("Screen sharing is OFF.");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

while (!stop.IsCancellationRequested)
{
    using var ws = new ClientWebSocket();
    try
    {
        Console.WriteLine("Connecting to My-Level...");
        await ws.ConnectAsync(new Uri(serverUrl), stop.Token);

        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            await Send(ws, new { type = "agent.register", pairingCode, deviceId, name = Environment.MachineName, platform = "windows" }, stop.Token);
            var registration = await ReceiveJson(ws, stop.Token);
            if (registration?.type != "agent.registered" || string.IsNullOrWhiteSpace(registration.deviceToken))
            {
                Console.WriteLine(registration?.message ?? "The server did not confirm registration.");
                return;
            }
            deviceToken = registration.deviceToken;
            SaveState(stateFile, new AgentState(deviceId, deviceToken));
            pairingCode = null;
        }
        else
        {
            await Send(ws, new { type = "agent.resume", deviceId, deviceToken }, stop.Token);
            var resumed = await ReceiveJson(ws, stop.Token);
            if (resumed?.type != "agent.connected")
            {
                Console.WriteLine(resumed?.message ?? "Saved session could not be resumed. Pair this computer again.");
                File.Delete(stateFile);
                deviceToken = null;
                Console.Write("Pairing code: ");
                pairingCode = Console.ReadLine()?.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(pairingCode)) return;
                continue;
            }
        }

        Console.WriteLine("Connected to My-Level.");
        await Send(ws, new { type = "agent.sharing", active = sharing }, stop.Token);
        Console.WriteLine(sharing ? "Screen sharing is ON. Keep this window visible while sharing." : "Screen sharing is OFF.");

        using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var heartbeatTask = Task.Run(async () =>
        {
            while (!connectionStop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), connectionStop.Token); }
                catch (OperationCanceledException) { break; }
                if (ws.State == WebSocketState.Open)
                {
                    try { await Send(ws, new { type = "agent.heartbeat" }, connectionStop.Token); }
                    catch { break; }
                }
            }
        }, connectionStop.Token);

        try
        {
            while (!connectionStop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (sharing)
                {
                    var frame = CaptureScreenJpeg();
                    await ws.SendAsync(frame, WebSocketMessageType.Binary, true, connectionStop.Token);
                    await Task.Delay(250, connectionStop.Token);
                }
                else
                {
                    await Task.Delay(500, connectionStop.Token);
                }
            }
        }
        finally
        {
            connectionStop.Cancel();
            try { await heartbeatTask; } catch { }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
    catch (WebSocketException ex) { Console.WriteLine($"Connection lost: {ex.Message}"); }
    catch (Exception ex) { Console.WriteLine($"Agent error: {ex.Message}"); }

    if (!stop.IsCancellationRequested)
    {
        Console.WriteLine("Reconnecting in 3 seconds...");
        try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
    }
}

static async Task Send(ClientWebSocket ws, object value, CancellationToken cancellationToken)
{
    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
}

static async Task<JsonElement?> ReceiveJson(ClientWebSocket ws, CancellationToken cancellationToken)
{
    var buffer = new byte[8192];
    using var ms = new MemoryStream();
    while (true)
    {
        var result = await ws.ReceiveAsync(buffer, cancellationToken);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        ms.Write(buffer, 0, result.Count);
        if (!result.EndOfMessage) continue;
        try
        {
            using var doc = JsonDocument.Parse(ms.ToArray());
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }
}

static AgentState LoadState(string path)
{
    try
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AgentState>(json) ?? new AgentState(null, null);
    }
    catch { return new AgentState(null, null); }
}

static void SaveState(string path, AgentState state)
{
    File.WriteAllText(path, JsonSerializer.Serialize(state));
}

static byte[] CaptureScreenJpeg()
{
    var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
    const int maxWidth = 1280;
    var scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
    var width = Math.Max(1, (int)(bounds.Width * scale));
    var height = Math.Max(1, (int)(bounds.Height * scale));
    using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
    using (var graphics = Graphics.FromImage(source))
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
    using var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    using (var graphics = Graphics.FromImage(output))
    {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
    }
    using var ms = new MemoryStream();
    var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
    using var parameters = new EncoderParameters(1);
    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
    output.Save(ms, encoder, parameters);
    return ms.ToArray();
}

record AgentState(string? DeviceId, string? DeviceToken);
