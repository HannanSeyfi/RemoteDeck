using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using RemoteDeck;

var mutex = new Mutex(true, "RemoteDeck.SingleInstance", out var firstInstance);
if (!firstInstance) return;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(8765); options.Limits.MaxRequestBodySize = 16 * 1024; });
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<WindowsInputService>();
builder.Services.AddSingleton<RemoteDeckState>();
var app = builder.Build();
var controllerGate = new SemaphoreSlim(1, 1);

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self' ws: wss:; img-src 'self' data:; frame-ancestors 'none'";
    await next();
});
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/api/status", (HttpContext context, PairingService pairing, RemoteDeckState state) => Results.Ok(new
{
    paired = pairing.IsValid(context.Request.Cookies["rd_token"]), computerName = Environment.MachineName, enabled = state.Enabled
}));
app.MapPost("/api/pair", (PairRequest request, HttpContext context, PairingService pairing) =>
{
    if (!pairing.TryPair(request.Code ?? "", request.DeviceName ?? "", out var token)) return Results.Unauthorized();
    context.Response.Cookies.Append("rd_token", token, new CookieOptions { HttpOnly = true, Secure = false, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(90), IsEssential = true });
    return Results.Ok(new { paired = true });
});
app.MapGet("/ws", async (HttpContext context, PairingService pairing, WindowsInputService input, RemoteDeckState state) =>
{
    if (!state.Enabled) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!pairing.IsValid(context.Request.Cookies["rd_token"])) { context.Response.StatusCode = 401; return; }
    var origin = context.Request.Headers.Origin.ToString();
    var expected = $"{context.Request.Scheme}://{context.Request.Host}";
    if (!string.IsNullOrEmpty(origin) && !origin.Equals(expected, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
    if (!await controllerGate.WaitAsync(0)) { context.Response.StatusCode = 409; return; }
    WebSocket? socket = null;
    try
    {
        socket = await context.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[4096]; var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rateWindow = DateTimeOffset.UtcNow; var messageCount = 0;
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            using var data = new MemoryStream(); WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); return; }
                if (data.Length + result.Count > 8192) { await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", CancellationToken.None); return; }
                await data.WriteAsync(buffer.AsMemory(0, result.Count), context.RequestAborted);
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var now = DateTimeOffset.UtcNow; if (now - rateWindow >= TimeSpan.FromSeconds(1)) { rateWindow = now; messageCount = 0; }
            if (++messageCount > 300) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Too many commands", CancellationToken.None); return; }
            try { var message = JsonSerializer.Deserialize<ControlMessage>(data.ToArray(), options); if (message is not null && state.Enabled) input.Execute(message); }
            catch (JsonException) { }
            catch (Exception error) { app.Logger.LogWarning(error, "Remote-control command failed."); }
        }
    }
    finally
    {
        try { input.ReleaseButtons(); }
        catch (Exception error) { app.Logger.LogWarning(error, "Held mouse buttons could not be released cleanly."); }
        socket?.Dispose();
        controllerGate.Release();
    }
});

var pairingService = app.Services.GetRequiredService<PairingService>();
await app.StartAsync();
var tray = new RemoteDeckTrayContext(app, pairingService, app.Services.GetRequiredService<RemoteDeckState>());
Application.Run(tray);
using (var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    try { await app.StopAsync(shutdownTimeout.Token); }
    catch (OperationCanceledException) { }
}
mutex.ReleaseMutex(); mutex.Dispose();

public sealed record PairRequest(string? Code, string? DeviceName);
public sealed class RemoteDeckState { public bool Enabled { get; set; } = true; }

public sealed class RemoteDeckTrayContext : ApplicationContext
{
    private readonly WebApplication _app; private readonly PairingService _pairing; private readonly RemoteDeckState _state; private readonly NotifyIcon _icon;
    public RemoteDeckTrayContext(WebApplication app, PairingService pairing, RemoteDeckState state)
    {
        _app = app; _pairing = pairing; _state = state;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show connection details", null, (_, _) => ShowDetails());
        menu.Items.Add("Regenerate pairing code", null, (_, _) => { _pairing.RegenerateCode(); ShowDetails(); });
        var devicesMenu = new ToolStripMenuItem("Revoke a paired device");
        menu.Items.Add(devicesMenu);
        menu.Opening += (_, _) =>
        {
            devicesMenu.DropDownItems.Clear();
            var devices = _pairing.GetDevices();
            if (devices.Count == 0) devicesMenu.DropDownItems.Add("No paired devices");
            foreach (var device in devices)
            {
                var item = new ToolStripMenuItem($"{device.Name} ({device.CreatedUtc.LocalDateTime:g})");
                item.Click += (_, _) => _pairing.Revoke(device.Name);
                devicesMenu.DropDownItems.Add(item);
            }
        };
        menu.Items.Add("Enable remote control", null, (_, _) => { _state.Enabled = true; });
        menu.Items.Add("Disable remote control", null, (_, _) => { _state.Enabled = false; });
        menu.Items.Add("Revoke all paired devices", null, (_, _) => _pairing.ForgetAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        var applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        _icon = new NotifyIcon { Icon = applicationIcon, Text = "RemoteDeck", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowDetails();
    }
    private void ShowDetails()
    {
        var addresses = GetLocalIpv4Addresses().Select(a => $"http://{a}:8765");
        var devices = _pairing.GetDevices();
        MessageBox.Show($"RemoteDeck\n\nPairing code: {_pairing.CurrentPairingCode}\n\nOpen on your device:\n{string.Join("\n", addresses)}\n\nPaired devices: {devices.Count}\nRemote control: {(_state.Enabled ? "enabled" : "disabled")}", "RemoteDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private static IEnumerable<IPAddress> GetLocalIpv4Addresses() => Dns.GetHostEntry(Dns.GetHostName()).AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
    protected override void Dispose(bool disposing) { if (disposing) { _icon.Visible = false; _icon.Dispose(); } base.Dispose(disposing); }
    protected override void ExitThreadCore()
    {
        // Leave the UI message loop first. Blocking it on Kestrel shutdown can
        // leave the process and notification icon present when a client was connected.
        _icon.Visible = false;
        base.ExitThreadCore();
    }
}
