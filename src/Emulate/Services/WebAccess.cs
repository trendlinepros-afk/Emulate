using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Emulate.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Emulate.Services;

/// <summary>
/// "Web access": serves a running device to web browsers on the local network.
/// The page shows a live H.264 stream of the screen (decoded in the browser with WebCodecs, screenshot
/// fallback otherwise) and sends taps, swipes, keys and text back. Protected by a 6-digit access code.
/// </summary>
public static class WebAccess
{
    private static readonly Dictionary<string, WebAccessServer> Servers = new();

    public static event Action<Profile>? Changed;

    public static bool IsRunning(Profile p)
    {
        lock (Servers) return Servers.ContainsKey(p.Id);
    }

    public static string EnsureCode(Profile p)
    {
        if (string.IsNullOrEmpty(p.WebCode))
        {
            p.WebCode = NewCode();
            ProfileStore.Save();
        }
        return p.WebCode!;
    }

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static async Task StartAsync(RunningDevice dev)
    {
        var p = dev.Profile;
        await StopAsync(p);
        EnsureCode(p);
        if (p.WebPort is <= 0 or > 65535 || !PortFree(p.WebPort))
        {
            var used = ProfileStore.All.Where(o => o != p).Select(o => o.WebPort).ToHashSet();
            var port = 8601;
            while (used.Contains(port) || !PortFree(port)) port++;
            p.WebPort = port;
            ProfileStore.Save();
        }

        var server = new WebAccessServer(dev);
        await server.StartAsync(p.WebPort);
        lock (Servers) Servers[p.Id] = server;
        Log.Say($"Web access for {p.Name} is on: {Urls(p).FirstOrDefault()}");
        Changed?.Invoke(p);
    }

    public static async Task StopAsync(Profile p)
    {
        WebAccessServer? s;
        lock (Servers)
        {
            if (!Servers.Remove(p.Id, out s)) return;
        }
        await s.DisposeAsync();
        Changed?.Invoke(p);
    }

    /// <summary>Links other devices can open (LAN addresses first). The code is included so the link just works.</summary>
    public static List<string> Urls(Profile p)
    {
        var code = p.WebCode ?? "";
        return LanAddresses().Select(ip => $"http://{ip}:{p.WebPort}/?code={code}").ToList();
    }

    public static List<string> LanAddresses()
    {
        var list = new List<(string ip, int rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && g.Address.AddressFamily == AddressFamily.InterNetwork);
                var virtualish = Regex.IsMatch(ni.Description + ni.Name, "virtual|vEthernet|VMware|VirtualBox|Hyper-V|WSL|Loopback|Bluetooth", RegexOptions.IgnoreCase);
                foreach (var a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = a.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    list.Add((s, (hasGateway ? 0 : 2) + (virtualish ? 1 : 0)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listing network addresses", ex);
        }
        var result = list.OrderBy(x => x.rank).Select(x => x.ip).Distinct().ToList();
        if (result.Count == 0) result.Add("localhost");
        return result;
    }

    private static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds an inbound Windows Firewall rule for Emulate (asks for administrator rights).</summary>
    public static void AllowThroughFirewall()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppPaths.InstallDir, "Emulate.exe");
        var cmd = $"netsh advfirewall firewall delete rule name=\"Emulate web access\" >nul 2>&1 & " +
                  $"netsh advfirewall firewall add rule name=\"Emulate web access\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain";
        Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
    }
}

/// <summary>Web server for one device.</summary>
internal sealed class WebAccessServer : IAsyncDisposable
{
    private readonly RunningDevice _dev;
    private WebApplication? _app;
    private readonly ScreenStreamer _streamer;
    private readonly InputShell _input;

    public WebAccessServer(RunningDevice dev)
    {
        _dev = dev;
        _streamer = new ScreenStreamer(dev);
        _input = new InputShell(dev);
    }

    private Profile P => _dev.Profile;

    public async Task StartAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        app.MapGet("/", async ctx =>
        {
            if (!Authorized(ctx, out var fromQuery))
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(WebPage.Login(P.Name, ctx.Request.Query.ContainsKey("code")));
                return;
            }
            if (fromQuery)
            {
                ctx.Response.Cookies.Append(CookieName, P.WebCode!, new CookieOptions
                {
                    MaxAge = TimeSpan.FromDays(30), SameSite = SameSiteMode.Lax, HttpOnly = true, Path = "/",
                });
            }
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(WebPage.Viewer(P.Name));
        });

        app.MapGet("/shot", async ctx =>
        {
            if (!Authorized(ctx, out _)) { ctx.Response.StatusCode = 403; return; }
            var png = await ProcessRunner.RunBinaryAsync(AppPaths.Adb, new[] { "-s", _dev.Serial, "exec-out", "screencap", "-p" }, TimeSpan.FromSeconds(15));
            ctx.Response.ContentType = "image/png";
            ctx.Response.Headers.CacheControl = "no-store";
            await ctx.Response.Body.WriteAsync(png);
        });

        app.Map("/ws", async ctx =>
        {
            if (!Authorized(ctx, out _) || !ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 403; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var wantVideo = ctx.Request.Query["video"] != "0";
            await RunClientAsync(ws, wantVideo, ctx.RequestAborted);
        });

        await app.StartAsync();
        _app = app;
    }

    private string CookieName => "emulate_" + P.Id;

    private bool Authorized(HttpContext ctx, out bool fromQuery)
    {
        var code = P.WebCode;
        fromQuery = false;
        if (string.IsNullOrEmpty(code)) return false;
        if (ctx.Request.Query.TryGetValue("code", out var q) && q.ToString().Trim() == code)
        {
            fromQuery = true;
            return true;
        }
        return ctx.Request.Cookies.TryGetValue(CookieName, out var c) && c == code;
    }

    private async Task RunClientAsync(WebSocket ws, bool wantVideo, CancellationToken aborted)
    {
        var client = new StreamClient(ws);
        await client.SendTextAsync(JsonSerializer.Serialize(new { type = "info", name = P.Name, os = P.Os.Name }));
        if (wantVideo) _streamer.Add(client);
        var sender = client.PumpAsync(aborted);
        try
        {
            var buffer = new byte[8192];
            var sb = new StringBuilder();
            while (ws.State == WebSocketState.Open && !aborted.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, aborted);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType != WebSocketMessageType.Text) continue;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var msg = sb.ToString();
                sb.Clear();
                try { await HandleInputAsync(msg); } catch (Exception ex) { Log.Info("Web input: " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Browser went away.
        }
        finally
        {
            _streamer.Remove(client);
            client.Complete();
            await sender;
        }
    }

    private async Task HandleInputAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        var t = m.GetProperty("t").GetString();
        var (w, h) = await _streamer.GetLogicalSizeAsync();
        int X(string k) => (int)Math.Round(Math.Clamp(m.GetProperty(k).GetDouble(), 0, 1) * (w - 1));
        int Y(string k) => (int)Math.Round(Math.Clamp(m.GetProperty(k).GetDouble(), 0, 1) * (h - 1));
        switch (t)
        {
            case "tap":
                _input.Send($"input tap {X("x")} {Y("y")}");
                break;
            case "swipe":
                var ms = Math.Clamp(m.GetProperty("ms").GetInt32(), 40, 3000);
                _input.Send($"input swipe {X("x1")} {Y("y1")} {X("x2")} {Y("y2")} {ms}");
                break;
            case "key":
                var code = m.GetProperty("code").GetInt32();
                if (code is > 0 and < 400) _input.Send($"input keyevent {code}");
                break;
            case "text":
                var s = m.GetProperty("s").GetString() ?? "";
                var clean = new string(s.Where(c => c >= 32 && c < 127).ToArray());
                if (clean.Length > 0) _input.Send("input text " + Adb.ShQuote(clean.Replace(" ", "%s")));
                break;
            case "rotate":
                await DeviceManager.RotateAsync(_dev);
                break;
            // Press-and-hold / drag: a real finger down, moves, and up (long-press menus, dragging icons).
            case "down":
                _input.Send($"input motionevent DOWN {X("x")} {Y("y")}");
                break;
            case "move":
                _input.Send($"input motionevent MOVE {X("x")} {Y("y")}", move: true);
                break;
            case "up":
                _input.Send($"input motionevent UP {X("x")} {Y("y")}");
                break;
            case "fit":
                if (m.TryGetProperty("off", out _)) await _streamer.UnfitAsync();
                else await _streamer.FitAsync(m.GetProperty("w").GetInt32(), m.GetProperty("h").GetInt32());
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _streamer.Dispose();
        _input.Dispose();
        if (_app != null)
        {
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await _app.StopAsync(cts.Token); } catch { }
            await _app.DisposeAsync();
        }
    }
}

/// <summary>One connected browser: a bounded queue of outgoing messages.</summary>
internal sealed class StreamClient
{
    private readonly WebSocket _ws;
    private readonly Channel<(byte[] data, bool text)> _queue = Channel.CreateBounded<(byte[], bool)>(
        new BoundedChannelOptions(300) { FullMode = BoundedChannelFullMode.DropWrite });

    public StreamClient(WebSocket ws) => _ws = ws;

    /// <summary>Queues video. Returns false when the connection can't keep up (frames would be lost).</summary>
    public bool SendBinary(byte[] data) => _queue.Writer.TryWrite((data, false));

    /// <summary>Drops the connection; the page reconnects by itself and gets a fresh keyframe.</summary>
    public void Abort()
    {
        try { _ws.Abort(); } catch { }
        _queue.Writer.TryComplete();
    }

    public Task SendTextAsync(string text) => _queue.Writer.WriteAsync((Encoding.UTF8.GetBytes(text), true)).AsTask();

    public void Complete() => _queue.Writer.TryComplete();

    public async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (data, text) in _queue.Reader.ReadAllAsync(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                await _ws.SendAsync(data, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, ct);
            }
        }
        catch
        {
            // Connection closed.
        }
    }
}

/// <summary>Runs "screenrecord --output-format=h264" on the device and fans the stream out to browsers.</summary>
internal sealed class ScreenStreamer : IDisposable
{
    private readonly RunningDevice _dev;
    private readonly List<StreamClient> _clients = new();
    private Process? _proc;
    private CancellationTokenSource? _loopCts;
    private (int w, int h)? _physical;
    private int _rotation;
    private DateTime _rotationCheckedAt = DateTime.MinValue;
    private bool _disposed;

    public ScreenStreamer(RunningDevice dev) => _dev = dev;

    public void Add(StreamClient c)
    {
        lock (_clients) _clients.Add(c);
        // New viewers need a fresh keyframe: restart the recorder (or start it).
        Restart();
    }

    public void Remove(StreamClient c)
    {
        bool none;
        lock (_clients)
        {
            _clients.Remove(c);
            none = _clients.Count == 0;
        }
        if (none)
        {
            StopLoop();
            _ = UnfitAsync(); // nobody watching: give the device its normal screen back
        }
    }

    private bool _fitted;
    private string? _fitSize;

    /// <summary>Reshapes the Android display to the browser's viewing area so it fills the page.</summary>
    public async Task FitAsync(int areaW, int areaH)
    {
        if (areaW < 100 || areaH < 100) return;
        var r = await Adb.ShellAsync(_dev.Serial, "wm size", TimeSpan.FromSeconds(10));
        var m = Regex.Match(r.Output, @"Physical size:\s*(\d+)x(\d+)");
        if (!m.Success) return;
        int pw = int.Parse(m.Groups[1].Value), ph = int.Parse(m.Groups[2].Value);
        // Keep the device's width (so text size stays the same) and change its height to the browser's shape.
        var natW = Math.Min(pw, ph);
        double ratio = Math.Clamp((double)areaH / areaW, 0.5, 2.6);
        int w = natW / 2 * 2, h = (int)Math.Round(natW * ratio / 2) * 2;
        var size = $"{w}x{h}";
        if (_fitted && _fitSize == size) return;
        await Adb.ShellAsync(_dev.Serial, $"wm size {size}", TimeSpan.FromSeconds(10));
        _fitted = true;
        _fitSize = size;
        _physical = null;
        Restart();
    }

    public async Task UnfitAsync()
    {
        if (!_fitted) return;
        _fitted = false;
        _fitSize = null;
        await Adb.ShellAsync(_dev.Serial, "wm size reset", TimeSpan.FromSeconds(10));
        _physical = null;
        bool any;
        lock (_clients) any = _clients.Count > 0;
        if (any) Restart();
    }

    private readonly object _loopGate = new();

    private void Restart()
    {
        lock (_loopGate)
        {
            StopLoop();
            if (_disposed) return;
            var cts = _loopCts = new CancellationTokenSource();
            _ = Task.Run(() => LoopAsync(cts.Token));
        }
    }

    private void StopLoop()
    {
        lock (_loopGate)
        {
            _loopCts?.Cancel();
            _loopCts = null;
            try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
            _proc = null;
        }
    }

    /// <summary>Display size in the current orientation (what "input tap" coordinates use).</summary>
    public async Task<(int w, int h)> GetLogicalSizeAsync()
    {
        _physical ??= await ReadPhysicalSizeAsync();
        if ((DateTime.UtcNow - _rotationCheckedAt).TotalSeconds > 2) await RefreshRotationAsync();
        var (w, h) = _physical.Value;
        return _rotation % 2 == 1 ? (h, w) : (w, h);
    }

    private async Task<(int, int)> ReadPhysicalSizeAsync()
    {
        var r = await Adb.ShellAsync(_dev.Serial, "wm size", TimeSpan.FromSeconds(10));
        // "Physical size: 1080x2400" (+ maybe "Override size: ..."); the last one wins.
        var matches = Regex.Matches(r.Output, @"(\d+)x(\d+)");
        if (matches.Count > 0)
        {
            var m = matches[^1];
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
        return (_dev.Profile.Device.Width, _dev.Profile.Device.Height);
    }

    /// <returns>True when the orientation changed.</returns>
    private async Task<bool> RefreshRotationAsync()
    {
        _rotationCheckedAt = DateTime.UtcNow;
        var r = await Adb.ShellAsync(_dev.Serial, "dumpsys window displays | grep -m 1 mCurrentRotation", TimeSpan.FromSeconds(10));
        var m = Regex.Match(r.Output, @"mCurrentRotation=(?:ROTATION_)?(\d+)");
        if (!m.Success) return false;
        var v = int.Parse(m.Groups[1].Value);
        var rot = v >= 90 ? v / 90 : v;
        if (rot == _rotation) return false;
        _rotation = rot;
        return true;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshRotationAsync();
                var (w, h) = await GetLogicalSizeAsync();
                // Encode at most 720 px wide (or tall in landscape), multiples of 16 for the encoder.
                var scale = Math.Min(1.0, 720.0 / Math.Min(w, h));
                int vw = (int)(w * scale) / 16 * 16, vh = (int)(h * scale) / 16 * 16;

                var psi = ProcessRunner.CreateStartInfo(AppPaths.Adb, new[]
                {
                    "-s", _dev.Serial, "exec-out", "screenrecord", "--output-format=h264",
                    "--size", $"{vw}x{vh}", "--bit-rate", "4000000", "--time-limit", "180", "-",
                });
                psi.StandardOutputEncoding = null;
                var proc = new Process { StartInfo = psi };
                proc.Start();
                _proc = proc;
                _ = proc.StandardError.ReadToEndAsync();
                var stream = proc.StandardOutput.BaseStream;
                var buffer = new byte[256 * 1024];
                var lastRotationCheck = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    var chunk = buffer[..read];
                    List<StreamClient>? slow = null;
                    lock (_clients)
                    {
                        foreach (var c in _clients)
                            if (!c.SendBinary(chunk)) (slow ??= new()).Add(c);
                        if (slow != null) foreach (var c in slow) _clients.Remove(c);
                    }
                    if (slow != null) foreach (var c in slow) c.Abort();
                    if ((DateTime.UtcNow - lastRotationCheck).TotalSeconds > 2)
                    {
                        lastRotationCheck = DateTime.UtcNow;
                        if (await RefreshRotationAsync()) break; // re-encode at the new orientation
                    }
                }
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info("Screen stream: " + ex.Message);
            }
            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        StopLoop();
        _ = UnfitAsync();
    }
}

/// <summary>
/// A long-lived "adb shell" that input commands are fed to one at a time (much faster than starting adb each time).
/// Commands run strictly in order; consecutive finger moves are merged so a drag never lags behind.
/// </summary>
internal sealed class InputShell : IDisposable
{
    private const string Marker = "__EMULATE_DONE__";
    private readonly RunningDevice _dev;
    private readonly LinkedList<(string cmd, bool move)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Process? _proc;
    private TaskCompletionSource? _done;

    public InputShell(RunningDevice dev)
    {
        _dev = dev;
        _ = Task.Run(WorkerAsync);
    }

    /// <param name="move">Finger-move commands replace an unsent previous move instead of queueing behind it.</param>
    public void Send(string command, bool move = false)
    {
        lock (_queue)
        {
            if (move && _queue.Last is { Value.move: true } last) last.Value = (command, true);
            else
            {
                _queue.AddLast((command, move));
                _signal.Release();
            }
        }
    }

    private async Task WorkerAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_cts.Token); } catch (OperationCanceledException) { return; }
            string cmd;
            lock (_queue)
            {
                if (_queue.First == null) continue;
                cmd = _queue.First.Value.cmd;
                _queue.RemoveFirst();
            }
            try
            {
                EnsureShell();
                var done = _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _proc!.StandardInput.WriteAsync(cmd + "; echo " + Marker + "\n");
                await _proc.StandardInput.FlushAsync();
                await Task.WhenAny(done.Task, Task.Delay(5000, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Info("Input shell: " + ex.Message);
                try { _proc?.Kill(true); } catch { }
                _proc = null;
            }
        }
    }

    private void EnsureShell()
    {
        if (_proc is { HasExited: false }) return;
        var psi = ProcessRunner.CreateStartInfo(AppPaths.Adb, new[] { "-s", _dev.Serial, "shell" });
        psi.RedirectStandardInput = true;
        _proc = Process.Start(psi)!;
        _proc.OutputDataReceived += (_, e) => { if (e.Data?.Contains(Marker) == true) _done?.TrySetResult(); };
        _proc.ErrorDataReceived += (_, _) => { };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _proc?.Kill(true); } catch { }
        _proc = null;
    }
}
