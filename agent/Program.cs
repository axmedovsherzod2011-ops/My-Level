using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;

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

RecoveryTask.Install(Environment.ProcessPath);
RemoveLegacyStartupEntry();

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

static void RemoveLegacyStartupEntry()
{
    try
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("My-Level", false);
    }
    catch { }
}

static string? PromptText(string title, string text, string defaultValue)
{
    using var form = new Form
    {
        Width = 430, Height = 175, Text = title, StartPosition = FormStartPosition.CenterScreen,
        FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false
    };
    var label = new Label { Left = 20, Top = 18, Width = 375, Height = 42, Text = text };
    var input = new TextBox { Left = 20, Top = 68, Width = 375, Text = defaultValue };
    var ok = new Button { Left = 235, Top = 105, Width = 75, Text = "OK", DialogResult = DialogResult.OK };
    var cancel = new Button { Left = 320, Top = 105, Width = 75, Text = "Cancel", DialogResult = DialogResult.Cancel };
    form.Controls.AddRange(new Control[] { label, input, ok, cancel });
    form.AcceptButton = ok; form.CancelButton = cancel;
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
    private RTCPeerConnection? peer;
    private Vp8NetVideoEncoderEndPoint? videoEndpoint;
    private CancellationTokenSource? mediaStop;
    private readonly object peerLock = new();

    public AgentContext(string serverUrl, string? pairingCode, string deviceId, string? deviceToken, string stateFile, bool sharing)
    {
        this.serverUrl = serverUrl; this.pairingCode = pairingCode; this.deviceId = deviceId;
        this.deviceToken = deviceToken; this.stateFile = stateFile; this.sharing = sharing;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("My-Level", null, (_, _) => ShowStatus()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(sharing ? "Screen sharing: ON" : "Screen sharing: OFF") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("Exit My-Level", null, (_, _) => ExitAgent()));
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = sharing ? "My-Level — Screen sharing ON" : "My-Level — Screen sharing OFF", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowStatus();
        _ = RunAsync();
    }

    private void ShowStatus() => MessageBox.Show(
        sharing ? "My-Level is running in the background.\n\nScreen sharing is ON.\n\nThe admin viewer now uses WebRTC for low-latency live video.\n\nIf the agent crashes or is terminated unexpectedly, Windows will restart it automatically.\n\nTo stop sharing completely, choose Exit My-Level from the tray menu." : "My-Level is running in the background.\n\nScreen sharing is OFF.",
        "My-Level", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void ExitAgent()
    {
        stop.Cancel(); ClosePeer(); RecoveryTask.Remove(); tray.Visible = false; tray.Dispose(); ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { stop.Cancel(); ClosePeer(); tray.Visible = false; tray.Dispose(); stop.Dispose(); }
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
                        ExitAgent(); return;
                    }
                    deviceToken = registeredToken;
                    File.WriteAllText(stateFile, JsonSerializer.Serialize(new AgentState(deviceId, deviceToken, sharing)));
                }
                else
                {
                    await Send(ws, new { type = "agent.resume", deviceId, deviceToken }, stop.Token);
                    var resumed = await ReceiveUntilType(ws, "agent.connected", stop.Token);
                    if (resumed is null)
                    {
                        File.Delete(stateFile); deviceToken = null; tray.Text = "My-Level — Pairing required";
                        MessageBox.Show("This saved device session is no longer valid. Pair this computer again from the My-Level website.", "My-Level", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        ExitAgent(); return;
                    }
                }

                await Send(ws, new { type = "agent.sharing", active = sharing }, stop.Token);
                tray.Text = sharing ? "My-Level — Screen sharing ON" : "My-Level — Screen sharing OFF";

                using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var heartbeatTask = Task.Run(async () =>
                {
                    while (!connectionStop.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(5), connectionStop.Token); } catch { break; }
                        if (ws.State == WebSocketState.Open) { try { await Send(ws, new { type = "agent.heartbeat" }, connectionStop.Token); } catch { break; } }
                    }
                }, connectionStop.Token);

                await ReceiveLoop(ws, connectionStop.Token);
                connectionStop.Cancel();
                try { await heartbeatTask; } catch { }
                ClosePeer();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (WebSocketException) { ClosePeer(); }
            catch (Exception) { ClosePeer(); }
            if (!stop.IsCancellationRequested)
            {
                tray.Text = sharing ? "My-Level — Reconnecting…" : "My-Level — Screen sharing OFF";
                try { await Task.Delay(2000, stop.Token); } catch { break; }
            }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var message = await ReceiveJson(ws, cancellationToken);
            if (message is null) break;
            var value = message.Value;
            var type = GetString(value, "type");
            try
            {
                if (type == "webrtc.offer") await HandleOffer(ws, value, cancellationToken);
                else if (type == "webrtc.candidate") HandleCandidate(value);
                else if (type == "webrtc.stop") ClosePeer();
            }
            catch (Exception ex)
            {
                try { await Send(ws, new { type = "webrtc.error", message = ex.Message }, cancellationToken); } catch { }
                ClosePeer();
            }
        }
    }

    private async Task HandleOffer(ClientWebSocket ws, JsonElement message, CancellationToken cancellationToken)
    {
        ClosePeer();
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.cloudflare.com" } }
        };
        var pc = new RTCPeerConnection(config);
        var endpoint = new Vp8NetVideoEncoderEndPoint();
        endpoint.KeyframeIntervalFrames = 30;
        endpoint.BaseQIndex = 28;
        var track = new MediaStreamTrack(endpoint.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        pc.addTrack(track);
        endpoint.OnVideoSourceEncodedSample += pc.SendVideo;
        pc.OnVideoFormatsNegotiated += formats => { if (formats.Count > 0) endpoint.SetVideoSourceFormat(formats.First()); };
        pc.onicecandidate += candidate =>
        {
            if (candidate == null) return;
            _ = Send(ws, new
            {
                type = "webrtc.candidate",
                candidate = new { candidate = candidate.candidate, sdpMid = candidate.sdpMid ?? candidate.sdpMLineIndex.ToString(), sdpMLineIndex = candidate.sdpMLineIndex, usernameFragment = candidate.usernameFragment }
            }, cancellationToken);
        };
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
            {
                StartCapture(endpoint, cancellationToken);
                tray.Text = "My-Level — WebRTC live ON";
            }
            else if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                StopCapture();
            }
        };

        var offer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = GetString(message, "sdp") ?? "" };
        var result = pc.setRemoteDescription(offer);
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"Could not accept browser offer: {result}");
        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);
        lock (peerLock) { peer = pc; videoEndpoint = endpoint; }
        await Send(ws, new { type = "webrtc.answer", sdp = answer.sdp }, cancellationToken);
    }

    private void HandleCandidate(JsonElement message)
    {
        var candidate = message.TryGetProperty("candidate", out var c) ? c : default;
        if (candidate.ValueKind == JsonValueKind.Null || candidate.ValueKind == JsonValueKind.Undefined) return;
        var init = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidate.GetRawText());
        if (init is not null) peer?.addIceCandidate(init);
    }

    private void StartCapture(Vp8NetVideoEncoderEndPoint endpoint, CancellationToken parentToken)
    {
        StopCapture();
        mediaStop = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var token = mediaStop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var sample = CaptureScreenBgr(out var width, out var height);
                    endpoint.ExternalVideoSourceRawSample(33, width, height, sample, VideoPixelFormatsEnum.Bgr);
                    await Task.Delay(33, token);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(100, token); }
            }
        }, token);
    }

    private void StopCapture()
    {
        try { mediaStop?.Cancel(); } catch { }
        mediaStop?.Dispose(); mediaStop = null;
    }

    private void ClosePeer()
    {
        StopCapture();
        lock (peerLock)
        {
            try { videoEndpoint?.CloseVideo(); } catch { }
            try { peer?.close(); } catch { }
            peer = null; videoEndpoint = null;
        }
    }

    private static byte[] CaptureScreenBgr(out int width, out int height)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        const int maxWidth = 1280;
        var scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
        width = Math.Max(2, (int)(bounds.Width * scale) & ~1);
        height = Math.Max(2, (int)(bounds.Height * scale) & ~1);
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        using var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        var rect = new Rectangle(0, 0, width, height);
        var data = output.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[width * height * 3];
            for (var y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * width * 3, width * 3);
            return bytes;
        }
        finally { output.UnlockBits(data); }
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
        var buffer = new byte[16384]; using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            try { using var doc = JsonDocument.Parse(ms.ToArray()); return doc.RootElement.Clone(); } catch { return null; }
        }
    }

    private static string? GetString(JsonElement? element, string property)
    {
        if (element is not JsonElement value || value.ValueKind != JsonValueKind.Object) return null;
        return value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    }
}

static class RecoveryTask
{
    private const string TaskName = "My-Level\\Agent Recovery";
    public static void Install(string? exe)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            var userId = $"{Environment.UserDomainName}\\{Environment.UserName}";
            var escapedExe = System.Security.SecurityElement.Escape(exe);
            var escapedUser = System.Security.SecurityElement.Escape(userId);
            var startBoundary = DateTime.Now.ToString("s");
            var xml = $"<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + $"<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Author>My-Level</Author><Description>My-Level authorized screen-sharing agent recovery.</Description></RegistrationInfo>" + $"<Triggers><LogonTrigger><StartBoundary>{startBoundary}</StartBoundary><Enabled>true</Enabled><UserId>{escapedUser}</UserId></LogonTrigger></Triggers><Principals><Principal id=\"Author\"><UserId>{escapedUser}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" + "<Settings><Enabled>true</Enabled><AllowStartOnDemand>true</AllowStartOnDemand><AllowHardTerminate>true</AllowHardTerminate><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><RestartOnFailure><Interval>PT1M</Interval><Count>10</Count></RestartOnFailure><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><StartWhenAvailable>true</StartWhenAvailable></Settings>" + "<Actions Context=\"Author\"><Exec><Command>" + escapedExe + "</Command></Exec></Actions></Task>";
            var xmlPath = Path.Combine(Path.GetTempPath(), $"My-Level-{Guid.NewGuid():N}.xml");
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);
            try { RunSchtasks($"/Create /XML \"{xmlPath}\" /TN \"{TaskName}\" /F"); } finally { try { File.Delete(xmlPath); } catch { } }
        }
        catch { }
    }
    public static void Remove() { try { RunSchtasks($"/Delete /TN \"{TaskName}\" /F"); } catch { } }
    private static void RunSchtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo { FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"), Arguments = arguments, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        process?.WaitForExit(5000);
    }
}

record AgentState(string? DeviceId, string? DeviceToken, bool SharingEnabled);