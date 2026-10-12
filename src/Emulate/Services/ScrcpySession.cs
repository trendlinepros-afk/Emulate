using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Emulate.Services;

/// <summary>
/// Runs the scrcpy server (third_party/scrcpy, Apache-2.0) on a device: a low-latency H.264 screen stream plus
/// a control channel that injects touches/keys directly through Android's InputManager (milliseconds, versus the
/// few hundred ms of "adb shell input"). Protocol per scrcpy v5.0.1: raw Annex-B video (no stream/frame meta),
/// big-endian control messages.
/// </summary>
internal sealed class ScrcpySession : IAsyncDisposable
{
    public const string Version = "5.0.1";
    private const string RemoteJar = "/data/local/tmp/emulate-scrcpy-" + Version + ".jar";

    public static string LocalJar => Path.Combine(AppPaths.InstallDir, "scrcpy-server.jar");
    public static bool Available => File.Exists(LocalJar);

    private readonly string _serial;
    private Process? _proc;
    private TcpClient? _video, _control;
    private int _port;
    private readonly object _writeGate = new();

    public NetworkStream VideoStream => _video!.GetStream();

    private ScrcpySession(string serial) => _serial = serial;

    public static async Task<ScrcpySession> StartAsync(string serial, int maxSize, CancellationToken ct)
    {
        var s = new ScrcpySession(serial);
        try
        {
            await s.StartCoreAsync(maxSize, ct);
            return s;
        }
        catch
        {
            await s.DisposeAsync();
            throw;
        }
    }

    private async Task StartCoreAsync(int maxSize, CancellationToken ct)
    {
        // Push the server once per device boot (/data/local/tmp survives reboots, but this is cheap to check).
        var exists = await Adb.ShellAsync(_serial, $"ls {RemoteJar} 2>/dev/null", TimeSpan.FromSeconds(10));
        if (!exists.Output.Contains(RemoteJar))
        {
            var push = await Adb.RunAsync(TimeSpan.FromSeconds(60), "-s", _serial, "push", LocalJar, RemoteJar);
            if (!push.Ok) throw new InvalidOperationException("Couldn't copy the stream server to the device: " + push.Combined);
        }

        var scid = Random.Shared.Next(1, int.MaxValue).ToString("x8");
        var fwd = await Adb.RunAsync(TimeSpan.FromSeconds(15), "-s", _serial, "forward", "tcp:0", $"localabstract:scrcpy_{scid}");
        if (!fwd.Ok || !int.TryParse(fwd.Output.Trim(), out _port)) throw new InvalidOperationException("adb forward failed: " + fwd.Combined);

        var args = new[]
        {
            "-s", _serial, "shell",
            $"CLASSPATH={RemoteJar}", "app_process", "/", "com.genymobile.scrcpy.Server", Version,
            $"scid={scid}", "log_level=warn", "tunnel_forward=true",
            "video=true", "audio=false", "control=true",
            "video_codec=h264", $"max_size={maxSize}", "video_bit_rate=6000000", "max_fps=60",
            "send_device_meta=false", "send_frame_meta=false", "send_stream_meta=false", "send_dummy_byte=true",
            "clipboard_autosync=false", "cleanup=false", "power_on=false",
        };
        var psi = ProcessRunner.CreateStartInfo(AppPaths.Adb, args);
        _proc = new Process { StartInfo = psi };
        _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("scrcpy: " + e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("scrcpy: " + e.Data); };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        // The server needs a moment to start listening. Through "adb forward" a connection succeeds even before
        // that, then closes immediately, so wait until the dummy byte actually arrives.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_proc.HasExited) throw new InvalidOperationException("The stream server exited (see the log).");
            var c = new TcpClient { NoDelay = true };
            try
            {
                await c.ConnectAsync("127.0.0.1", _port, ct);
                var one = new byte[1];
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(1500);
                if (await c.GetStream().ReadAsync(one, readCts.Token) == 1)
                {
                    _video = c;
                    break;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Not ready yet.
            }
            c.Dispose();
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The stream server didn't start.");
            await Task.Delay(150, ct);
        }

        _control = new TcpClient { NoDelay = true };
        await _control.ConnectAsync("127.0.0.1", _port, ct);
        // Drain device -> client messages (clipboard etc.) so the server never blocks on them.
        _ = Task.Run(async () =>
        {
            var buf = new byte[4096];
            try { while (await _control.GetStream().ReadAsync(buf) > 0) { } } catch { }
        });
        Log.Info($"scrcpy {Version} streaming from {_serial} via tcp:{_port}");
    }

    private void Write(byte[] msg)
    {
        lock (_writeGate)
        {
            try { _control?.GetStream().Write(msg); }
            catch (Exception ex) { Log.Info("scrcpy control: " + ex.Message); }
        }
    }

    // ---- Control messages (ControlMessageReader in scrcpy v5.0.1) ----

    public const byte ActionDown = 0, ActionUp = 1, ActionMove = 2;

    /// <summary>Finger touch. x/y are in video-frame pixels; w/h must equal the current video size or scrcpy drops the event.</summary>
    public void Touch(byte action, long pointerId, int x, int y, int w, int h)
    {
        var m = new byte[32];
        m[0] = 2; // TYPE_INJECT_TOUCH_EVENT
        m[1] = action;
        BinaryPrimitives.WriteInt64BigEndian(m.AsSpan(2), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(18), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(20), (ushort)h);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(22), action == ActionUp ? (ushort)0 : (ushort)0xffff); // pressure
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(24), 0); // action button
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(28), 0); // buttons
        Write(m);
    }

    /// <summary>Scroll wheel. v/h are in "notches" (positive vScroll scrolls content up, like wheel-up).</summary>
    public void Scroll(int x, int y, int w, int h, float hScroll, float vScroll)
    {
        static short Fixed(float v) => (short)Math.Clamp((int)Math.Round(v / 16f * 32768f), short.MinValue, short.MaxValue);
        var m = new byte[21];
        m[0] = 3; // TYPE_INJECT_SCROLL_EVENT
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(1), x);
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(5), y);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(9), (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(11), (ushort)h);
        BinaryPrimitives.WriteInt16BigEndian(m.AsSpan(13), Fixed(hScroll));
        BinaryPrimitives.WriteInt16BigEndian(m.AsSpan(15), Fixed(vScroll));
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(17), 0);
        Write(m);
    }

    /// <summary>Key press (down + up).</summary>
    public void Key(int keycode)
    {
        foreach (var action in new byte[] { 0, 1 })
        {
            var m = new byte[14];
            m[0] = 0; // TYPE_INJECT_KEYCODE
            m[1] = action;
            BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(2), keycode);
            BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(6), 0); // repeat
            BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(10), 0); // meta state
            Write(m);
        }
    }

    public void Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Length > 300 ? text[..300] : text);
        var m = new byte[5 + bytes.Length];
        m[0] = 1; // TYPE_INJECT_TEXT
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(1), bytes.Length);
        bytes.CopyTo(m, 5);
        Write(m);
    }

    public void Rotate() => Write(new byte[] { 11 }); // TYPE_ROTATE_DEVICE

    /// <summary>Asks the encoder to restart, which produces a fresh keyframe for a newly connected viewer.</summary>
    public void ResetVideo() => Write(new byte[] { 17 }); // TYPE_RESET_VIDEO

    public async ValueTask DisposeAsync()
    {
        try { _video?.Dispose(); } catch { }
        try { _control?.Dispose(); } catch { }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        if (_port > 0)
        {
            try { await Adb.RunAsync(TimeSpan.FromSeconds(10), "-s", _serial, "forward", "--remove", $"tcp:{_port}"); } catch { }
        }
    }
}
