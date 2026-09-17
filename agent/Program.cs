using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

const string defaultServer = "wss://my-level-api.onrender.com";
var serverUrl = Environment.GetEnvironmentVariable("MY_LEVEL_SERVER") ?? defaultServer;
var pairingCode = Environment.GetEnvironmentVariable("MY_LEVEL_PAIRING_CODE");
var stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "My-Level");
Directory.CreateDirectory(stateDir);
var stateFile = Path.Combine(stateDir, "device.json");
var state = LoadState(stateFile);
var deviceId = state.DeviceId ?? Environment.GetEnvironmentVariable("MY_LEVEL_DEVICE_ID") ?? Guid.NewGuid().ToString("N");
var deviceToken = state.DeviceToken;

ApplicationConfiguration.Initialize();

if (string.IsNullOrWhiteSpace(deviceToken))
{
    pairingCode ??= PromptText("My-Level — Pair this computer", "Enter the one-time pairing code from the My-Level website:", "");
    pairingCode = pairingCode?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(pairingCode)) return;
}

bool sharing;
if (state.SharingEnabled)
{
    sharing = true;
}
else
{
    var consent = MessageBox.Show(
        "Start screen sharing on this authorized computer?\n\nChoose Yes only if you have authorized My-Level to monitor this PC.\n\nThe agent will run in the Windows notification area (system tray).",
        "My-Level — Screen sharing",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Information);
    sharing = consent == DialogResult.Yes;
    state = state with { SharingEnabled = sharing };
    SaveState(stateFile, state with { DeviceId = deviceId, DeviceToken = deviceToken });
}

// Once the user has explicitly authorized this PC, launch My-Level automatically
// when the same Windows user signs in. The tray icon remains visible while it runs.
SetStartWithWindows(true);

var context = new AgentContext(serverUrl, pairingCode, deviceId, deviceToken, stateFile, sharing);
Application.Run(context);

static AgentState LoadState(string path)
{
    try
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AgentState>(json) ?? new AgentState(null, null, false);
    }
    catch { return new AgentState(null, null, false); }
}

static void SaveState(string path, AgentState state) => File.WriteAllText(path, JsonSerializer.Serialize(state));

static void SetStartWithWindows(bool enabled)
{
    try
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (key is null) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        if (enabled) key.SetValue("My-Level", $"\"{exe}\"");
        else key.DeleteValue("My-Level", false);
    }
    catch { }
}

static string? PromptText(string title, string text, string defaultValue)
{
    using var form = new Form
    {
        Width = 430,
        Height = 175,
        Text = title,
        StartPosition = FormStartPosition.CenterScreen,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        MaximizeBox = false,
        MinimizeBox = false
    };
    var label = new Label { Left = 20, Top = 18, Width = 375, Height = 42, Text = text };
    var input = new TextBox { Left = 20, Top = 68, Width = 375, Text = defaultValue };
    var ok = new Button { Left = 235, Top = 105, Width = 75, Text = "OK", DialogResult = DialogResult.OK };
    var cancel = new Button { Left = 320, Top = 105, Width = 75, Text = "Cancel", DialogResult = DialogResult.Cancel };
    form.Controls.AddRange(new Control[] { label, input, ok, cancel });
    form.AcceptButton = ok;
    form.CancelButton = cancel;
    return form.ShowDialog() == DialogResult.OK ? input.Text : null;
}

sealed class AgentContext : ApplicationContext
{
    private readonly string serverUrl;
    private readonly string? pairingCode;
    private readonly string deviceId;
    private string? deviceToken;
    private readonly string stateFile;
    private readonly NotifyIcon tray;
    private readonly CancellationTokenSource stop = new();
    private readonly bool sharing;

    public AgentContext(string serverUrl, string? pairingCode, string deviceId, string? deviceToken, string stateFile, bool sharing)
    {
        this.serverUrl = serverUrl;
        this.pairingCode = pairingCode;
        this.deviceId = deviceId;
        this.deviceToken = deviceToken;
        this.stateFile = stateFile;
        this.sharing = sharing;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("My-Level", null, (_, _) => ShowStatus()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(sharing ? "Screen sharing: ON" : "Screen sharing: OFF") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Exit My-Level", null, (_, _) => ExitAgent()));

        tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = sharing ? "My-Level — Screen sharing ON" : "My-Level — Screen sharing OFF",
            Visible = true,
            ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => ShowStatus();

        _ = RunAsync();
    }

    private void ShowStatus()
    {
        MessageBox.Show(
            sharing
                ? "My-Level is running in the background.\n\nScreen sharing is ON.\n\nUse the My-Level admin page to view this authorized computer.\n\nTo stop sharing completely, choose Exit My-Level from the tray menu.":
                "My-Level is running in the background.\n\nScreen sharing is OFF.",
            "My-Level",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ExitAgent()
    {
        stop.Cancel();
        tray.Visible = false;
        tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stop.Cancel();
            tray.Visible = false;
            tray.Dispose();
            stop.Dispose();
        }
        base.Dispose(disposing);
    }

    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(serverUrl), stop.Token);

                if (string.IsNullOrWhiteSpace(deviceToken))
                {
                    await Send(ws, new { type = "agent.register", pairingCode, deviceId, name = Environment.MachineName, platform = "windows" }, stop.Token);
                    var registration = await ReceiveUntilType(ws, "agent.registered", stop.Token);
                    var registeredToken = GetString(registration, "deviceToken");
                    if (registration is null || registeredToken is null)
                    {
                        tray.Text = "My-Level — Pairing failed";
                        MessageBox.Show(GetString(registration, "message") ?? "The server did not confirm registration.", "My-Level", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        ExitAgent();
                        return;
                    }
                    deviceToken = registeredToken;
                    SaveState(stateFile, new AgentState(deviceId, deviceToken, sharing));
                }
                else
                {
                    await Send(ws, new { type = "agent.resume", deviceId, deviceToken }, stop.Token);
                    var resumed = await ReceiveUntilType(ws, "agent.connected", stop.Token);
                    if (resumed is null)
                    {
                        File.Delete(stateFile);
                        deviceToken = null;
                        tray.Text = "My-Level — Pairing required";
                        MessageBox.Show("This saved device session is no longer valid. Pair this computer again from the My-Level website.", "My-Level", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        ExitAgent();
                        return;
                    }
                }

                await Send(ws, new { type = "agent.sharing", active = sharing }, stop.Token);
                tray.Text = sharing ? "My-Level — Screen sharing ON" : "My-Level — Screen sharing OFF";

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
            catch (WebSocketException) { }
            catch (Exception) { }

            if (!stop.IsCancellationRequested)
            {
                tray.Text = sharing ? "My-Level — Reconnecting…" : "My-Level — Screen sharing OFF";
                try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task Send(ClientWebSocket ws, object value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement?> ReceiveUntilType(ClientWebSocket ws, string expectedType, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveJson(ws, cancellationToken);
            if (message is null) return null;
            var type = GetString(message, "type");
            if (type == expectedType || type == "error") return message;
        }
        return null;
    }

    private static async Task<JsonElement?> ReceiveJson(ClientWebSocket ws, CancellationToken cancellationToken)
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

    private static string? GetString(JsonElement? element, string property)
    {
        if (element is not JsonElement value || value.ValueKind != JsonValueKind.Object) return null;
        return value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    }

    private static byte[] CaptureScreenJpeg()
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        const int maxWidth = 1280;
        var scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
        var width = Math.Max(1, (int)(bounds.Width * scale));
        var height = Math.Max(1, (int)(bounds.Height * scale));
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
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
}

record AgentState(string? DeviceId, string? DeviceToken, bool SharingEnabled);
