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
Console.WriteLine("My-Level Windows Agent");
Console.WriteLine("Use only on a computer that is authorized for monitoring.");
Console.Write("Pairing code (leave blank to exit): ");
pairingCode ??= Console.ReadLine()?.Trim().ToUpperInvariant();
if (string.IsNullOrWhiteSpace(pairingCode)) return;
using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri(serverUrl), CancellationToken.None);
await Send(ws, new { type="agent.register", pairingCode, deviceId=Environment.GetEnvironmentVariable("MY_LEVEL_DEVICE_ID") ?? Environment.MachineName, name=Environment.MachineName, platform="windows" });
Console.WriteLine("Connected to My-Level.");
Console.Write("Start screen sharing now? Type YES to enable it: ");
var consent = Console.ReadLine();
var sharing = string.Equals(consent?.Trim(), "YES", StringComparison.OrdinalIgnoreCase);
await Send(ws, new { type="agent.sharing", active=sharing });
Console.WriteLine(sharing ? "Screen sharing is ON. Keep this window visible while sharing." : "Screen sharing is OFF.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel=true; stop.Cancel(); };
var heartbeatTask = Task.Run(async () => { while(!stop.IsCancellationRequested && ws.State==WebSocketState.Open) { await Task.Delay(TimeSpan.FromSeconds(5),stop.Token).ContinueWith(_=>{}); if(ws.State==WebSocketState.Open) await Send(ws,new {type="agent.heartbeat"}); } });
try { while(!stop.IsCancellationRequested && ws.State==WebSocketState.Open) { if(sharing) { var frame=CaptureScreenJpeg(); await ws.SendAsync(frame,WebSocketMessageType.Binary,true,stop.Token); await Task.Delay(250,stop.Token); } else await Task.Delay(500,stop.Token); } }
catch(OperationCanceledException) { }
catch(WebSocketException ex) { Console.WriteLine($"Connection closed: {ex.Message}"); }
finally { if(ws.State==WebSocketState.Open) { try { await Send(ws,new {type="agent.sharing",active=false}); } catch { } try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure,"Agent stopped",CancellationToken.None); } catch { } } await heartbeatTask; }
static async Task Send(ClientWebSocket ws, object value) { var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)); await ws.SendAsync(bytes,WebSocketMessageType.Text,true,CancellationToken.None); }
static byte[] CaptureScreenJpeg() { var bounds=Screen.PrimaryScreen?.Bounds ?? new Rectangle(0,0,1280,720); const int maxWidth=1280; var scale=Math.Min(1.0,maxWidth/(double)bounds.Width); var width=Math.Max(1,(int)(bounds.Width*scale)); var height=Math.Max(1,(int)(bounds.Height*scale)); using var source=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format24bppRgb); using(var graphics=Graphics.FromImage(source)) graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size); using var output=new Bitmap(width,height,PixelFormat.Format24bppRgb); using(var graphics=Graphics.FromImage(output)){graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.DrawImage(source,new Rectangle(0,0,width,height));} using var ms=new MemoryStream(); var encoder=ImageCodecInfo.GetImageEncoders().First(e=>e.FormatID==ImageFormat.Jpeg.Guid); using var parameters=new EncoderParameters(1); parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,55L); output.Save(ms,encoder,parameters); return ms.ToArray(); }
